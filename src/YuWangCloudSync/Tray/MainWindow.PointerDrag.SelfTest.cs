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
        bool cancel = false, bool repeat = false, bool injectedTouch = false)
    {
        NativeVirtualFileReceiverWindow? receiverWindow = null;
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var touchDragRecognized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var touchReleasedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overCount = 0;
        var firstX = double.NaN;
        var lastX = double.NaN;
        var coordinates = "";
        var suffix = cancel ? "-cancel" : repeat ? "-repeat" : "";
        var tracePath = Path.Combine(testRoot, $"pointer-drag{suffix}-trace.txt");
        var copiedPath = Path.Combine(testRoot, $"pointer-drag{suffix}-copied.txt");
        var pointerRoot = (UIElement)Content;
        var traceGate = new object();
        void Trace(string message)
        {
            if (message.Contains("OLE mouse bridge ready", StringComparison.Ordinal)) oleStarted.TrySetResult();
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
        var statusMonitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var lastStatus = "";
        statusMonitor.Tick += (_, _) =>
        {
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
                var extraction = VirtualFileDragSelfTest.ReceiveAsync(data, Path.GetFileName(path), copiedPath);
                _ = extraction.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) dropped.TrySetResult(task.Result);
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
            QueueDragPreparation();
            var warmup = _dragWarmup ?? throw new InvalidOperationException("没有提前准备的拖放对象。");
            var ready = await warmup.WaitAsync(TimeSpan.FromSeconds(5));
            if (ready.Entries.Count != 1 || ready.Entries[0].Name != Path.GetFileName(path))
                throw new InvalidOperationException("已就绪文件的系统拖放数据不正确。");
            Trace("Selection preparation is ready");
            statusMonitor.Start();
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
            if (injectedTouch)
            {
                input = Task.Run(() => InjectTouchDragAsync((startX, startY), (endX, endY), touchDragRecognized.Task,
                    oleStarted.Task, Trace));
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
                    for (var step = 1; step <= 40; step++)
                    {
                        PointerTestMoveMouse(startX + (endX - startX) * step / 40,
                            startY + (endY - startY) * step / 40);
                        await Task.Delay(25);
                    }
                    await Task.Delay(180);
                    if (cancel)
                    {
                        PointerTestKeyboardEvent(27, 0, 0, 0);
                        await Task.Delay(80);
                        PointerTestKeyboardEvent(27, 0, 2, 0);
                        await Task.Delay(120);
                    }
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
                return new UiSelfTestCheck("Pointer drag cancellation releases OLE", !_outboundDragActive
                    && !dropped.Task.IsCompleted && !File.Exists(copiedPath) && overCount >= 2
                    && _status.Text == "已取消拖放", $"Target events={overCount}; active={_outboundDragActive}; status={_status.Text}; no content extracted.");
            }
            var text = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(externalInput ? 300 : injectedTouch ? 30 : 5));
            if (injectedTouch) await touchReleasedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finished = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_outboundDragActive && DateTime.UtcNow < finished) await Task.Delay(25);
            return new UiSelfTestCheck(injectedTouch ? "Injected touch drag reaches native receiver"
                : repeat ? "Pointer drag repeats after copy" : "Pointer to virtual-file OLE real copy", text == await File.ReadAllTextAsync(path)
                && !_outboundDragActive
                && (!injectedTouch || touchReleasedObserved.Task.IsCompleted)
                && overCount >= 2 && Math.Abs(lastX - firstX) > 10,
                $"Target drag-over events={overCount}; X={firstX:F1} → {lastX:F1}; copied complete file contents; actual pointer release observed={(!injectedTouch || touchReleasedObserved.Task.IsCompleted)}; pointer source={(injectedTouch ? "Windows touch injection" : "mouse injection")}.");
        }
        catch (Exception error)
        {
            var detail = $"{coordinates}; presses={presses}; moves={moves}; root presses={rootPresses}; root moves={rootMoves}; over={overCount}; X={firstX} → {lastX}; {lastStatus}; {error}";
            Trace(detail);
            return new UiSelfTestCheck(injectedTouch ? "Injected touch drag reaches native receiver" : "WinUI pointer to OLE real copy", false, detail);
        }
        finally
        {
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
            CancelDragWarmup();
            StopTouchDragTracking();
            receiverWindow?.Dispose();
        }
    }

    private static async Task InjectTouchDragAsync((int X, int Y) source, (int X, int Y) target,
        Task touchDragRecognized, Task oleStarted, Action<string> trace)
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

            var oleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!oleStarted.IsCompleted && DateTime.UtcNow < oleDeadline)
            {
                touch.Move(dragStart);
                await Task.Delay(40);
            }
            await oleStarted.WaitAsync(TimeSpan.FromSeconds(1));
            for (var step = 1; step <= 40; step++)
            {
                var last = (dragStart.X + (target.X - dragStart.X) * step / 40,
                    dragStart.Y + (target.Y - dragStart.Y) * step / 40);
                touch.Move(last);
                await Task.Delay(25);
            }
            for (var frame = 0; frame < 5; frame++)
            {
                touch.Move(target);
                await Task.Delay(50);
            }
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
