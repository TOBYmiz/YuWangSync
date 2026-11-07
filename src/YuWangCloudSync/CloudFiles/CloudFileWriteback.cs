using System.Collections.Concurrent;
using System.Security.Cryptography;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangCloudSync.Storage;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// CFAPI close notifications cover placeholders; ordinary new files require a directory watcher.
/// Both feed one serial queue. Every write rechecks the destination permission and preserves local
/// bytes on failure. Durable receipts close the gap between remote completion and local binding.
/// </summary>
internal sealed class CloudFileWriteback : IDisposable
{
    private readonly string _root, _prefix, _user;
    private readonly YuWangClient _client;
    private readonly SqliteCache _cache;
    private readonly Action<string>? _log;
    private readonly FileSystemWatcher _watcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastFailure = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;

    public CloudFileWriteback(string root, string user, YuWangClient client, SqliteCache cache, Action<string>? log)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _prefix = _root + Path.DirectorySeparatorChar;
        _user = user;
        _client = client;
        _cache = cache;
        _log = log;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        _watcher.Error += (_, _) => { _log?.Invoke("[WRITE] 目录监视溢出，将重新扫描本地待上传内容。"); Discover(_root); };
        _watcher.EnableRaisingEvents = true;
        _worker = Task.Run(RunAsync);
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Enqueue(args.FullPath);

