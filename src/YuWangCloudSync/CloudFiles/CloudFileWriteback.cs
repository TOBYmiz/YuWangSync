using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangCloudSync.Storage;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// CFAPI close notifications cover placeholders; ordinary new files require a directory watcher.
/// Both feed per-path debounce and exclusive-open confirmation, followed by one serial upload queue.
/// Every write rechecks destination permission and preserves local bytes on failure. Durable receipts
/// close the gap between remote completion and local binding.
/// </summary>
internal sealed class CloudFileWriteback : IDisposable
{
    private const long DebounceMilliseconds = 1_500;
    private const long MaximumConfirmationWaitMilliseconds = 60_000;
    private const int MaximumPendingEntries = 10_000;
    private const int ConfirmationConcurrency = 12;
    private const int MaximumScanQueueCapacity = 1_000;
    private const long MinimumFullScanIntervalMilliseconds = 30_000;
    private const long PeriodicFullScanIntervalMilliseconds = 5 * 60_000;
    private static readonly int[] BusyRetryDelaysMilliseconds = [2_000, 4_000, 8_000, 16_000, 30_000];

    private readonly string _root, _prefix, _user;
    private readonly YuWangClient _client;
    private readonly SqliteCache _cache;
    private readonly Action<string>? _log;
    private readonly FileSystemWatcher _watcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastFailure = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _confirming = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _readyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _needsReconciliation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Task> _confirmationTasks = new();
    private readonly SemaphoreSlim _confirmationSlots = new(ConfirmationConcurrency, ConfirmationConcurrency);
    private readonly Channel<ConfirmedPath> _readyQueue = Channel.CreateBounded<ConfirmedPath>(
        new BoundedChannelOptions(MaximumScanQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly AutoResetEvent _scanSignal = new(false);
    private readonly Thread _scanThread;
    private readonly Task _scheduler;
    private readonly Task _uploadWorker;
    private int _scanRequested;
    private int _pendingLimitWarning;
    private long _confirmationTaskId;

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
            Filter = "*.*",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 65_536,
            EnableRaisingEvents = false
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        _scanThread = new Thread(ScanLoop)
        {
            IsBackground = true,
            Name = "YuWang writeback directory scan",
            Priority = ThreadPriority.BelowNormal
        };
        _scanThread.Start();
        _scheduler = Task.Run(RunSchedulerAsync);
        _uploadWorker = Task.Run(RunUploadWorkerAsync);
        RequestFullScan();
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Enqueue(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args) => Enqueue(args.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _log?.Invoke($"[WRITE] 目录监视器异常 ({args.GetException().GetType().Name})，请求全量扫描。");
        RequestFullScan();
    }

    public void Enqueue(string path)
    {
        if (_stop.IsCancellationRequested || string.IsNullOrWhiteSpace(path)) return;
        try { path = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) || IsTemporaryPath(path)) return;

        SchedulePending(path, fromScan: false);
    }

    private void SchedulePending(string path, bool fromScan)
    {
        if (fromScan)
        {
            var stopHandle = _stop.Token.WaitHandle;
            while (!_pending.ContainsKey(path)
                   && _pending.Count >= MaximumPendingEntries && !_stop.IsCancellationRequested)
            {
                if (stopHandle.WaitOne(100)) return;
            }
        }
        if (_stop.IsCancellationRequested) return;

        var firstObserved = Environment.TickCount64;
        _pending.AddOrUpdate(path,
            _ => new PendingChange(firstObserved, firstObserved, firstObserved + DebounceMilliseconds, 0),
            (_, current) =>
            {
                var now = Environment.TickCount64;
                var due = now + DebounceMilliseconds;
                if (current.BusyRetries > 0) due = Math.Max(due, current.DueTick);
                return current with { LastEventTick = now, DueTick = due };
            });
    }

    private void EnforcePendingLimit()
    {
        var count = _pending.Count;
        if (count <= MaximumPendingEntries)
        {
            if (count < MaximumPendingEntries * 3 / 4)
                Interlocked.Exchange(ref _pendingLimitWarning, 0);
            return;
        }

        if (Interlocked.Exchange(ref _pendingLimitWarning, 1) == 0)
            _log?.Invoke($"[WRITE] 待处理路径超过 {MaximumPendingEntries} 条，清理最久未更新项并请求全量扫描。");
        RequestFullScan();

        var removeCount = count - MaximumPendingEntries;
        foreach (var entry in _pending.OrderBy(pair => pair.Value.LastEventTick).Take(removeCount))
        {
            ((ICollection<KeyValuePair<string, PendingChange>>)_pending).Remove(entry);
        }
    }

    private void RequestFullScan()
    {
        if (_stop.IsCancellationRequested) return;
        Interlocked.Exchange(ref _scanRequested, 1);
        try { _scanSignal.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void ScanLoop()
    {
        var stopHandle = _stop.Token.WaitHandle;
        var waitHandles = new WaitHandle[] { _scanSignal, stopHandle };
        var lastScan = long.MinValue;
        var nextPeriodicScan = Environment.TickCount64 + PeriodicFullScanIntervalMilliseconds;

        while (!_stop.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            if (now >= nextPeriodicScan) Interlocked.Exchange(ref _scanRequested, 1);

            if (Volatile.Read(ref _scanRequested) == 0)
            {
                var waitMilliseconds = (int)Math.Clamp(nextPeriodicScan - now, 1, 30_000);
                if (WaitHandle.WaitAny(waitHandles, waitMilliseconds) == 1) return;
                continue;
            }

            now = Environment.TickCount64;
            if (lastScan != long.MinValue && now - lastScan < MinimumFullScanIntervalMilliseconds)
            {
                var waitMilliseconds = (int)Math.Clamp(
                    MinimumFullScanIntervalMilliseconds - (now - lastScan), 1, 30_000);
                if (WaitHandle.WaitAny(waitHandles, waitMilliseconds) == 1) return;
                continue;
            }

            if (Interlocked.Exchange(ref _scanRequested, 0) == 0) continue;
            try
            {
                Discover(_root, waitForCapacity: true);
                foreach (var path in _needsReconciliation.Keys)
                {
                    if (File.Exists(path) || Directory.Exists(path)) SchedulePending(path, fromScan: true);
                    _needsReconciliation.TryRemove(path, out _);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"[WRITE] 全量扫描未完成 ({error.GetType().Name})。");
            }
            finally
            {
                lastScan = Environment.TickCount64;
                nextPeriodicScan = lastScan + PeriodicFullScanIntervalMilliseconds;
            }
        }
    }

    private void Discover(string path, bool waitForCapacity = false)
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if (_stop.IsCancellationRequested) break;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var owned = ProviderIdentity.TryReadPlaceholderIdentity(child, out _);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !owned) continue;
                if (IsTemporary(child, attributes)) continue;
                SchedulePending(child, fromScan: waitForCapacity);
                _needsReconciliation.TryRemove(child, out _);
                if ((attributes & FileAttributes.Directory) != 0) Discover(child, waitForCapacity);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task RunSchedulerAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                EnforcePendingLimit();
                var now = Environment.TickCount64;
                foreach (var entry in _pending)
                {
                    if (_stop.IsCancellationRequested) break;
                    if (now - entry.Value.FirstEventTick >= MaximumConfirmationWaitMilliseconds)
                    {
                        if (!((ICollection<KeyValuePair<string, PendingChange>>)_pending).Remove(entry)) continue;
                        _needsReconciliation.TryAdd(entry.Key, 0);
                        _log?.Invoke($"[WRITE] 独占确认等待超过 60 秒，移交全量扫描 path={entry.Key}");
                        RequestFullScan();
                        continue;
                    }

                    if (entry.Value.DueTick > now
                        || _confirming.ContainsKey(entry.Key)
                        || _readyPaths.ContainsKey(entry.Key)) continue;
                    if (!_confirmationSlots.Wait(0)) break;
                    if (!_confirming.TryAdd(entry.Key, 0))
                    {
                        _confirmationSlots.Release();
                        continue;
                    }
                    if (!((ICollection<KeyValuePair<string, PendingChange>>)_pending).Remove(entry))
                    {
                        _confirming.TryRemove(entry.Key, out _);
                        _confirmationSlots.Release();
                        continue;
                    }
                    StartConfirmation(entry.Key, entry.Value);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void StartConfirmation(string path, PendingChange pending)
    {
        var id = Interlocked.Increment(ref _confirmationTaskId);
        var task = Task.Run(() => ConfirmAndQueueAsync(path, pending));
        _confirmationTasks[id] = task;
        _ = task.ContinueWith(completed =>
        {
            _confirmationTasks.TryRemove(id, out _);
        },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ConfirmAndQueueAsync(string path, PendingChange pending)
    {
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (!CanConfirmWriteback(path))
            {
                _lastFailure.TryRemove(path, out _);
                return;
            }

            if (!Directory.Exists(path))
            {
                using var exclusive = OpenExclusiveRead(path);
            }

            if (!_readyPaths.TryAdd(path, 0)) return;
            try
            {
                var now = Environment.TickCount64;
                var confirmed = pending with
                {
                    FirstEventTick = now,
                    LastEventTick = now,
                    DueTick = now,
                    BusyRetries = 0
                };
                await _readyQueue.Writer.WriteAsync(new ConfirmedPath(path, confirmed), _stop.Token)
                    .ConfigureAwait(false);
                _needsReconciliation.TryRemove(path, out _);
            }
            catch
            {
                _readyPaths.TryRemove(path, out _);
                throw;
            }
        }
        catch (FileBusyException error)
        {
            ScheduleBusyRetry(path, pending, error.Message);
        }
        catch (FileNotFoundException) { _lastFailure.TryRemove(path, out _); }
        catch (DirectoryNotFoundException) { _lastFailure.TryRemove(path, out _); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogWritebackFailure(path, error);
            ScheduleGenericRetry(path, pending);
        }
        finally
        {
            _confirming.TryRemove(path, out _);
            _confirmationSlots.Release();
        }
    }

    private bool CanConfirmWriteback(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        if (Callbacks.IsWriteMutationPending(path)) return false;

        var attributes = File.GetAttributes(path);
        if (IsTemporary(path, attributes)) return false;
        var owned = ProviderIdentity.TryReadPlaceholderIdentity(path, out var identity);
        if ((attributes & FileAttributes.ReparsePoint) != 0 && !owned) return false;

        var directory = (attributes & FileAttributes.Directory) != 0;
        if (owned && (directory || identity.IsVirtual)) return false;
        if (owned)
        {
            if (!PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state)
                || state.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
                return false;
            if (!Callbacks.IsHydrationQuiet(identity.FileId, 2_000))
                throw new FileBusyException("文件仍在水化，稍后重新确认。");
        }
        return true;
    }

    private async Task RunUploadWorkerAsync()
    {
        try
        {
            await foreach (var ready in _readyQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(ready.Path).ConfigureAwait(false);
                    _lastFailure.TryRemove(ready.Path, out _);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (FileNotFoundException) { _lastFailure.TryRemove(ready.Path, out _); }
                catch (DirectoryNotFoundException) { _lastFailure.TryRemove(ready.Path, out _); }
                catch (FileBusyException error)
                {
                    ScheduleBusyRetry(ready.Path, ready.Pending, error.Message);
                }
                catch (Exception error)
                {
                    LogWritebackFailure(ready.Path, error);
                    ScheduleGenericRetry(ready.Path, ready.Pending);
                }
                finally
                {
                    _readyPaths.TryRemove(ready.Path, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void ScheduleBusyRetry(string path, PendingChange failed, string reason)
    {
        var now = Environment.TickCount64;
        var firstEvent = failed.FirstEventTick;
        var nextRetry = Math.Min(failed.BusyRetries, BusyRetryDelaysMilliseconds.Length - 1);
        var due = now + BusyRetryDelaysMilliseconds[nextRetry];
        var retry = failed with
        {
            FirstEventTick = firstEvent,
            LastEventTick = now,
            DueTick = due,
            BusyRetries = Math.Min(failed.BusyRetries + 1, BusyRetryDelaysMilliseconds.Length)
        };

        if (now - firstEvent >= MaximumConfirmationWaitMilliseconds)
        {
            _needsReconciliation.TryAdd(path, 0);
            _log?.Invoke($"[WRITE] 独占确认等待超过 60 秒，移交全量扫描 path={path}");
            RequestFullScan();
            return;
        }

        _pending.AddOrUpdate(path, retry, (_, current) =>
        {
            if (current.LastEventTick <= failed.LastEventTick) return retry;
            return current with
            {
                FirstEventTick = Math.Min(current.FirstEventTick, failed.FirstEventTick),
                DueTick = Math.Max(current.DueTick, due),
                BusyRetries = Math.Max(current.BusyRetries, retry.BusyRetries)
            };
        });

        if (!_lastFailure.TryGetValue(path, out var previous) || previous != reason)
            _log?.Invoke($"[WRITE] 文件仍被占用，按退避间隔重新确认 path={path} retry={retry.BusyRetries}");
        _lastFailure[path] = reason;
    }

    private void ScheduleGenericRetry(string path, PendingChange failed)
    {
        var now = Environment.TickCount64;
        var retry = new PendingChange(now, now, now + 30_000, 0);
        _pending.AddOrUpdate(path, retry,
            (_, current) => current.LastEventTick <= failed.LastEventTick ? retry : current);
    }

    private void LogWritebackFailure(string path, Exception error)
    {
        // Do not log HttpRequestException.Message: upload URLs contain tokens.
        var reason = error is HttpRequestException ? "HTTP 请求失败"
            : error is IOException or UnauthorizedAccessException ? error.Message
            : error.GetType().Name;
        if (!_lastFailure.TryGetValue(path, out var previous) || previous != reason)
            _log?.Invoke($"[WRITE] 保留本地内容，写回未完成 path={path} reason={reason}");
        _lastFailure[path] = reason;
    }

    private static FileStream OpenExclusiveRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { throw; }
        catch (DirectoryNotFoundException) { throw; }
        catch (IOException error) { throw new FileBusyException("文件仍被其他进程占用。", error); }
        catch (UnauthorizedAccessException error) { throw new FileBusyException("文件当前不可独占打开。", error); }
    }

    private static FileStream OpenUploadRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { throw; }
        catch (DirectoryNotFoundException) { throw; }
        catch (IOException error) { throw new FileBusyException("文件在确认后又被占用。", error); }
        catch (UnauthorizedAccessException error) { throw new FileBusyException("文件当前不可读取。", error); }
    }

    private static bool IsTemporaryPath(string path) =>
        Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)
        || Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTemporary(string path, FileAttributes attributes) =>
        (attributes & FileAttributes.Temporary) != 0 || IsTemporaryPath(path);

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
        if (owned && !Callbacks.IsHydrationQuiet(identity.FileId, 2_000))
            throw new FileBusyException("文件仍在水化，稍后重试写回。");

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

        // Exclusive-open confirmation has already succeeded. Re-open with the provider's existing
        // read-sharing mode for hashing/upload; it blocks new writers while remaining compatible
        // with the later CFAPI placeholder conversion.
        await using var file = OpenUploadRead(path);
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
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _stop.Cancel();
        _scanSignal.Set();
        _readyQueue.Writer.TryComplete();

        try { _scheduler.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        try { _uploadWorker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _scanThread.Join();

        while (_confirmationTasks.Count > 0)
        {
            var tasks = _confirmationTasks.Values.ToArray();
            if (tasks.Length == 0) break;
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }

        _confirmationSlots.Dispose();
        _scanSignal.Dispose();
        _stop.Dispose();
    }

    private readonly record struct PendingChange(
        long FirstEventTick, long LastEventTick, long DueTick, int BusyRetries);

    private readonly record struct ConfirmedPath(string Path, PendingChange Pending);

    private sealed class FileBusyException : IOException
    {
        public FileBusyException(string message) : base(message) { }
        public FileBusyException(string message, Exception innerException) : base(message, innerException) { }
    }
}
