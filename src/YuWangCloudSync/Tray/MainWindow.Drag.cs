using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using Windows.Security.Cryptography;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    private readonly DispatcherTimer _dragIntentTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _dragProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private (int X, int Y) _dragPressPosition;
    private bool _outboundDragActive;
    private bool _dragCancelledByUser;
    private CancellationTokenSource? _dragCancellation;
    private CancellationTokenSource? _dragWarmupCancellation;
    private Task<DragManifest>? _dragWarmup;
    private RemoteFolder? _dragWarmupFolder;
    private YuWangItem[] _dragWarmupItems = [];
    private Func<bool> _dragBackendAvailable = static () => Program.IsDragBackendAvailable;
    private Action<string>? _dragDiagnostic;
    private PointerPoint? _dragPointer;
    private sealed class DragProgressState
    {
        private DragPreparationProgress _latest = new("正在定位文件");
        internal DragPreparationProgress Latest => Volatile.Read(ref _latest);
        internal void Report(DragPreparationProgress progress) => Volatile.Write(ref _latest, progress);
    }
    private DragProgressState? _dragProgressState;
    private Task? _dragProgressTask;
    private bool _dragProgressShown;

    private void UpdateDragPreparationProgress()
    {
        if (_isClosed || _dragProgressState is null || _dragProgressTask is null) return;
        if (_dragProgressTask.IsCompleted)
        {
            StopDragPreparationProgress();
            return;
        }
        ShowDragPreparationProgress(_dragProgressState.Latest);
    }

    private void ShowDragPreparationProgress(DragPreparationProgress progress)
    {
        var text = progress.Stage;
        if (progress.Percent is { } percent)
            text += $" {percent:F2}% · {FormatSize(progress.CompletedBytes)} / {FormatSize(progress.TotalBytes)}";
        if (progress.TotalFiles > 0 && progress.TotalBytes is not null)
            text += $" · 已完成 {progress.CompletedFiles}/{progress.TotalFiles} 个文件";
        if (!string.IsNullOrEmpty(progress.FileName)) text += $" · {progress.FileName}";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.TextTrimming = TextTrimming.None;
        SetTransferProgress(text, progress.Percent ?? 0);
        _transferProgress.IsIndeterminate = progress.Percent is null;
        ToolTipService.SetToolTip(_status, text);
        _dragProgressShown = true;
        _cancelDragPreparation.Visibility = Visibility.Visible;
    }

    private void StopDragPreparationProgress(bool clearStatus = false)
    {
        _dragProgressTimer.Stop();
        _cancelDragPreparation.Visibility = Visibility.Collapsed;
        _dragProgressState = null;
        _dragProgressTask = null;
        if (!_dragProgressShown) return;
        _dragProgressShown = false;
        _transferProgress.IsIndeterminate = false;
        _transferProgress.Visibility = Visibility.Collapsed;
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(_status, null);
        if (clearStatus) SetStatus("");
    }

    private sealed class DragManifest
    {
        internal List<VirtualDragEntry> Entries { get; } = [];
        internal DragProgressState Progress { get; } = new();
        private readonly object _gate = new();
        private readonly List<DragPreparationProgress> _files = [];
        internal Action<DragPreparationProgress> AddFile(string name, long? length, long? cachedBytes = 0, bool ready = false)
        {
            var index = _files.Count;
            _files.Add(new("等待提取文件", name, cachedBytes, length, IsComplete: ready));
            return progress =>
            {
                lock (_gate)
                {
                    _files[index] = progress with
                    {
                        TotalBytes = progress.TotalBytes ?? _files[index].TotalBytes,
                        IsComplete = progress.IsComplete || (_files[index].IsComplete
                            && progress.CompletedBytes is { } bytes && bytes == progress.TotalBytes),
                    };
                    long? total = 0, completed = 0;
                    foreach (var file in _files) { total += file.TotalBytes; completed += file.CompletedBytes; }
                    Progress.Report(new(progress.Stage, progress.FileName, completed, total,
                        _files.Count(file => file.IsComplete), _files.Count));
                }
            };
        }
    }

    // Selection prefetch reads metadata only. It must never download or hydrate file data.
    private void QueueDragPreparation() => QueueDragPreparation(_currentFolder, SelectedEntries().ToArray());
    private void QueueDragPreparation(RemoteFolder? folder, YuWangItem[] items)
    {
        if (_busyNow || _outboundDragActive || _isClosed) return;
        if (_dragWarmup is not null && Equals(folder, _dragWarmupFolder) && items.SequenceEqual(_dragWarmupItems)) return;
        CancelDragWarmup();
        if (folder is null || items.Length == 0 || !_dragBackendAvailable()) return;
        _dragWarmupFolder = folder; _dragWarmupItems = items;
        _dragWarmupCancellation = CancellationTokenSource.CreateLinkedTokenSource(Program.ApplicationCancellationToken);
        _dragWarmupCancellation.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.DownloadTimeoutMinutes, 1, 120)));
        _dragWarmup = BuildDragManifestAsync(folder, items, _dragWarmupCancellation.Token);
        _ = _dragWarmup.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task<DragManifest> BuildDragManifestAsync(RemoteFolder folder, YuWangItem[] items, CancellationToken token)
    {
        var manifest = new DragManifest();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); EnsureDragBackendAvailable();
            _downloadedPaths.TryGetValue((folder.Disk.Id, item.Id), out var downloaded);
            var local = await Task.Run(() =>
            {
                var path = Program.ResolveLocalItemPath(folder.Disk.Id, folder.Id, item);
                var hasDownload = downloaded is not null && (item.IsFolder ? Directory.Exists(downloaded) : File.Exists(downloaded));
                if (hasDownload && (path is null || (!item.IsFolder && ((uint)File.GetAttributes(path) & 0x00441000) != 0))) return downloaded;
                return path;
            }, token);
            var name = UniqueDragName(item.Name, names);
            if (local is not null) await AddLocalDragEntriesAsync(manifest, local, name, token);
            else await AddRemoteDragEntriesAsync(manifest, folder.Disk.Id, item, name, token);
        }
        if (manifest.Entries.Count == 0) throw new IOException("没有可拖放的文件。");
        return manifest;
    }

    private static string UniqueDragName(string value, HashSet<string> names)
    {
        var name = SafeFileName(value); var result = name;
        for (var i = 2; !names.Add(result); i++) result = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
        return result;
    }

    private async Task AddLocalDragEntriesAsync(DragManifest manifest, string path, string name, CancellationToken token)
    {
        var metadata = await Task.Run(() =>
        {
            var attributes = File.GetAttributes(path);
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (directory && (attributes & FileAttributes.ReparsePoint) != 0 && !ProviderIdentity.OwnsPlaceholder(path))
                throw new IOException($"无法拖放指向其他位置的目录：{path}");
            var readiness = directory ? (Size: 0L, ReadyBytes: (long?)null, IsReady: false) : DragFilePreparation.ReadMetadata(path);
            return (Directory: directory, Size: directory ? (long?)null : readiness.Size,
                ReadyBytes: readiness.ReadyBytes, IsReady: readiness.IsReady, Modified: File.GetLastWriteTimeUtc(path));
        }, token);
        if (name.Length >= 260) throw new IOException("拖放文件相对路径超过 259 个字符。");
        if (metadata.Directory)
        {
            manifest.Entries.Add(new(name, true, null, metadata.Modified));
            var children = await Task.Run(() => Directory.GetFileSystemEntries(path), token);
            foreach (var child in children) await AddLocalDragEntriesAsync(manifest, child, Path.Combine(name, Path.GetFileName(child)), token);
        }
        else
        {
            var report = manifest.AddFile(name, metadata.Size, metadata.ReadyBytes, metadata.IsReady);
            manifest.Entries.Add(new(name, false, metadata.Size, metadata.Modified, async cancellation =>
            {
                EnsureDragBackendAvailable();
                var lease = await DragFilePreparation.CreateAsync([path], _dragBackendAvailable, _ => { }, cancellation, report);
                return new(path, lease);
            }));
        }
    }

    private async Task AddRemoteDragEntriesAsync(DragManifest manifest, ulong disk, YuWangItem item, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (name.Length >= 260) throw new IOException("拖放文件相对路径超过 259 个字符。");
        if (item.IsFolder)
        {
            manifest.Entries.Add(new(name, true, null, item.UploadTime));
            var children = await Client!.ListFolderAsync(disk, item.Id).WaitAsync(token);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
                await AddRemoteDragEntriesAsync(manifest, disk, child, Path.Combine(name, UniqueDragName(child.Name, names)), token);
        }
        else
        {
            // The HTML listing rounds sizes. Omit FD_FILESIZE until a precise probe is available;
            // the eventual IStream::Stat reports the real downloaded size.
            var report = manifest.AddFile(name, null);
            manifest.Entries.Add(new(name, false, null, item.UploadTime, async cancellation =>
            {
                EnsureDragBackendAvailable();
                var staging = Path.Combine(Path.GetTempPath(), ProductIdentity.Identifier, "dragged-files", Guid.NewGuid().ToString("N"));
                var path = await OnDragUiAsync(() => DownloadFileAsync(disk, item, staging, cancellation, reportProgress: false,
                    preparationProgress: (_, done, total) => report(new("正在下载文件", name, done, total))), cancellation);
                var lease = await DragFilePreparation.CreateAsync([path], _dragBackendAvailable, _ => { }, cancellation, report);
                return new(path, lease);
            }));
        }
    }

    private Task<T> OnDragUiAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(await action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new IOException("主界面已关闭，无法继续准备文件。"));
        return completion.Task.WaitAsync(token);
    }

    private void CancelDragWarmup()
    {
        if (!_outboundDragActive) StopDragPreparationProgress(clearStatus: true);
        _dragWarmup = null; _dragWarmupFolder = null; _dragWarmupItems = [];
        _dragWarmupCancellation?.Cancel(); _dragWarmupCancellation?.Dispose(); _dragWarmupCancellation = null;
    }

    private void CancelActiveDragPreparation()
    {
        _dragCancelledByUser = true;
        _dragCancellation?.Cancel();
        _cancelDragPreparation.IsEnabled = false;
        SetStatus("正在取消文件准备…");
    }

    private void CheckDragIntent()
    {
        if (_dragSourceRow is not { Tag: YuWangItem item } row || !NativeFileDrag.IsLeftButtonDown
            || NativeFileDrag.IsEscapeDown || _isClosed || _busyNow || _currentFolder is null)
        { _dragIntentTimer.Stop(); _dragSourceRow = null; return; }
        var position = NativeFileDrag.CursorPosition; var threshold = NativeFileDrag.DragThreshold;
        if (Math.Abs(position.X - _dragPressPosition.X) < threshold.X && Math.Abs(position.Y - _dragPressPosition.Y) < threshold.Y) return;
        _dragIntentTimer.Stop(); _dragSourceRow = null;
        if (!row.IsSelected) SelectDragSource(row);
        var items = row.IsSelected ? SelectedEntries().ToArray() : [item];
        RunUi(() => StartVirtualDragAsync(_currentFolder, items, row));
    }

    private async Task<DragItemVisual?> CaptureDragItemAsync(ListViewItem row)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(row);
            var buffer = await bitmap.GetPixelsAsync(); CryptographicBuffer.CopyToByteArray(buffer, out var pixels);
            // Keep the selected row's complete visual. Only apply the normal drag translucency.
            for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(pixels[i] * 0.85);
            var point = _dragPointer?.Position;
            var origin = row.TransformToVisual(_items).TransformPoint(new Windows.Foundation.Point());
            var scale = _root.XamlRoot?.RasterizationScale ?? 1;
            return new(pixels, bitmap.PixelWidth, bitmap.PixelHeight,
                (int)Math.Clamp(((point?.X ?? origin.X) - origin.X) * scale, 0, bitmap.PixelWidth - 1),
                (int)Math.Clamp(((point?.Y ?? origin.Y) - origin.Y) * scale, 0, bitmap.PixelHeight - 1));
        }
        catch (Exception error) { Serilog.Log.Debug(error, "读取原项目拖放外观"); return null; }
    }

    private async Task StartVirtualDragAsync(RemoteFolder folder, YuWangItem[] items, ListViewItem row)
    {
        if (_outboundDragActive || _busyNow || _isClosed || items.Length == 0) return;
        _infoBar.IsOpen = false;
        QueueDragPreparation(folder, items);
        var warmup = _dragWarmup; var metadataCancellation = _dragWarmupCancellation;
        _dragWarmup = null; _dragWarmupCancellation = null; _dragWarmupFolder = null; _dragWarmupItems = [];
        _outboundDragActive = _busyNow = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Program.ApplicationCancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.DownloadTimeoutMinutes, 1, 120)));
        _dragCancellation = cancellation;
        _dragCancelledByUser = false;
        _cancelDragPreparation.IsEnabled = true;
        var session = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        NativeFileDrag? metadataPreview = null;
        var enteredOle = false;
        try
        {
            EnsureDragBackendAvailable();
            if (warmup is null) throw new IOException("没有可拖放的文件，请重新选择文件后重试。");
            _dragProgressState = new(); _dragProgressTask = session.Task;
            _dragProgressState.Report(new("正在读取文件信息")); _dragProgressTimer.Start();
            monitor.Tick += (_, _) =>
            {
                if (_isClosed || !_dragBackendAvailable()) cancellation.Cancel();
                if (!enteredOle && (!NativeFileDrag.IsLeftButtonDown || NativeFileDrag.IsEscapeDown))
                { _dragCancelledByUser = true; cancellation.Cancel(); }
                try { metadataPreview?.MovePreparationImage(); }
                catch (Exception error) { Serilog.Log.Debug(error, "移动项目预览"); cancellation.Cancel(); }
            };
            monitor.Start();
            var visual = await CaptureDragItemAsync(row);
            if (!warmup.IsCompleted && visual is not null)
            {
                metadataPreview = new NativeFileDrag(preparing: false);
                metadataPreview.ShowItemPreview(visual);
            }
            var manifest = await warmup.WaitAsync(cancellation.Token);
            metadataPreview?.Dispose(); metadataPreview = null;
            _dragProgressState = manifest.Progress;
            manifest.Progress.Report(new("拖动项目到目标位置，松手后提取文件"));
            EnsureDragBackendAvailable(); cancellation.Token.ThrowIfCancellationRequested();
            if (!NativeFileDrag.IsLeftButtonDown || NativeFileDrag.IsEscapeDown) throw new OperationCanceledException();
            using var data = new VirtualFileDataObject(manifest.Entries, cancellation.Token);
            _dragDiagnostic?.Invoke($"Entering virtual file drag; descriptors={manifest.Entries.Count}; no hydration started");
            enteredOle = true;
            _items.ReleasePointerCaptures(); NativeFileDrag.ReleaseMouseCapture();
            var copied = await NativeFileDrag.DropVirtualAsync(data, visual,
                () => !cancellation.IsCancellationRequested && _dragBackendAvailable() && !data.HasFailure);
            _dragDiagnostic?.Invoke($"OLE returned; copied={copied}; async extraction={data.InAsyncOperation}");
            if (copied)
            {
                await data.FinishExtractionAsync(cancellation.Token);
                EnsureDragBackendAvailable();
                SetStatus("已交给目标复制");
            }
            else
            {
                cancellation.Cancel();
                if (data.Failure is { } error) throw new IOException(error.Message, error);
                SetStatus("已取消拖放");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                SetStatus("已取消文件准备");
                if (!_dragBackendAvailable()) ShowInfo(InfoBarSeverity.Error, "拖放失败", "同步引擎已停止，无法继续提取文件。");
                else if (!_dragCancelledByUser && !Program.ApplicationCancellationToken.IsCancellationRequested && cancellation.IsCancellationRequested)
                    ShowInfo(InfoBarSeverity.Error, "拖放失败", "文件准备超时，请检查网络连接与同步引擎后重试。");
            }
        }
        catch (Exception error)
        {
            _dragDiagnostic?.Invoke($"Virtual drag failed: {error}"); Serilog.Log.Warning(error, "向外拖放失败");
            if (!_isClosed) { SetStatus("拖放失败"); ShowInfo(InfoBarSeverity.Error, "拖放失败", error.Message); }
        }
        finally
        {
            cancellation.Cancel(); session.TrySetResult(); monitor.Stop(); metadataPreview?.Dispose(); StopDragPreparationProgress();
            metadataCancellation?.Cancel(); metadataCancellation?.Dispose(); _dragCancellation = null;
            _outboundDragActive = _busyNow = false;
            if (!_isClosed)
            {
                UpdateCommandStates(); QueueDragPreparation();
                if (_pendingCollapsedNode is not null || _pendingSelection is not null) QueueNavigationChange();
            }
        }
    }

    private void EnsureDragBackendAvailable()
    {
        if (!_dragBackendAvailable()) throw new IOException("同步引擎尚未连接或正在停止，请等待同步恢复后再拖放。");
    }
}
