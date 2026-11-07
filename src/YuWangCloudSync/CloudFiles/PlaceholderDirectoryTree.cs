using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Storage;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Materialize known cloud directories in parent-first order, without creating ordinary local folders.</summary>
public sealed class PlaceholderDirectoryTree
{
    private readonly string _root, _prefix, _user;
    private readonly ulong _container;
    private readonly SqliteCache _cache;
    private readonly Action<string>? _log;

    public PlaceholderDirectoryTree(string root, SqliteCache cache, string user, ulong container,
        Action<string>? log = null)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _prefix = _root + Path.DirectorySeparatorChar;
        _cache = cache;
        _user = user;
        _container = container;
        _log = log;
    }

    public List<(ulong diskId, string localPath)> PrepareVirtualTree(CancellationToken ct = default)
    {
        var disks = new List<(ulong, string)>();
        var seen = new HashSet<(string, ulong)>();
        var queue = new Queue<(ulong, string)>();
        queue.Enqueue((_container, _root));
        while (queue.TryDequeue(out var parent))
        {
            ct.ThrowIfCancellationRequested();
            var children = _cache.ListChildNodes(_user, parent.Item1);
            foreach (var node in children)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add((node.NodeType, node.Id)))
                {
                    _log?.Invoke($"[TREE] 跳过重复或循环的磁盘树节点 id={node.Id}");
                    continue;
                }
                var path = ChildPath(parent.Item2, node.LocalName);
                try
                {
                    if (!Directory.Exists(path)) PlaceholderCreator.CreateVirtualBatch(parent.Item2, [node]);
                    var expected = node.IsDisk ? FileIdentity.ForDisk(node.Id) : FileIdentity.ForContainer(node.Id);
                    RequireIdentity(path, expected);
                    if (node.IsDisk)
                    {
                        _cache.SaveSubFolder(node.Id, 0, 0, node.LocalName, path);
                        disks.Add((node.Id, path));
                    }
                    else queue.Enqueue((node.Id, path));
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _log?.Invoke($"[TREE] 目录层级尚未准备好，暂缓该分支 path={path} reason={error.Message}");
                }
            }
        }
        return disks;
    }

    public void EnsureDirectory(ulong disk, ulong folder, string path, CancellationToken ct = default)
        => EnsureDirectory(disk, folder, Path.GetFullPath(path), new HashSet<(ulong, ulong)>(), ct);

    public List<(ulong diskId, string localPath)> PrepareCachedTree(CancellationToken ct = default)
    {
        var disks = PrepareVirtualTree(ct);
        var visited = new HashSet<(ulong, ulong)>();
        var queue = new Queue<(ulong disk, ulong folder, string path)>(disks.Select(d => (d.diskId, 0UL, d.localPath)));
        var created = 0;
        while (queue.TryDequeue(out var node))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add((node.disk, node.folder))) continue;
            try
            {
                var items = _cache.ListCachedItems(node.disk, node.folder);
                created += PopulateDirectory(node.disk, node.folder, node.path, items, ct);
                foreach (var child in items.Where(i => i.isFolder))
                {
                    var path = ChildPath(node.path, child.name);
                    _cache.SaveSubFolder(node.disk, child.id, node.folder, child.name, path);
                    queue.Enqueue((node.disk, child.id, path));
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log?.Invoke($"[TREE] 暂缓缓存占位分支 path={node.path} reason={error.Message}");
            }
        }
        _log?.Invoke($"[TREE] 本地占位树已预建 disks={disks.Count} folders={visited.Count} created={created}（不下载文件内容）");
        return disks;
    }

    private void EnsureDirectory(ulong disk, ulong folder, string path, HashSet<(ulong, ulong)> visiting,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("拒绝在同步根之外补建目录占位。");
        if (!visiting.Add((disk, folder))) throw new IOException("缓存中的目录父子关系成环。");
        if (Callbacks.IsWriteMutationPending(path)) throw new IOException("目录正在移动，稍后补建占位。");
        if (Directory.Exists(path))
        {
            RequireIdentity(path, new FileIdentity(disk, 0, folder, 0), checkParent: false);
            return;
        }
        if (folder == 0)
        {
            // A disk may have disappeared since the first tree preparation. Repair its
            // virtual ancestors before retrying; never create a plain Directory.CreateDirectory.
            if (!PrepareVirtualTree(ct).Any(d => d.diskId == disk
                && d.localPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new DirectoryNotFoundException($"磁盘占位路径尚未准备好: {path}");
            return;
        }
        var restore = _cache.GetPlaceholderRestoreInfo(new FileIdentity(disk, 0, folder, 0))
            ?? throw new DirectoryNotFoundException($"缺少目录占位的父级缓存 disk={disk} folder={folder}");
        if (!ChildPath(restore.ParentPath, restore.Name).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("目录缓存路径已改变，停止在旧路径补建占位。");
        var parent = _cache.TryGetFolderByLocalPath(restore.ParentPath)
            ?? throw new DirectoryNotFoundException("缺少父目录的云端映射。");
        if (parent.diskId != disk) throw new IOException("父目录磁盘映射不一致。");
        EnsureDirectory(disk, parent.folderId, restore.ParentPath, visiting, ct);
        PlaceholderCreator.CreateBatch(restore.ParentPath, disk, parent.folderId,
            [(folder, restore.Name, true, null, restore.UploadTime)]);
        RequireIdentity(path, new FileIdentity(disk, parent.folderId, folder, 0));
    }

    public int PopulateDirectory(ulong disk, ulong folder, string path,
        List<(ulong id, string name, bool isFolder, long? size, DateTime? uploadTime)> items,
        CancellationToken ct = default)
    {
        EnsureDirectory(disk, folder, path, ct);
        foreach (var item in items) ChildPath(path, item.name);
        var permission = _cache.GetDirectoryPermission(_user, disk, folder);
        var count = PlaceholderCreator.CreateBatch(path, disk, folder, items, readOnly: permission != DirectoryPermission.FullAccess);
        if (count > 0)
        {
            Callbacks.InvalidateFolderListing(disk, folder);
            ExplorerStateNotifier.NotifyDirectory(path);
        }
        return count;
    }

    private string ChildPath(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("云端条目名称不是合法的本地目录项名称。");
        var path = Path.GetFullPath(Path.Combine(parent, name));
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("云端条目路径越出了同步根。");
        return path;
    }

    private static void RequireIdentity(string path, FileIdentity expected, bool checkParent = true)
    {
        if (!Directory.Exists(path) || !ProviderIdentity.TryReadPlaceholderIdentity(path, out var actual)
            || actual.Type != 0 || actual.DiskId != expected.DiskId || actual.FileId != expected.FileId
            || (checkParent && actual.FolderId != expected.FolderId))
            throw new IOException($"该路径不是预期的云端目录占位，保留已有内容: {path}");
    }
}