    public void Enqueue(string path)
    {
        if (_stop.IsCancellationRequested || string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        if (path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            _pending[path] = Environment.TickCount64 + 2000;
    }

    private void Discover(string path)
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if (_stop.IsCancellationRequested) break;
                var attributes = File.GetAttributes(child);
                var owned = ProviderIdentity.TryReadPlaceholderIdentity(child, out _);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !owned) continue;
                if (IsTemporary(child, attributes)) continue;
                _pending.TryAdd(child, Environment.TickCount64 + 2000);
                if ((attributes & FileAttributes.Directory) != 0) Discover(child);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task RunAsync()
    {
        long nextScan = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Environment.TickCount64 >= nextScan)
                {
                    Discover(_root);
                    nextScan = Environment.TickCount64 + 30_000;
                }
                foreach (var entry in _pending.OrderBy(pair => pair.Key.Length))
                {
                    if (entry.Value > Environment.TickCount64) continue;
                    if (!((ICollection<KeyValuePair<string, long>>)_pending).Remove(entry)) continue;
                    _stop.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await ProcessAsync(entry.Key).ConfigureAwait(false);
                        _lastFailure.TryRemove(entry.Key, out _);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        // Do not log HttpRequestException.Message: upload URLs contain tokens.
                        var reason = error is HttpRequestException ? "HTTP 请求失败" : error is IOException or UnauthorizedAccessException
                            ? error.Message : error.GetType().Name;
                        if (!_lastFailure.TryGetValue(entry.Key, out var previous) || previous != reason)
                            _log?.Invoke($"[WRITE] 保留本地内容，写回未完成 path={entry.Key} reason={reason}");
                        _lastFailure[entry.Key] = reason;
                        _pending.TryAdd(entry.Key, Environment.TickCount64 + 30_000);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private static bool IsTemporary(string path, FileAttributes attributes) =>
        (attributes & FileAttributes.Temporary) != 0 || Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    private void ValidateScope(string path)
    {
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("路径不在同步目录内。");
        for (var current = Path.GetDirectoryName(path); current is not null && current.Length > _root.Length;
             current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
                && !ProviderIdentity.OwnsPlaceholder(current))
                throw new IOException("不沿目录链接上传同步根外的内容。");
    }

    private async Task ProcessAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        ValidateScope(path);
        if (Callbacks.IsWriteMutationPending(path)) return;
        var attributes = File.GetAttributes(path);
        if (IsTemporary(path, attributes)) return;
        var owned = ProviderIdentity.TryReadPlaceholderIdentity(path, out var identity);
        if ((attributes & FileAttributes.ReparsePoint) != 0 && !owned) return;
        var directory = (attributes & FileAttributes.Directory) != 0;
        if (owned && directory && !identity.IsVirtual && identity.FileId != 0)
            _cache.CompleteBoundWriteback(_user, path, identity.FileId);
        if (owned && (identity.IsVirtual || directory)) return;
        if (owned)
        {
            if (!PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state)) return;
            if (state.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
            {
                _cache.CompleteBoundWriteback(_user, path, identity.FileId);
                return;
            }
        }
        // A hydration close is never an upload request. Dirty partially hydrated content must
        // first finish hydration; reading it here could otherwise recursively enter FETCH_DATA.
        if (owned && !Callbacks.IsHydrationQuiet(identity.FileId, 2000))
            throw new IOException("文件仍在水化，稍后重试写回。");
        var parentPath = Path.GetDirectoryName(path)!;
        var parent = _cache.TryGetFolderByLocalPath(parentPath);
        if (parent is null && parentPath.Length > _root.Length)
        {
            await ProcessAsync(parentPath).ConfigureAwait(false);
            parent = _cache.TryGetFolderByLocalPath(parentPath);
        }
        if (parent is null || parent.Value.diskId == 0) return;
        if (owned && (identity.DiskId != parent.Value.diskId || identity.FolderId != parent.Value.folderId))
            throw new IOException("占位身份与当前父目录不一致，等待移动回调完成。");
        var (disk, folder) = parent.Value;
        var listing = await _client.ListFolderWithPermissionsAsync(disk, folder).ConfigureAwait(false);
        _cache.SaveDirectoryPermission(_user, disk, folder, listing.Permission);
        if (!listing.IsValidPage)
            throw new UnauthorizedAccessException($"目录响应无效，写入权限未知 disk={disk} folder={folder}。");
        if (listing.Permission == DirectoryPermission.ReadOnly)
            throw new UnauthorizedAccessException($"当前账号对该目录只有只读权限，不能上传本地内容 disk={disk} folder={folder} permission=ReadOnly。");
        if (listing.Permission != DirectoryPermission.FullAccess)
            throw new UnauthorizedAccessException($"当前目录写入权限未知，等待重新探测 disk={disk} folder={folder} permission={listing.Permission}。");
        _stop.Token.ThrowIfCancellationRequested();
        var name = Path.GetFileName(path);
        if (directory)
        {
            var receipt = _cache.GetWritebackReceipt(_user, path, "directory");
            YuWangItem created;
            if (receipt is { } saved)
            {
                if (saved.Disk != disk || saved.Folder != folder || saved.Id == 0)
                    throw new IOException("目录创建结果尚未确认，停止重复创建，请核对云端目录。");
                created = await _client.CreateRemoteFolderAsync(disk, folder, name, _stop.Token, saved.Id).ConfigureAwait(false);
            }
            else
            {
                if (listing.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("云端已存在同名目录，停止自动合并。");
                _cache.SaveWritebackReceipt(_user, path, "directory", disk, folder, name, 0);
                created = await _client.CreateRemoteFolderAsync(disk, folder, name, _stop.Token,
                    rememberCreated: id => _cache.SaveWritebackReceipt(_user, path, "directory", disk, folder, name, id)).ConfigureAwait(false);
            }
            _cache.SaveSubFolder(disk, created.Id, folder, name, path);
            try
            {
                var childListing = await _client.ListFolderWithPermissionsAsync(disk, created.Id).ConfigureAwait(false);
                _cache.SaveDirectoryPermission(_user, disk, created.Id, childListing.Permission);
            }
            catch (Exception error)
            {
                _log?.Invoke($"[WRITE] 新建目录权限预热未完成 directory={created.Id} reason={error.GetType().Name}");
            }
            if (!PlaceholderUpdater.TryBindIdentity(path, new(disk, folder, created.Id, 0), true, _log))
                throw new IOException("云端目录已创建，本地身份绑定尚未完成。");
            _cache.RemoveWritebackReceipt(_user, path, "directory");
            Callbacks.InvalidateFolderListing(disk, folder);
            Discover(path);
            _log?.Invoke($"[WRITE] 新建目录已同步 directory={created.Id} path={path}");
            return;
        }

        // Holding this handle denies new writers and deletion for the entire hash/upload/bind
        // transaction. A file still open for writing fails here and is retried after it closes.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var uploadProgress = new UploadProgressReporter(path, file.Length, _log, _stop.Token);
        var modified = File.GetLastWriteTimeUtc(path);
        var hash = await SHA256.HashDataAsync(file, _stop.Token).ConfigureAwait(false);
        var hashText = Convert.ToHexString(hash);
        // Recover a crash after binding the previous uploaded ID. Keep this snapshot's
        // receipt if identity binding succeeded but setting in-sync still needs a retry.
        if (owned) _cache.CompleteBoundWriteback(_user, path, identity.FileId, hashText);
        if (_cache.HasOtherPendingWriteback(_user, path, hashText))
            throw new IOException("此前写回尚未完成且本地内容已改变，保留本地文件和云端备份等待核对。");
        // A native rename clears in-sync even when the data stream is unchanged. The
        // namespace callback has verified the same cloud ID/name/version at this path;
        // reuse its last verified content hash instead of uploading the same bytes again.
        var verifiedHash = owned ? _cache.GetContentHash(identity.FileId) : null;
        if (verifiedHash is not null && CryptographicOperations.FixedTimeEquals(hash, verifiedHash)
            && listing.Items.Any(i => !i.IsFolder && i.Id == identity.FileId && i.Name == name
                && i.UploadTime is not null && i.UploadTime.Value.ToString("yyyy-MM-dd HH:mm:ss")
                    == _cache.GetFileDownloadInfo(identity.FileId).UploadTime))
        {
            if (!PlaceholderUpdater.TryBindIdentity(path, identity, true, _log))
                throw new IOException("内容与云端校验记录一致，但同步状态尚未更新。");
            _cache.CompleteBoundWriteback(_user, path, identity.FileId);
            uploadProgress.Complete();
            return;
        }
        var existingReceipt = _cache.GetWritebackReceipt(_user, path, hashText);
        if (existingReceipt is { } prior && (prior.Disk != disk || prior.Folder != folder))
            throw new IOException("待恢复写回记录的目标目录已改变。");
        var collisions = listing.Items.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (collisions.Length > 1 || collisions.Any(i => i.IsFolder)) throw new IOException("云端存在同名冲突。");
        var oldId = existingReceipt?.OldId ?? (owned ? identity.FileId : collisions.SingleOrDefault()?.Id ?? 0);
        var backupName = existingReceipt?.Name ?? name;
        if (existingReceipt is null && oldId != 0)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var tail = "_back";
            extension = extension[..Math.Min(extension.Length, 255 - tail.Length)];
            backupName = stem[..Math.Min(stem.Length, 255 - extension.Length - tail.Length)] + tail + extension;
            for (var suffix = 1; listing.Items.Any(i => i.Name.Equals(backupName, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                tail = "_back_" + suffix;
                extension = extension[..Math.Min(extension.Length, 255 - tail.Length)];
                backupName = stem[..Math.Min(stem.Length, 255 - extension.Length - tail.Length)] + tail + extension;
            }
        }
        _cache.SaveWritebackReceipt(_user, path, hashText, disk, folder, backupName, existingReceipt?.Id ?? 0, oldId);
        var attempt = _cache.GetUploadAttempt(_user, path, hashText);
        void RememberAttempt(UploadAttempt value) => _cache.SaveUploadAttempt(_user, path, hashText, value);
        YuWangItem remote;
        if (oldId != 0)
        {
            remote = await _client.ReplaceRemoteFileAsync(disk, folder, oldId, name, backupName, file, modified, hash,
                id => _cache.SaveWritebackReceipt(_user, path, hashText, disk, folder, backupName, id), _stop.Token, uploadProgress.Report,
                attempt, RememberAttempt).ConfigureAwait(false);
        }
        else
        {
            remote = await _client.UploadNewFileAsync(disk, folder, name, file, modified, hash, _stop.Token, uploadProgress.Report,
                attempt, RememberAttempt).ConfigureAwait(false);
            _cache.SaveWritebackReceipt(_user, path, hashText, disk, folder, backupName, remote.Id);
        }
        _cache.SaveFileMetadata(disk, folder, remote.Id, name, remote.UploadTime?.ToString("yyyy-MM-dd HH:mm:ss"), file.Length);
        _cache.SaveContentHash(remote.Id, hash);
        if (file.Length == 0)
            _cache.SaveDirectInfo(remote.Id, 0, "", remote.UploadTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
        else _cache.SetRealSize(remote.Id, file.Length);
        if (owned && identity.FileId != remote.Id) _cache.RemoveCachedItem(identity);
        if (!PlaceholderUpdater.TryBindIdentity(path, new(disk, folder, remote.Id, 1), true, _log))
            throw new IOException("文件已上传并校验，本地身份绑定尚未完成。");
        _cache.RemoveWritebackReceipt(_user, path, hashText);
        _cache.MarkHydrated(remote.Id);
        _cache.InvalidateFolderListing(disk, folder);
        Callbacks.InvalidateFolderListing(disk, folder);
        ExplorerStateNotifier.NotifyItem(path);
        uploadProgress.Complete();
        _log?.Invoke($"[WRITE] 文件上传并校验完成 file={remote.Id} bytes={file.Length} path={path}");
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _stop.Cancel();
        try { _worker.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }
}
