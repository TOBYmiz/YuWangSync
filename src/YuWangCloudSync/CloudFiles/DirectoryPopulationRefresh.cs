using System.Collections.Concurrent;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Storage;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// Complete each native enumeration with population disabled, then arm the next
/// enumeration outside the callback after the configured refresh interval. Known
/// directories get one initial arm shortly after startup. Directory opens do not
/// produce CFAPI open notifications, and leaving population enabled in the receipt
/// causes repeated fetches within the same query, even when there are no remaining
/// placeholders to transfer.
/// </summary>
internal sealed class DirectoryPopulationRefresh : IDisposable
{
    private static readonly TimeSpan InitialArmDelay = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, Entry> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private volatile bool _disposed;

    private sealed record Entry(FileIdentity Identity, long ScheduledAt, long RetryAt, bool Armed, bool Initial);

    public DirectoryPopulationRefresh(SqliteCache cache, string root, Func<TimeSpan> refreshInterval,
        Action<string>? log)
    {
        _refreshInterval = refreshInterval;
        _log = log;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (identity, path) in cache.ListKnownFolderPaths())
            if (Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                ScheduleInitial(identity, path);
        _timer = new Timer(ArmDirectories, null, 250, 250);
    }

    private void ScheduleInitial(FileIdentity identity, string path)
        => SetSchedule(identity, path, initial: true);

    public void Schedule(FileIdentity identity, string path)
        => SetSchedule(identity, path, initial: false);

    private void SetSchedule(FileIdentity identity, string path, bool initial)
    {
        if (identity.IsVirtual) return;
        if (!_disposed)
            _directories[path] = new Entry(identity, Environment.TickCount64, 0, false, initial);
    }

    public bool BeginEnumeration(FileIdentity identity, string path)
    {
        // Take the force-refresh marker once. Concurrent callbacks for the same query
        // can reuse the listing; the production callback serializes them by directory.
        if (_directories.TryRemove(path, out var entry))
            return entry.Armed && entry.Identity.DiskId == identity.DiskId
                && entry.Identity.FileId == identity.FileId;
        return false;
    }

    private void ArmDirectories(object? state)
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed) return;
            foreach (var (path, entry) in _directories)
            {
                var now = Environment.TickCount64;
                var delay = entry.Initial ? InitialArmDelay : _refreshInterval();
                if (!entry.Initial && delay < TimeSpan.FromMinutes(1))
                    delay = TimeSpan.FromMinutes(1);
                var due = entry.RetryAt > 0
                    ? entry.RetryAt
                    : entry.ScheduledAt + (long)delay.TotalMilliseconds;
                if (entry.Armed || due > now) continue;
                if (!Directory.Exists(path))
                {
                    _directories.TryRemove(path, out _);
                    continue;
                }
                if (!ProviderIdentity.TryReadPlaceholderIdentity(path, out var actual)
                    || actual.DiskId != entry.Identity.DiskId || actual.FileId != entry.Identity.FileId)
                {
                    _directories.TryRemove(path, out _);
                    continue;
                }
                // Publish first: enabling population can immediately release a waiting
                // application query. Do not overwrite an entry scheduled by its callback.
                var armed = entry with { Armed = true, RetryAt = 0 };
                if (!_directories.TryUpdate(path, armed, entry)) continue;
                if (!PlaceholderUpdater.TryEnableOnDemandPopulation(path, _log))
                    _directories.TryUpdate(path, armed with
                    {
                        Armed = false,
                        RetryAt = Environment.TickCount64 + 5000
                    }, armed);
            }
        }
        catch (Exception error) { _log?.Invoke($"[REFRESH] 重开目录枚举失败: {error.Message}"); }
        finally { Monitor.Exit(_gate); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _directories.Clear();
        }
    }
}
