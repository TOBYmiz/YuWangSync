using YuWangCloudSync.Models;

namespace YuWangCloudSync.CloudFiles;

// Keep real failures separate from placeholder availability. One successful FETCH_DATA range
// does not mean the whole file has recovered; wait for a full availability probe or hydration.
internal sealed class SyncRootDownloadFailures
{
    private const int Capacity = 4096;
    private readonly Dictionary<(ulong Disk, ulong Folder, ulong File), long> _failures = [];
    private long _version;

    internal long Observe(FileIdentity item)
    {
        lock (_failures) return _failures.GetValueOrDefault((item.DiskId, item.FolderId, item.FileId));
    }

    internal void Record(FileIdentity item)
    {
        if (!item.IsValid || item.IsVirtual || item.Type != 1) return;
        lock (_failures)
        {
            var key = (item.DiskId, item.FolderId, item.FileId);
            if (_failures.Count == Capacity && !_failures.ContainsKey(key)) _failures.Remove(_failures.Keys.First());
            _failures[key] = ++_version;
        }
    }

    internal bool HasFailure(FileIdentity item, SyncRootAvailability availability, long observed)
    {
        lock (_failures)
        {
            var key = (item.DiskId, item.FolderId, item.FileId);
            if (!_failures.TryGetValue(key, out var failure)) return false;
            if (failure != observed || availability is not (SyncRootAvailability.Local or SyncRootAvailability.Pinned))
                return true;
            _failures.Remove(key);
            return false;
        }
    }
}
