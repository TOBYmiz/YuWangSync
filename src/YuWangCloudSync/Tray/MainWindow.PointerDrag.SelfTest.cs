using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    // Opt-in desktop integration test. Inject an input contact from this isolated
    // browser into a separate STA drop target and verify complete virtual-file extraction.
    private async Task<UiSelfTestCheck> CheckWinUiPointerDragAsync(ListViewItem source, string path, string testRoot,
        bool cancel = false, bool repeat = false, bool injectedTouch = false, ListViewItem? additionalSelection = null)
    {
        NativeVirtualFileReceiverWindow? receiverWindow = null;
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var touchDragRecognized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var touchReleasedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowManifest = new TaskCompletionSource<DragManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task>? cancelGesture = cancel ? () =>
        {
            cancelIssued.TrySetResult();
            return CancelPointerTestAsync();
        } : null;
        nint previewHandle = 0;
        var previewBeforeOle = false;
        var previewCountMatched = false;
        string? previewCountLabel = null;
        var expectedPreviewMode = injectedTouch ? "touch" : "mouse";
        string? previewMode = null;
        var previewAtGap = false;
        var previewOverReceiver = false;
        var previewPassedHitTest = false;
        var previewStayedVisible = true;
        var previewMoved = false;
        var overCount = 0;
        var firstX = double.NaN;
        var lastX = double.NaN;
        var coordinates = "";
        var suffix = (additionalSelection is null ? "" : "-multi") + (cancel ? "-cancel" : repeat ? "-repeat" : "");
        var inputMode = injectedTouch ? "Touch" : "Mouse";
        var caseName = additionalSelection is null ? $"{inputMode} single-file drag copies the complete file"
            : cancel ? $"{inputMode} multi-file drag cancels without extracting files"
            : repeat ? $"{inputMode} multi-file drag repeats without losing selected files"
            : $"{inputMode} multi-file drag copies all selected files";
        var tracePath = Path.Combine(testRoot, $"pointer-drag{suffix}-trace.txt");
        var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal) { [Path.GetFileName(path)] = path };
        if (additionalSelection?.Tag is YuWangItem additionalItem)
            sourcePaths.Add(additionalItem.Name, Path.Combine(testRoot, additionalItem.Name));
        var copyPaths = sourcePaths.Keys.Select((name, index) => (name,
                path: Path.Combine(testRoot, $"pointer-drag{suffix}-copied-{index}.bin")))
            .ToDictionary(file => file.name, file => file.path, StringComparer.Ordinal);
        var expectedCountLabel = sourcePaths.Count > 1 ? $"等 {sourcePaths.Count} 项" : null;
        var pointerRoot = (UIElement)Content;
        var traceGate = new object();
        void Trace(string message)
        {
            if (message.Contains("OLE mouse bridge ready", StringComparison.Ordinal)) oleStarted.TrySetResult();
            if (message.StartsWith("Drag preview shown; mode=", StringComparison.Ordinal))
                previewMode = message.Contains("mode=touch;", StringComparison.Ordinal) ? "touch" : "mouse";
            if (message.Contains("Touch drag recognised", StringComparison.Ordinal)) touchDragRecognized.TrySetResult();
            if (message.Contains("Touch pointer contact ended", StringComparison.Ordinal)
                || message.Contains("Touch contact ended", StringComparison.Ordinal)
                || message.Contains("released (pointer event)", StringComparison.Ordinal)) touchReleasedObserved.TrySetResult();
            lock (traceGate) File.AppendAllText(tracePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        var presses = 0;
        var moves = 0;
        var rootPresses = 0;
        var rootMoves = 0;
        Microsoft.UI.Xaml.Input.PointerEventHandler rootPressed = (_, _) => rootPresses++;
        Microsoft.UI.Xaml.Input.PointerEventHandler rootMoved = (_, _) => rootMoves++;
        Microsoft.UI.Xaml.Input.PointerEventHandler captureLost = (sender, args) =>
            Trace($"Pointer capture lost; pointer={args.Pointer.PointerId}; device={args.Pointer.PointerDeviceType}; sender={sender?.GetType().Name}; original={args.OriginalSource?.GetType().Name}");
        pointerRoot.AddHandler(UIElement.PointerPressedEvent, rootPressed, handledEventsToo: true);
        pointerRoot.AddHandler(UIElement.PointerMovedEvent, rootMoved, handledEventsToo: true);
        pointerRoot.AddHandler(UIElement.PointerCaptureLostEvent, captureLost, handledEventsToo: true);
        Microsoft.UI.Xaml.Input.PointerEventHandler pressed = (_, _) => { presses++; Trace("List pointer pressed"); };
        Microsoft.UI.Xaml.Input.PointerEventHandler moved = (_, _) => moves++;
        _items.AddHandler(UIElement.PointerPressedEvent, pressed, handledEventsToo: true);
        _items.AddHandler(UIElement.PointerMovedEvent, moved, handledEventsToo: true);
        Task? input = null;
        var previousBackend = _dragBackendAvailable;
        var previousDiagnostic = _dragDiagnostic;
        _dragDiagnostic = Trace;
        var externalInput = Environment.GetCommandLineArgs().Contains("--ui-selftest-external-pointer", StringComparer.OrdinalIgnoreCase);
        var statusMonitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        Action? observePreview = null;
        var lastStatus = "";
        statusMonitor.Tick += (_, _) =>
        {
            observePreview?.Invoke();
            var status = $"Status={_status.Text}; banner={_infoBar.IsOpen}: {_infoBar.Title} {_infoBar.Message}";
            if (status == lastStatus) return;
            lastStatus = status;
            Trace(status);
        };
        Windows.Foundation.TypedEventHandler<object, WindowEventArgs> sourceClosed = (_, _) =>
            dropped.TrySetException(new IOException("测试来源窗口已关闭。"));
        Closed += sourceClosed;
        try
        {
            var savedCursor = NativeFileDrag.CursorPosition;
            AppWindow.MoveAndResize(new RectInt32(30, 60, 900, 650));
            var window = WindowNative.GetWindowHandle(this);
            if (!PointerTestGetWindowRect(window, out var sourceBounds))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var targetX = sourceBounds.Right + 30;
            var targetY = Math.Max(sourceBounds.Top + 100, 100);
            var screenRight = PointerTestGetSystemMetrics(76) + PointerTestGetSystemMetrics(78);
            var screenBottom = PointerTestGetSystemMetrics(77) + PointerTestGetSystemMetrics(79);
            var targetWidth = Math.Min(800, screenRight - targetX - 20);
            var targetHeight = Math.Min(640, screenBottom - targetY - 20);
            if (targetWidth < 180 || targetHeight < 180)
                throw new InvalidOperationException($"当前桌面空间不足以并排显示拖放接收窗口；source={sourceBounds.Left},{sourceBounds.Top},{sourceBounds.Right},{sourceBounds.Bottom}; screen={screenRight}x{screenBottom}.");
            void RecordReceiverDragOver(int x)
            {
                if (double.IsNaN(firstX)) firstX = x;
                lastX = x; overCount++;
                if (overCount == 1) Trace($"Native receiver DragOver; x={x}");
            }
            Task ReceiveFile(nint data)
            {
                var extraction = VirtualFileDragSelfTest.ReceiveAsync(data, copyPaths);
                _ = extraction.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) dropped.TrySetResult();
                    else if (task.IsCanceled) dropped.TrySetCanceled();
                    else dropped.TrySetException(task.Exception!.GetBaseException());
                }, TaskScheduler.Default);
                return extraction;
            }
            receiverWindow = new NativeVirtualFileReceiverWindow("拖放回归接收窗口", targetX, targetY,
                targetWidth, targetHeight, () => new NativeVirtualFileReceiver(RecordReceiverDragOver, ReceiveFile));
            var target = receiverWindow.Handle;
            const uint showWithoutActivation = 0x0001 | 0x0002 | 0x0010 | 0x0040; // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
            if (!PointerTestSetWindowPos(target, new nint(-1), 0, 0, 0, 0, showWithoutActivation))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法置顶拖放测试接收窗口。");
            Activate();
            PointerTestShowWindow(window, 9);
            if (!PointerTestSetWindowPos(window, new nint(-1), 0, 0, 0, 0, showWithoutActivation))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法置顶拖放测试来源窗口。");
            PointerTestSetForegroundWindow(window);
            await Task.Delay(200);
            _root.UpdateLayout();
            _dragFixturePaths = [];
            _dragFixturePaths[(_currentFolder!.Disk.Id, ((Net.YuWangItem)source.Tag).Id)] = path;
            // Every visible fixture is a complete local file. Manual tests must not
            // accidentally exercise the fake account's unavailable network client.
            foreach (var row in _items.Items.OfType<ListViewItem>())
                if (row.Tag is Net.YuWangItem item && !ReferenceEquals(row, source))
                {
                    var otherPath = Path.Combine(testRoot, item.Name);
                    await File.WriteAllTextAsync(otherPath, "browser secondary drag fixture");
                    _dragFixturePaths[(_currentFolder.Disk.Id, item.Id)] = otherPath;
                }
            _dragBackendAvailable = static () => true;
            _items.SelectedItem = source;
            if (additionalSelection is not null)
            {
                SetCheckboxSelectionEnabled(true);
                if (!source.IsSelected) _items.SelectedItems.Add(source);
                _items.SelectedItems.Add(additionalSelection);
                _root.UpdateLayout();
            }
            QueueDragPreparation();
            var warmup = _dragWarmup ?? throw new InvalidOperationException("没有提前准备的拖放对象。");
            var ready = await warmup.WaitAsync(TimeSpan.FromSeconds(5));
            if (ready.Entries.Count != sourcePaths.Count || !sourcePaths.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(ready.Entries.Select(entry => entry.Name)))
                throw new InvalidOperationException("已就绪文件的系统拖放数据不正确。");
            Trace($"Selection preparation is ready; selected={sourcePaths.Count}");
            // Keep the manifest pending until a real preview window has appeared.
            if (!externalInput) _dragWarmup = allowManifest.Task;
            // The chrome wrapper adds a caption above _root; screen coordinates must
            // include that wrapper or the injected click lands in the command bar.
            var point = source.TransformToVisual((UIElement)Content)
                .TransformPoint(new Windows.Foundation.Point(100, source.ActualHeight / 2));
            await WinUiUiHost.SaveWindowPngAsync((FrameworkElement)Content, "pointer-drag-source", testRoot);
            // Avoid LINQ's ICollection<UIElement> probe on this WinRT iterable under AOT.
            var hitNames = new List<string>();
            foreach (var element in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(point,
                (UIElement)Content))
            {
                hitNames.Add(element.GetType().Name);
                if (hitNames.Count == 6) break;
            }
            var hits = string.Join(",", hitNames);
            var origin = new PointerTestPoint();
            if (!PointerTestClientToScreen(window, ref origin)) throw new InvalidOperationException("无法定位测试窗口。");
            var scale = PointerTestGetDpiForWindow(window) / 96d;
            var startX = origin.X + (int)(point.X * scale);
            var startY = origin.Y + (int)(point.Y * scale);
            var sourceHitWindow = PointerTestWindowFromPoint(new PointerTestPoint { X = startX, Y = startY });
            var sourceHit = PointerTestGetAncestor(sourceHitWindow, 2);
            _ = PointerTestGetWindowThreadProcessId(sourceHitWindow, out var sourceHitProcessId);
            if (!PointerTestGetWindowRect(target, out var targetBounds))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var endX = (targetBounds.Left + targetBounds.Right) / 2;
            var endY = (targetBounds.Top + targetBounds.Bottom) / 2;
            var targetHitWindow = PointerTestWindowFromPoint(new PointerTestPoint { X = endX, Y = endY });
            var targetHit = PointerTestGetAncestor(targetHitWindow, 2);
            _ = PointerTestGetWindowThreadProcessId(targetHitWindow, out var targetHitProcessId);
            var xamlScale = _root.XamlRoot?.RasterizationScale ?? 1;
            coordinates = $"Source=({startX},{startY}); target=({endX},{endY}); source bounds={sourceBounds.Left},{sourceBounds.Top},{sourceBounds.Right},{sourceBounds.Bottom}; target bounds={targetBounds.Left},{targetBounds.Top},{targetBounds.Right},{targetBounds.Bottom}; browser size={AppWindow.Size.Width}x{AppWindow.Size.Height}; DPI={scale * 96}; XAML scale={xamlScale}; source hwnd={window:X}; source hit={sourceHit:X} pid={sourceHitProcessId}; target hwnd={target:X}; target hit={targetHit:X} pid={targetHitProcessId}; hits={hits}";
            Trace("Pointer drag test coordinates: " + coordinates);
            if (Math.Abs(scale - xamlScale) > 0.01)
                throw new InvalidOperationException("窗口 DPI 与 XAML 光栅缩放不同，拒绝在坐标未稳定时注入拖放。");
            // WinUI composition may return a root/child HWND distinct from WindowNative's
            // handle. Confirm both points belong to this isolated test process; actual
            // receiver DragOver and extraction below verify the target HWND end to end.
            if (sourceHitWindow == 0 || sourceHitProcessId != (uint)Environment.ProcessId)
                throw new InvalidOperationException("拖放测试起点被其他进程遮挡。");
            if (targetHitWindow == 0 || targetHitProcessId != (uint)Environment.ProcessId)
                throw new InvalidOperationException("拖放测试目标点被其他进程遮挡。");
            var gapX = (sourceBounds.Right + targetBounds.Left) / 2;
            (int Left, int Top) initialPreview = default;
            observePreview = () =>
            {
                // Esc can close the preview while OLE is still returning and the button
                // is down. Only require continuity before the injected cancellation.
                if (externalInput || cancelIssued.Task.IsCompleted || !NativeFileDrag.IsLeftButtonDown || NativeFileDrag.IsEscapeDown
                    || _dragCancellation is not { IsCancellationRequested: false }) return;
                var handle = _dragItemPreview?.Handle ?? 0;
                if (handle == 0 || !PointerTestIsWindowVisible(handle) || !PointerTestGetWindowRect(handle, out var bounds))
                {
                    if (previewHandle != 0) previewStayedVisible = false;
                    return;
                }
                if (previewHandle == 0)
                {
                    previewHandle = handle;
                    previewCountLabel = _dragItemPreview?.DisplayedItemCount;
                    previewCountMatched = previewCountLabel == expectedCountLabel;
                    initialPreview = (bounds.Left, bounds.Top);
                    previewBeforeOle = !oleStarted.Task.IsCompleted;
                    Trace($"Preview shown during manifest wait={previewBeforeOle}; hwnd={handle:X}");
                    allowManifest.TrySetResult(ready);
                    previewReady.TrySetResult();
                }
                else if (previewHandle != handle) previewStayedVisible = false;
                previewMoved |= initialPreview != (bounds.Left, bounds.Top);
                var (x, y) = NativeFileDrag.CursorPosition;
                if (!previewAtGap && x >= sourceBounds.Right && x < targetBounds.Left && oleStarted.Task.IsCompleted)
                {
                    previewAtGap = true;
                    Trace($"Preview in gap outside both windows; hwnd={handle:X}; pointer={x},{y}");
                }
                if (!previewOverReceiver && x >= targetBounds.Left && x < targetBounds.Right
                    && y >= targetBounds.Top && y < targetBounds.Bottom)
                {
                    previewOverReceiver = true;
                    // The preview is offset from the pointer. Check the real OLE
                    // hotspot, not a point at the center of the acrylic window.
                    var hit = PointerTestWindowFromPoint(new PointerTestPoint { X = x, Y = y });
                    var cursorOutsideCard = x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom;
                    previewPassedHitTest = cursorOutsideCard && PointerTestGetAncestor(hit, 2) == target
                        && PointerTestGetForegroundWindow() != handle;
                    Trace($"Preview over receiver; hwnd={handle:X}; hit at pointer={hit:X}; pointer={x},{y}; outside card={cursorOutsideCard}");
                }
            };
            bool PreviewWasKeptAndDestroyed() => externalInput || (previewMode == expectedPreviewMode && previewCountMatched
                && previewBeforeOle && previewAtGap
                && previewOverReceiver && previewPassedHitTest && previewStayedVisible && previewMoved
                && _dragItemPreview is null && !PointerTestIsWindow(previewHandle));
            string PreviewDetail() => $"Preview: mode={previewMode}, count={previewCountLabel ?? "none"}, before OLE={previewBeforeOle}, gap={previewAtGap}, receiver={previewOverReceiver}, "
                + $"drop hotspot unobstructed={previewPassedHitTest}, same and visible={previewStayedVisible}, moved={previewMoved}, "
                + $"destroyed={previewHandle != 0 && !PointerTestIsWindow(previewHandle)}.";
            statusMonitor.Start();
            if (injectedTouch)
            {
                input = Task.Run(() => InjectTouchDragAsync((startX, startY), (gapX, endY), (endX, endY), cancelGesture,
                    touchDragRecognized.Task, previewReady.Task, oleStarted.Task, Trace));
            }
            else if (!externalInput) input = Task.Run(async () =>
            {
                try
                {
                    PointerTestMoveMouse(startX, startY);
                    await Task.Delay(150);
                    var hit = PointerTestWindowFromPoint(new PointerTestPoint { X = startX, Y = startY });
                    coordinates += $"; hit root={PointerTestGetAncestor(hit, 2):X}; foreground={PointerTestGetForegroundWindow():X}";
                    PointerTestMouseEvent(2); // left down
                    await Task.Delay(180);
                    coordinates += $"; cursor={NativeFileDrag.CursorPosition}; down={NativeFileDrag.IsLeftButtonDown}";
                    var dragX = Math.Min(startX + 32, sourceBounds.Right - 1);
                    PointerTestMoveMouse(dragX, startY);
                    await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(160);
                    if (oleStarted.Task.IsCompleted) throw new InvalidOperationException("鼠标尚在文件列表内时进入了 OLE。");
                    for (var step = 1; step <= 24; step++)
                    {
                        PointerTestMoveMouse(dragX + (gapX - dragX) * step / 24, startY + (endY - startY) * step / 24);
                        await Task.Delay(25);
                    }
                    await oleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(150);
                    for (var step = 1; step <= 16; step++)
                    {
                        PointerTestMoveMouse(gapX + (endX - gapX) * step / 16, endY);
                        await Task.Delay(25);
                    }
                    await Task.Delay(180);
                    if (cancelGesture is not null) await cancelGesture();
                    PointerTestMouseEvent(4); // left up
                    await Task.Delay(600);
                    if (!dropped.Task.IsCompleted)
                    {
                        PointerTestKeyboardEvent(27, 0, 0, 0);
                        PointerTestKeyboardEvent(27, 0, 2, 0);
                    }
                }
                finally
                {
                    PointerTestMouseEvent(4);
                    PointerTestSetCursorPos(savedCursor.X, savedCursor.Y);
                }
            });
            if (input is not null) await input;
            if (cancel)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (_outboundDragActive && DateTime.UtcNow < deadline) await Task.Delay(25);
                return new UiSelfTestCheck(caseName, !_outboundDragActive
                    && !dropped.Task.IsCompleted && copyPaths.Values.All(file => !File.Exists(file)) && overCount >= 2
                    && _status.Text == "已取消拖放" && PreviewWasKeptAndDestroyed(),
                    $"Target events={overCount}; active={_outboundDragActive}; status={_status.Text}; no content extracted. {PreviewDetail()}");
            }
            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(externalInput ? 300 : injectedTouch ? 30 : 5));
            if (injectedTouch) await touchReleasedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finished = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_outboundDragActive && DateTime.UtcNow < finished) await Task.Delay(25);
            var copiedFiles = 0;
            foreach (var (name, file) in sourcePaths)
            {
                var actual = await File.ReadAllBytesAsync(copyPaths[name]);
                var expected = await File.ReadAllBytesAsync(file);
                if (actual.AsSpan().SequenceEqual(expected)) copiedFiles++;
            }
            return new UiSelfTestCheck(caseName, copiedFiles == sourcePaths.Count && !_outboundDragActive
                && (!injectedTouch || touchReleasedObserved.Task.IsCompleted)
                && overCount >= 2 && Math.Abs(lastX - firstX) > 10 && PreviewWasKeptAndDestroyed(),
                $"Target drag-over events={overCount}; X={firstX:F1} → {lastX:F1}; complete file contents={copiedFiles}/{sourcePaths.Count}; touch release observed={(injectedTouch ? touchReleasedObserved.Task.IsCompleted.ToString() : "n/a")}; pointer source={(externalInput ? "external" : injectedTouch ? "Windows touch injection" : "mouse injection")}. {PreviewDetail()}");
        }
        catch (Exception error)
        {
            var detail = $"{coordinates}; presses={presses}; moves={moves}; root presses={rootPresses}; root moves={rootMoves}; over={overCount}; X={firstX} → {lastX}; {lastStatus}; {error}";
            Trace(detail);
            return new UiSelfTestCheck(caseName, false, detail);
        }
        finally
        {
            allowManifest.TrySetCanceled();
            if (input is not null)
            {
                try { await input; }
                catch { /* The primary failure has already been recorded in the check detail. */ }
            }
            _dragBackendAvailable = previousBackend;
            _dragDiagnostic = previousDiagnostic;
            statusMonitor.Stop();
            Closed -= sourceClosed;
            _items.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            _items.RemoveHandler(UIElement.PointerMovedEvent, moved);
            pointerRoot.RemoveHandler(UIElement.PointerPressedEvent, rootPressed);
            pointerRoot.RemoveHandler(UIElement.PointerMovedEvent, rootMoved);
            pointerRoot.RemoveHandler(UIElement.PointerCaptureLostEvent, captureLost);
            if (additionalSelection is not null && !_isClosed) SetCheckboxSelectionEnabled(false);
            CancelDragWarmup();
            StopTouchDragTracking();
            receiverWindow?.Dispose();
        }
    }

    private static async Task CancelPointerTestAsync()
    {
        PointerTestKeyboardEvent(27, 0, 0, 0);
        await Task.Delay(80);
        PointerTestKeyboardEvent(27, 0, 2, 0);
        await Task.Delay(120);
    }

    private static async Task InjectTouchDragAsync((int X, int Y) source, (int X, int Y) gap,
        (int X, int Y) target, Func<Task>? cancelGesture, Task touchDragRecognized, Task previewReady, Task oleStarted, Action<string> trace)
    {
        using var touch = new InjectedTouchContact();
        touch.Down(source);
        trace($"Windows touch injection DOWN; pointer type=Touch; start={source}; feedback=None");
        try
        {
            // Match a finger drag: establish a stationary contact, then break the
            // ListView's hold/pan ambiguity with a short horizontal pull.
            var holdUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
            while (DateTime.UtcNow < holdUntil)
            {
                touch.Move(source);
                await Task.Delay(40);
            }
            var dragStart = source;
            for (var step = 1; step <= 24 && !touchDragRecognized.IsCompleted; step++)
            {
                dragStart = (source.X + step, source.Y);
                touch.Move(dragStart);
                await Task.Delay(20);
            }
            var dragDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!touchDragRecognized.IsCompleted && DateTime.UtcNow < dragDeadline)
            {
                touch.Move(dragStart);
                await Task.Delay(40);
            }
            await touchDragRecognized.WaitAsync(TimeSpan.FromSeconds(1));
            trace("Windows touch injection reached ListView drag recognition");

            var previewDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!previewReady.IsCompleted && DateTime.UtcNow < previewDeadline)
            {
                touch.Move(dragStart);
                await Task.Delay(40);
            }
            await previewReady.WaitAsync(TimeSpan.FromSeconds(1));
            for (var frame = 0; frame < 4; frame++)
            {
                touch.Move(dragStart);
                await Task.Delay(40);
            }
            if (oleStarted.IsCompleted) throw new InvalidOperationException("触点尚在文件列表内时进入了 OLE。");
            async Task MoveBetween((int X, int Y) from, (int X, int Y) to, int steps)
            {
                for (var step = 1; step <= steps; step++)
                {
                    touch.Move((from.X + (to.X - from.X) * step / steps, from.Y + (to.Y - from.Y) * step / steps));
                    await Task.Delay(25);
                }
            }
            await MoveBetween(dragStart, gap, 24);
            for (var frame = 0; frame < 4; frame++)
            {
                touch.Move(gap);
                await Task.Delay(40);
            }
            await oleStarted.WaitAsync(TimeSpan.FromSeconds(1));
            await MoveBetween(gap, target, 16);
            for (var frame = 0; frame < 5; frame++)
            {
                touch.Move(target);
                await Task.Delay(50);
            }
            if (cancelGesture is not null) await cancelGesture();
            touch.Up(); // Same point as the previous injected frame.
            trace($"Windows touch injection UP; target={target}");
        }
        finally
        {
            if (touchDragRecognized.IsCompleted && !oleStarted.IsCompleted)
                trace("Windows touch injection ended before the OLE bridge became ready");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointerTestPoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerTestRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestClientToScreen(nint window, ref PointerTestPoint point);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] private static extern uint PointerTestGetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestSetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private struct PointerTestInput { internal uint Type; internal PointerTestMouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerTestMouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    private static void PointerTestMouseEvent(uint flags)
    {
        var input = new PointerTestInput { Mouse = new PointerTestMouseInput { Flags = flags } };
        if (PointerTestSendInput(1, [input], Marshal.SizeOf<PointerTestInput>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法发送测试鼠标输入。");
    }
    private static void PointerTestMoveMouse(int x, int y)
    {
        var input = new PointerTestInput
        {
            Mouse = new PointerTestMouseInput
            {
                X = (x - PointerTestGetSystemMetrics(76)) * 65535 / (PointerTestGetSystemMetrics(78) - 1),
                Y = (y - PointerTestGetSystemMetrics(77)) * 65535 / (PointerTestGetSystemMetrics(79) - 1),
                Flags = 0xc001, // MOVE | ABSOLUTE | VIRTUALDESK generates WinUI pointer updates.
            },
        };
        if (PointerTestSendInput(1, [input], Marshal.SizeOf<PointerTestInput>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法移动测试鼠标。");
    }
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] private static extern int PointerTestGetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint PointerTestSendInput(uint count, [In] PointerTestInput[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern nint PointerTestWindowFromPoint(PointerTestPoint point);
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestIsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint PointerTestGetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestGetWindowRect(nint window, out PointerTestRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern nint PointerTestGetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint PointerTestGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool PointerTestShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool PointerTestSetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestSetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void PointerTestKeyboardEvent(byte key, byte scan, uint flags, nuint extra);
}
