using System.Collections.Concurrent;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Storage;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// Complete each native enumeration with population disabled, then arm the next
/// enumeration outside the callback. Directory opens do not produce CFAPI open
/// notifications. Leaving population enabled in the receipt causes repeated fetches
/// within the same query, even when there are no remaining placeholders to transfer.
/// </summary>
internal sealed class DirectoryPopulationRefresh : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private volatile bool _disposed;

    private sealed record Entry(FileIdentity Identity, long Due, bool Armed);

    public DirectoryPopulationRefresh(SqliteCache cache, string root, Action<string>? log)
    {
        _log = log;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (identity, path) in cache.ListKnownFolderPaths())
            if (Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Schedule(identity, path);
        _timer = new Timer(ArmDirectories, null, 250, 250);
    }

    public void Schedule(FileIdentity identity, string path)
    {
        if (identity.IsVirtual) return;
        if (!_disposed)
            _directories[path] = new Entry(identity, Environment.TickCount64 + 1000, false);
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
                if (entry.Armed || entry.Due > Environment.TickCount64) continue;
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
                var armed = entry with { Armed = true };
                if (!_directories.TryUpdate(path, armed, entry)) continue;
                if (!PlaceholderUpdater.TryEnableOnDemandPopulation(path, _log))
                    _directories.TryUpdate(path, entry with { Due = Environment.TickCount64 + 5000 }, armed);
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
