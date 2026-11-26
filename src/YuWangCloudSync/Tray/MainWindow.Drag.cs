using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        SetTransferProgress(text, progress.Percent ?? 0, progress.FileName);
        _transferProgress.IsIndeterminate = progress.Percent is null;
        _dragProgressShown = true;
        _cancelDragPreparation.Visibility = Visibility.Visible;
    }

    private const int MaxStatusFileNameLength = 12;

    /// <summary>Middle-truncates a long name so the extension stays visible in the one-line status bar.</summary>
    internal static string ShortenFileName(string name)
    {
        if (name.Length <= MaxStatusFileNameLength) return name;
        var elements = StringInfo.ParseCombiningCharacters(name);
        if (elements.Length <= MaxStatusFileNameLength) return name;
        var extension = Path.GetExtension(name);
        var extensionStart = Array.BinarySearch(elements, name.Length - extension.Length);
        var extensionLength = extensionStart < 0 ? 0 : elements.Length - extensionStart;
        // Leave space for the ellipsis and at least one text element from the stem.
        if (extensionLength > MaxStatusFileNameLength - 2) extensionLength = 0;
        var keep = MaxStatusFileNameLength - extensionLength - 1;
        var head = (keep + 1) / 2;
        var tail = extensionLength + keep - head;
        return $"{name[..elements[head]]}…{(tail > 0 ? name[elements[^tail]..] : "")}";
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
        ToolTipService.SetToolTip(_status, null);
        if (clearStatus) SetStatus("");
        else UpdateTransferStatusLayout();
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

    // Used only by the isolated UI/receiver tests, which have no live sync root.
    private Dictionary<(ulong Disk, ulong Item), string>? _dragFixturePaths;

    private async Task<DragManifest> BuildDragManifestAsync(RemoteFolder folder, YuWangItem[] items, CancellationToken token)
    {
        var manifest = new DragManifest();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var client = Client;
        var ancestors = new HashSet<ulong> { folder.Id };
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); EnsureDragBackendAvailable();
            var name = UniqueDragName(item.Name, names);
            if (_dragFixturePaths?.TryGetValue((folder.Disk.Id, item.Id), out var fixture) == true)
                await AddDragFileAsync(manifest, item, name, fixture, async (report, cancellation) =>
                {
                    var lease = await DragFilePreparation.CreateAsync([fixture], _dragBackendAvailable, _ => { }, cancellation, report);
                    return new(fixture, lease);
                }, token);
            else
                await AddSyncRootDragEntriesAsync(manifest, client ?? throw new IOException("尚未连接到云端。"),
                    folder.Disk.Id, folder.Id, item, name, ancestors, token);
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

    private async Task AddSyncRootDragEntriesAsync(DragManifest manifest, YuWangClient client, ulong disk,
        ulong parent, YuWangItem item, string name, HashSet<ulong> ancestors, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureDragBackendAvailable();
        if (name.Length >= 260) throw new IOException("文件路径过长（超过 259 个字符）。");
        if (item.IsFolder)
        {
            if (!ancestors.Add(item.Id)) throw new IOException("云端目录层级成环。");
            try
            {
                manifest.Entries.Add(new(name, true, null, item.UploadTime));
                var children = await ListSyncRootFolderChildrenAsync(client, disk, parent, item, token);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in children)
                    await AddSyncRootDragEntriesAsync(manifest, client, disk, item.Id, child,
                        Path.Combine(name, UniqueDragName(child.Name, names)), ancestors, token);
            }
            finally { ancestors.Remove(item.Id); }
            return;
        }
        var local = await Task.Run(() => Program.ResolveLocalItemPath(disk, parent, item), token);
        await AddDragFileAsync(manifest, item, name, local,
            (report, cancellation) => HydrateSyncRootFileAsync(client, disk, parent, item, report, cancellation), token);
    }

    private async Task AddDragFileAsync(DragManifest manifest, YuWangItem item, string name, string? local,
        Func<Action<DragPreparationProgress>, CancellationToken, Task<VirtualFileContent>> prepareContent, CancellationToken token)
    {
        var metadata = local is null ? default : await Task.Run(() => DragFilePreparation.ReadMetadata(local), token);
        // An online placeholder may still hold a padded, provisional length. Omit FD_FILESIZE
        // until the stream is prepared, instead of promising a rounded length to the receiver.
        long? size = metadata.IsReady ? metadata.Size : null;
        var report = manifest.AddFile(name, size, metadata.ReadyBytes, metadata.IsReady);
        manifest.Entries.Add(new(name, false, size, item.UploadTime, async cancellation =>
        {
            EnsureDragBackendAvailable();
            return await prepareContent(report, cancellation);
        }));
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
        SetProgressStatus("正在取消文件准备…");
    }

    private void CheckDragIntent()
    {
        if (NativeFileDrag.IsTouchContactActive) return; // Touch drags start from OnTouchDragItemsStarting.
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

    #region Touch and pen

    private Pointer? _touchPointer;
    private Pointer? _touchCapturePointer;
    private bool _touchCaptureLost;
    private long _touchLastCaptureAttempt;
    private bool _touchXamlMotionReported;
    private bool? _touchOutsideWindow;
    private long _touchLastPositionLog;

    private static bool IsContactPointer(Pointer pointer) => pointer.PointerDeviceType != PointerDeviceType.Mouse;

    private (int X, int Y) ToScreenPixels(PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(null).Position;
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = TouchGetDpiForWindow(window) / 96d;
        return NativeFileDrag.ClientToScreen(window, position.X * scale, position.Y * scale);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint TouchGetDpiForWindow(nint window);

    private void BeginTouchContact(PointerRoutedEventArgs args)
    {
        _touchPointer = args.Pointer;
        var state = TouchPointerHook.BindXamlPointer(args.Pointer.PointerId, out var nativePosition, out var detail);
        var position = state == PointerContact.InContact ? nativePosition : ToScreenPixels(args);
        NativeFileDrag.BeginTouchContact(position);
        ReportDragDiagnostic($"Touch contact bound; pointer={args.Pointer.PointerId}; native={state}; {detail}; position={position}");
    }

    /// <summary>Mirrors the touch contact for OLE; returns true when the move belongs to it.</summary>
    private bool TrackTouchContact(PointerRoutedEventArgs args)
    {
        if (_touchPointer is null || args.Pointer.PointerId != _touchPointer.PointerId) return false;
        // OLE hit-tests drop targets at the cursor, which touch does not move.
        if (!_touchPollOwnsContact)
        {
            var hasPointerPosition = TouchPointerHook.TryGetPosition(args.Pointer.PointerId, out var pointerPosition, out _);
            var position = hasPointerPosition ? pointerPosition : ToScreenPixels(args);
            NativeFileDrag.MoveTouchContact(position, moveCursor: _touchDragging);
            if (_touchDragging) ReportTouchPosition(position, hasPointerPosition ? "current pointer sample" : "XAML/DPI fallback");
            if (_touchDragging && !_touchXamlMotionReported)
            {
                _touchXamlMotionReported = true;
                ReportDragDiagnostic($"Touch contact tracked by captured XAML pointer; at={position}");
            }
        }
        return true;
    }

    private void ReleaseTouchPointerCapture()
    {
        if (_touchCapturePointer is not { } pointer) return;
        _root.ReleasePointerCapture(pointer);
        _items.ReleasePointerCapture(pointer);
        _touchCapturePointer = null;
        _touchCaptureLost = false;
    }

    private void OnTouchPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (!_touchDragging || _touchPointer is not { } pointer || args.Pointer.PointerId != pointer.PointerId) return;
        _touchCapturePointer = null;
        _touchCaptureLost = true;
        _touchLastCaptureAttempt = 0;
        ReportDragDiagnostic($"Touch pointer capture lost; pointer={pointer.PointerId}; sender={sender?.GetType().Name}");
    }

    private void RestoreTouchPointerCapture(Pointer pointer)
    {
        var captured = _root.CapturePointer(pointer);
        var target = captured ? "window root" : "unavailable";
        if (!captured && _items.CapturePointer(pointer))
        {
            captured = true;
            target = "file list";
        }
        _touchCapturePointer = captured ? pointer : null;
        _touchCaptureLost = !captured;
        _touchLastCaptureAttempt = Environment.TickCount64;
        ReportDragDiagnostic($"Touch pointer capture {(captured ? "restored" : "restore failed")}; pointer={pointer.PointerId}; target={target}");
    }

    private void EndTouchContact(Pointer pointer)
    {
        if (_touchPointer is null || pointer.PointerId != _touchPointer.PointerId) return;
        _touchPointer = null;
        _touchDragTracker.Stop();
        ReleaseTouchPointerCapture();
        if (!_touchDragging) { NativeFileDrag.ClearTouchContact(); return; }
        ReportDragDiagnostic($"Touch contact {pointer.PointerId} released (pointer event)");
        NativeFileDrag.EndTouchContact(); // Released: OLE drops; before OLE the drag cancels, as with a mouse.
    }

    // Capture on the window root keeps XAML updates and lift routed back to this window after the
    // finger leaves the file list. The native hook is an additional source when WinUI's XAML capture
    // does not expose a contact update; native tracking must not be required to start the drag.
    private readonly DispatcherTimer _touchDragTracker = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private bool _touchDragging, _touchPollOwnsContact;
    private PointerContact? _touchPollState;

    private void ReportTouchWindowBoundary((int X, int Y) position)
    {
        var outside = TouchPointerHook.IsOutsideWindow(position);
        if (_touchOutsideWindow == outside) return;
        _touchOutsideWindow = outside;
        ReportDragDiagnostic($"Touch contact window boundary; outside={outside}; at={position}");
    }

    private void ReportTouchPosition((int X, int Y) position, string source)
    {
        ReportTouchWindowBoundary(position);
        var now = Environment.TickCount64;
        if (now - _touchLastPositionLog < 500) return;
        _touchLastPositionLog = now;
        ReportDragDiagnostic($"Touch contact position; source={source}; at={position}; outside={_touchOutsideWindow}");
    }

    private void PollTouchDragContact()
    {
        if (_touchPointer is not { } pointer) { _touchDragTracker.Stop(); return; }
        var state = TouchPointerHook.Read(out var position, out var detail);
        if (state != _touchPollState)
        {
            _touchPollState = state;
            ReportDragDiagnostic($"Touch contact {pointer.PointerId}: native={state}; {detail}; xamlCapture={_touchCapturePointer is not null}; nativeOwned={_touchPollOwnsContact}");
        }
        if (state == PointerContact.InContact)
        {
            _touchPollOwnsContact = true;
            if (_touchCaptureLost && Environment.TickCount64 - _touchLastCaptureAttempt >= 50)
                RestoreTouchPointerCapture(pointer);
            ReportTouchPosition(position, "current pointer sample");
            NativeFileDrag.MoveTouchContact(position, moveCursor: true);
        }
        else if (_touchPollOwnsContact)
        {
            _touchDragTracker.Stop();
            _touchPointer = null;
            ReleaseTouchPointerCapture();
            NativeFileDrag.EndTouchContact();
        }
    }

    private void StopTouchDragTracking()
    {
        _touchDragTracker.Stop();
        TouchPointerHook.Stop();
        ReleaseTouchPointerCapture();
        _touchDragging = _touchPollOwnsContact = false;
        _touchCaptureLost = false;
        _touchLastCaptureAttempt = 0;
        _touchXamlMotionReported = false;
        _touchOutsideWindow = null;
        _touchLastPositionLog = 0;
        _touchPollState = null;
        _touchPointer = null;
        NativeFileDrag.ClearTouchContact();
    }

    // The list recognised a touch/pen drag gesture (hold, or swipe across the scroll direction).
    // Its WinUI drag cannot carry virtual files, so hand the gesture to the OLE path instead.
    private void OnTouchDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        args.Cancel = true;
        if (_touchPointer is not { } pointer || !NativeFileDrag.IsTouchContactActive || !NativeFileDrag.IsLeftButtonDown
            || _dragSourceRow is not { Tag: YuWangItem item } row || _isClosed || _busyNow || _currentFolder is null)
            return;
        _dragSourceRow = null;
        _itemMenu.Hide();
        _touchDragging = true; _touchPollOwnsContact = false; _touchPollState = null;
        _touchCaptureLost = false;
        RestoreTouchPointerCapture(pointer);
        _touchXamlMotionReported = false;
        _touchOutsideWindow = null;
        _touchLastPositionLog = 0;
        NativeFileDrag.EnableTouchDragBridge();
        NativeFileDrag.MoveTouchContact(NativeFileDrag.CursorPosition, moveCursor: true);
        ReportDragDiagnostic($"Touch drag recognised; pointer={pointer.PointerId}; device={pointer.PointerDeviceType}; capture={(_touchCapturePointer is null ? "unavailable" : "window root or file list")}");
        _touchDragTracker.Start();
        PollTouchDragContact();
        if (!row.IsSelected) SelectDragSource(row);
        var items = row.IsSelected ? SelectedEntries().ToArray() : [item];
        RunUi(() => StartVirtualDragAsync(_currentFolder, items, row));
    }

    #endregion

    private async Task<DragItemVisual?> CaptureDragItemAsync(ListViewItem row)
    {
        try
        {
            row.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(row);
            var buffer = await bitmap.GetPixelsAsync(); CryptographicBuffer.CopyToByteArray(buffer, out var pixels);
            // Rendering the row alone omits the file pane behind its translucent selection
            // fill. Composite that themed surface beneath the premultiplied BGRA pixels
            // before applying drag opacity, so the background follows the item too.
            var background = Project<SolidColorBrush>(WinUiUiHost.GetBrushResource("SolidBackgroundFillColorBaseBrush"))?.Color
                ?? (_root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            if (Project<SolidColorBrush>(WinUiUiHost.GetBrushResource("LayerFillColorDefaultBrush")) is { } layer)
            {
                var alpha = layer.Color.A / 255d * layer.Opacity;
                background.R = (byte)Math.Round(layer.Color.R * alpha + background.R * (1 - alpha));
                background.G = (byte)Math.Round(layer.Color.G * alpha + background.G * (1 - alpha));
                background.B = (byte)Math.Round(layer.Color.B * alpha + background.B * (1 - alpha));
            }
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var uncovered = 1 - pixels[i + 3] / 255d;
                pixels[i] = (byte)((pixels[i] + background.B * uncovered) * 0.85);
                pixels[i + 1] = (byte)((pixels[i + 1] + background.G * uncovered) * 0.85);
                pixels[i + 2] = (byte)((pixels[i + 2] + background.R * uncovered) * 0.85);
                pixels[i + 3] = (byte)(255 * 0.85);
            }
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
            if (warmup is null) throw new IOException("没有可拖放的文件。");
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
            manifest.Progress.Report(new("拖到目标位置后松开"));
            EnsureDragBackendAvailable(); cancellation.Token.ThrowIfCancellationRequested();
            if (!NativeFileDrag.IsLeftButtonDown || NativeFileDrag.IsEscapeDown) throw new OperationCanceledException();
            using var data = new VirtualFileDataObject(manifest.Entries, cancellation.Token);
            ReportDragDiagnostic($"Entering virtual file drag; descriptors={manifest.Entries.Count}; no hydration started");
            enteredOle = true;
            if (!_touchDragging)
            {
                _items.ReleasePointerCaptures();
                NativeFileDrag.ReleaseMouseCapture();
            }
            var canContinue = () => !cancellation.IsCancellationRequested && _dragBackendAvailable() && !data.HasFailure;
            // Keep the UI dispatcher available for PointerReleased after the finger leaves
            // the source window. The worker's OLE loop receives the mirrored touch through
            // OleMouseInput, while the source window retains touch pointer capture.
            var copied = await NativeFileDrag.DropVirtualAsync(data, visual, canContinue, ReportDragDiagnostic);
            ReportDragDiagnostic($"OLE returned; copied={copied}; async extraction={data.InAsyncOperation}; streams={data.OpenStreams}; ended={data.OperationEnded.IsCompleted}");
            if (copied)
            {
                await data.FinishExtractionAsync(cancellation.Token);
                EnsureDragBackendAvailable();
                SetStatus("已开始复制");
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
                if (!_dragBackendAvailable()) ShowInfo(InfoBarSeverity.Error, "拖放失败", "同步已停止，无法继续。");
                else if (!_dragCancelledByUser && !Program.ApplicationCancellationToken.IsCancellationRequested && cancellation.IsCancellationRequested)
                    ShowInfo(InfoBarSeverity.Error, "拖放失败", "准备文件超时，请检查网络后重试。");
            }
        }
        catch (Exception error)
        {
            ReportDragDiagnostic($"Virtual drag failed: {error}"); Serilog.Log.Warning(error, "向外拖放失败");
            if (!_isClosed) { SetStatus("拖放失败"); ShowInfo(InfoBarSeverity.Error, "拖放失败", error.Message); }
        }
        finally
        {
            cancellation.Cancel(); session.TrySetResult(); monitor.Stop(); metadataPreview?.Dispose(); StopDragPreparationProgress();
            metadataCancellation?.Cancel(); metadataCancellation?.Dispose(); _dragCancellation = null;
            _outboundDragActive = _busyNow = false;
            StopTouchDragTracking();
            if (!_isClosed)
            {
                UpdateCommandStates(); QueueDragPreparation();
                if (_pendingCollapsedNode is not null || _pendingSelection is not null) QueueNavigationChange();
            }
        }
    }

    private void ReportDragDiagnostic(string message)
    {
        Serilog.Log.Information("[DRAG] {Detail}", message);
        try { _dragDiagnostic?.Invoke(message); }
        catch (Exception error) { Serilog.Log.Debug(error, "写入拖放诊断失败"); }
    }

    private void EnsureDragBackendAvailable()
    {
        if (!_dragBackendAvailable()) throw new IOException("同步尚未就绪，请稍后再拖放。");
    }
}
