using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    // Opt-in desktop integration test. Move a real mouse pointer from this isolated
    // browser into a second test window and verify the receiving application can copy.
    private async Task<UiSelfTestCheck> CheckWinUiPointerDragAsync(ListViewItem source, string path, string testRoot,
        bool cancel = false, bool repeat = false)
    {
        nint target = 0;
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        { lock (traceGate) File.AppendAllText(tracePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        var presses = 0;
        var moves = 0;
        var rootPresses = 0;
        var rootMoves = 0;
        Microsoft.UI.Xaml.Input.PointerEventHandler rootPressed = (_, _) => rootPresses++;
        Microsoft.UI.Xaml.Input.PointerEventHandler rootMoved = (_, _) => rootMoves++;
        pointerRoot.AddHandler(UIElement.PointerPressedEvent, rootPressed, handledEventsToo: true);
        pointerRoot.AddHandler(UIElement.PointerMovedEvent, rootMoved, handledEventsToo: true);
        Microsoft.UI.Xaml.Input.PointerEventHandler pressed = (_, _) => { presses++; Trace("List pointer pressed"); };
        Microsoft.UI.Xaml.Input.PointerEventHandler moved = (_, _) => moves++;
        _items.AddHandler(UIElement.PointerPressedEvent, pressed, handledEventsToo: true);
        _items.AddHandler(UIElement.PointerMovedEvent, moved, handledEventsToo: true);
        IDisposable? receiverRegistration = null;
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
            // AppWindow sizes are physical pixels; the browser enforces a DIP minimum.
            // Place the receiver after that *actual* width so high DPI cannot hide it.
            var targetX = AppWindow.Position.X + AppWindow.Size.Width + 30;
            // Use a native window so WinUI's child InputSite drop target cannot intercept the receiver.
            target = PointerTestCreateWindow(0, "STATIC", "拖放回归接收窗口", 0x10cf0000,
                targetX, 160, 800, 640, 0, 0, 0, 0);
            if (target == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var receiver = new NativeVirtualFileReceiver(x =>
            {
                if (double.IsNaN(firstX)) firstX = x;
                lastX = x; overCount++;
            }, data =>
            {
                var extraction = VirtualFileDragSelfTest.ReceiveAsync(data, Path.GetFileName(path), copiedPath);
                _ = extraction.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) dropped.TrySetResult(task.Result);
                    else if (task.IsCanceled) dropped.TrySetCanceled();
                    else dropped.TrySetException(task.Exception!.GetBaseException());
                }, TaskScheduler.Default);
                return extraction;
            });
            receiverRegistration = NativeVirtualFileReceiver.Register(target, receiver);
            Activate();
            var window = WindowNative.GetWindowHandle(this);
            PointerTestShowWindow(window, 9);
            PointerTestSetForegroundWindow(window);
            await Task.Delay(200);
            _root.UpdateLayout();
            _downloadedPaths[(_currentFolder!.Disk.Id, ((Net.YuWangItem)source.Tag).Id)] = path;
            // Every visible fixture is a complete local file. Manual tests must not
            // accidentally exercise the fake account's unavailable network client.
            foreach (var row in _items.Items.OfType<ListViewItem>())
                if (row.Tag is Net.YuWangItem item && !ReferenceEquals(row, source))
                {
                    var otherPath = Path.Combine(testRoot, item.Name);
                    await File.WriteAllTextAsync(otherPath, "browser secondary drag fixture");
                    _downloadedPaths[(_currentFolder.Disk.Id, item.Id)] = otherPath;
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
            var endX = targetX + 600;
            var endY = 480;
            coordinates = $"Source=({startX},{startY}); target=({endX},{endY}); browser width={AppWindow.Size.Width}; DPI={scale * 96}; hwnd={window:X}; hits={hits}";
            if (!externalInput) input = Task.Run(async () =>
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
            var text = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(externalInput ? 300 : 5));
            var finished = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_outboundDragActive && DateTime.UtcNow < finished) await Task.Delay(25);
            return new UiSelfTestCheck(repeat ? "Pointer drag repeats after copy" : "Pointer to virtual-file OLE real copy", text == await File.ReadAllTextAsync(path)
                && !_outboundDragActive
                && overCount >= 2 && Math.Abs(lastX - firstX) > 10,
                $"Target drag-over events={overCount}; X={firstX:F1} → {lastX:F1}; copied complete file contents.");
        }
        catch (Exception error)
        {
            var detail = $"{coordinates}; presses={presses}; moves={moves}; root presses={rootPresses}; root moves={rootMoves}; over={overCount}; X={firstX} → {lastX}; {lastStatus}; {error}";
            Trace(detail);
            return new UiSelfTestCheck("WinUI pointer to OLE real copy", false, detail);
        }
        finally
        {
            if (input is not null) await input;
            _dragBackendAvailable = previousBackend;
            _dragDiagnostic = previousDiagnostic;
            statusMonitor.Stop();
            Closed -= sourceClosed;
            _items.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            _items.RemoveHandler(UIElement.PointerMovedEvent, moved);
            pointerRoot.RemoveHandler(UIElement.PointerPressedEvent, rootPressed);
            pointerRoot.RemoveHandler(UIElement.PointerMovedEvent, rootMoved);
            CancelDragWarmup();
            receiverRegistration?.Dispose();
            if (target != 0) PointerTestDestroyWindow(target);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointerTestPoint { internal int X, Y; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint PointerTestCreateWindow(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PointerTestDestroyWindow(nint window);
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
    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern nint PointerTestGetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint PointerTestGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool PointerTestShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool PointerTestSetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void PointerTestKeyboardEvent(byte key, byte scan, uint flags, nuint extra);
}
