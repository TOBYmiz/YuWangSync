using System.Collections.Concurrent;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Storage;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Periodically refresh only the account's explicitly kept directories.</summary>
internal sealed class DirectoryPopulationRefresh : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly Func<FileIdentity, string, bool> _refreshDirectory;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private volatile bool _disposed;

    private sealed record Entry(FileIdentity Identity, long DueAt, bool Refreshing);

    public DirectoryPopulationRefresh(SqliteCache cache, string root, string userKey,
        Func<TimeSpan> refreshInterval, Func<FileIdentity, string, bool> refreshDirectory,
        Action<string>? log)
    {
        _refreshInterval = refreshInterval;
        _refreshDirectory = refreshDirectory;
        _log = log;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dueAt = Environment.TickCount64 + (long)refreshInterval().TotalMilliseconds;
        foreach (var (diskId, folderId, path, _) in cache.ListKeptDirectoryRefreshes(userKey))
        {
            try
            {
                if (Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _directories[path] = new Entry(
                        new FileIdentity(diskId, cache.GetFolderParentId(diskId, folderId), folderId, 0),
                        dueAt, false);
            }
            catch { /* Stale paths will be pruned on the next timer pass. */ }
        }
        _timer = new Timer(RefreshDueDirectories, null, 250, 250);
    }

    public void Schedule(FileIdentity identity, string path)
    {
        if (!identity.IsValid || identity.IsVirtual || identity.Type != 0 || _disposed) return;
        lock (_gate)
        {
            if (_disposed) return;
            var refreshing = false;
            foreach (var (existingPath, entry) in _directories)
            {
                if (!SameDirectory(entry.Identity, identity)) continue;
                refreshing |= entry.Refreshing;
                _directories.TryRemove(existingPath, out _);
            }
            _directories[path] = new Entry(identity,
                Environment.TickCount64 + (long)_refreshInterval().TotalMilliseconds, refreshing);
        }
    }

    public void Unschedule(FileIdentity identity)
    {
        lock (_gate)
        {
            foreach (var (path, entry) in _directories)
                if (SameDirectory(entry.Identity, identity)) _directories.TryRemove(path, out _);
        }
    }

    public void Forget(FileIdentity identity, string path)
    {
        lock (_gate)
        {
            if (_directories.TryGetValue(path, out var entry) && SameDirectory(entry.Identity, identity))
                _directories.TryRemove(path, out _);
        }
    }

    private static bool SameDirectory(FileIdentity left, FileIdentity right)
        => left.DiskId == right.DiskId && left.FileId == right.FileId;

    private void RefreshDueDirectories(object? state)
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed) return;
            var now = Environment.TickCount64;
            foreach (var (path, entry) in _directories)
            {
                if (entry.Refreshing || entry.DueAt > now) continue;
                if (!Directory.Exists(path)
                    || !ProviderIdentity.TryReadPlaceholderIdentity(path, out var actual)
                    || actual.Type != 0
                    || actual.DiskId != entry.Identity.DiskId
                    || actual.FileId != entry.Identity.FileId)
                {
                    _directories.TryRemove(path, out _);
                    _log?.Invoke($"[REFRESH] 删除已失效的保持刷新目录记录: {path}");
                    continue;
                }

                var refreshing = entry with { Refreshing = true };
                if (!_directories.TryUpdate(path, refreshing, entry)) continue;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var succeeded = false;
                    try { succeeded = _refreshDirectory(entry.Identity, path); }
                    catch (Exception error) { _log?.Invoke($"[REFRESH] 保持目录刷新异常: {path}: {error.Message}"); }
                    finally
                    {
                        lock (_gate)
                        {
                            if (!_disposed)
                            {
                                var current = _directories.FirstOrDefault(pair =>
                                    SameDirectory(pair.Value.Identity, entry.Identity));
                                if (current.Key is not null)
                                {
                                    var interval = _refreshInterval();
                                    var retry = succeeded ? interval : TimeSpan.FromSeconds(
                                        Math.Min(60, Math.Max(5, interval.TotalSeconds / 2)));
                                    _directories[current.Key] = new Entry(current.Value.Identity,
                                        Environment.TickCount64 + (long)retry.TotalMilliseconds, false);
                                }
                            }
                        }
                        _log?.Invoke(succeeded
                            ? $"[REFRESH] 保持目录下次刷新已排程，间隔 {_refreshInterval().TotalMinutes:F0} 分钟: {path}"
                            : $"[REFRESH] 保持目录刷新失败，将稍后重试: {path}");
                    }
                });
            }
        }
        catch (Exception error) { _log?.Invoke($"[REFRESH] 定时扫描保持刷新目录失败: {error.Message}"); }
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
