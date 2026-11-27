using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace YuWangCloudSync.Tray;

internal sealed unsafe partial class NativeFileDrag
{
    // Keep WinUI's dispatcher free to process captured touch movement and release while OLE
    // runs its nested loop on a dedicated STA. OleMouseInput bridges the contact to that loop.
    internal static Task<bool> DropVirtualAsync(VirtualFileDataObject data, Func<bool> canContinue,
        Action<string>? diagnostic = null, Func<(int X, int Y), bool>? isOutsideSource = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(DropVirtualCore(data, canContinue, diagnostic, isOutsideSource));
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { data.Dispose(); }
        }) { IsBackground = true, Name = "Virtual file drag" };
        data.Retain();
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { data.Dispose(); throw; }
        return completion.Task;
    }

    private static bool DropVirtualCore(VirtualFileDataObject data, Func<bool> canContinue,
        Action<string>? diagnostic, Func<(int X, int Y), bool>? isOutsideSource)
    {
        // The UI owns the image for both input types. No Shell drag-image formats
        // are attached to the virtual file data object.
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try
        {
            data.SetSourceThread();
            var source = new FileDropSource(canContinue, diagnostic);
            var pointer = ComInterfaceMarshaller<IFileDropSource>.ConvertToUnmanaged(source);
            try
            {
                using var input = new OleMouseInput(canContinue, diagnostic, isOutsideSource);
                diagnostic?.Invoke($"OLE mouse bridge ready; thread={Environment.CurrentManagedThreadId}");
                var hr = DoDragDrop(data.Pointer, (nint)pointer, 1, out var effect);
                input.ThrowIfFailed();
                diagnostic?.Invoke($"OLE finished; HRESULT=0x{hr:X8}; effect={effect}");
                if (hr < 0) throw new COMException($"启动系统拖放失败（HRESULT 0x{hr:X8}）。", hr);
                return hr == 0x40100 && effect == 1;
            }
            finally { ComInterfaceMarshaller<IFileDropSource>.Free(pointer); GC.KeepAlive(source); }
        }
        finally { OleUninitialize(); }
    }

    private sealed class OleMouseInput : IDisposable
    {
        private readonly object _gate = new();
        private readonly uint _thread = GetCurrentThreadId();
        private readonly Func<bool> _canContinue;
        private readonly Action<string>? _diagnostic;
        private readonly Func<(int X, int Y), bool>? _isOutsideSource;
        private readonly System.Threading.Timer _timer;
        private Exception? _failure;
        private bool _stopped;
        private long _lastTouchLog;
        private bool? _lastTouchOutside;

        internal OleMouseInput(Func<bool> canContinue, Action<string>? diagnostic, Func<(int X, int Y), bool>? isOutsideSource)
        {
            _canContinue = canContinue;
            _diagnostic = diagnostic;
            _isOutsideSource = isOutsideSource;
            // OLE initialization waits for its first WM_MOUSE message before capture.
            // A worker has no such message, and WinUI sends WM_POINTER on the UI thread.
            PeekMessageW(out _, 0, 0, 0, 0);
            Forward();
            _timer = new(_ => Tick(), null, 16, 16);
        }

        private void Tick()
        {
            try { Forward(); }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _failure ??= error;
                    PostThreadMessageW(_thread, 0x0100, 27, 0);
                    _stopped = true;
                }
            }
        }

        internal void ThrowIfFailed()
        {
            lock (_gate) if (_failure is { } error) throw new IOException("拖放鼠标消息传递失败。", error);
        }

        private void Forward()
        {
            lock (_gate)
            {
                if (_stopped) return;
                var cancel = IsEscapeDown || !_canContinue();
                var down = IsLeftButtonDown;
                var cursor = CursorPosition;
                if (IsTouchContactActive && _isOutsideSource is not null)
                {
                    var outside = _isOutsideSource(cursor);
                    var now = Environment.TickCount64;
                    if (now - _lastTouchLog >= 500 || _lastTouchOutside != outside)
                    {
                        _lastTouchLog = now;
                        _lastTouchOutside = outside;
                        _diagnostic?.Invoke($"Touch OLE bridge sample; at={cursor}; contact={down}; outsideWindow={outside}");
                    }
                }
                var position = (nint)((uint)(ushort)cursor.X | ((uint)(ushort)cursor.Y << 16));
                var message = cancel ? 0x0100u : down ? 0x0200u : 0x0202u;
                nuint keys = cancel ? 27u : down ? 1u : 0u;
                if (!cancel)
                {
                    if ((GetAsyncKeyState(16) & 0x8000) != 0) keys |= 4;
                    if ((GetAsyncKeyState(17) & 0x8000) != 0) keys |= 8;
                }
                if (!PostThreadMessageW(_thread, message, keys, cancel ? 0 : position))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法传递拖放鼠标消息。");
                if (cancel || !down) _stopped = true;
            }
        }

        public void Dispose()
        {
            // An in-flight callback must finish before the native thread can exit or be reused.
            lock (_gate) _stopped = true;
            _timer.Dispose();
        }
    }

    /* 原 Shell 位图与仅准备阶段显示的预览，暂时停用；保留供对照。
    // DropVirtualCore 之前接收 visual：在 DoDragDrop 前，把位图写到 OLE 数据对象。
    // if (visual is not null) drag.SetItemVisual(visual);
    private sealed record DragItemVisual(byte[] Pixels, int Width, int Height, int OffsetX, int OffsetY);

    private void SetItemVisual(DragItemVisual visual)
    {
        var info = new BitmapInfo { Size = 40, Width = visual.Width, Height = -visual.Height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(0, ref info, 0, out var pixels, 0, 0);
        if (bitmap == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Marshal.Copy(visual.Pixels, 0, pixels, visual.Pixels.Length);
            var image = new DragImage
            {
                Size = new Point { X = visual.Width, Y = visual.Height },
                Offset = new Point { X = visual.OffsetX, Y = visual.OffsetY },
                Bitmap = bitmap, ColorKey = 0xffffffff,
            };
            var initialize = (delegate* unmanaged[Stdcall]<nint, DragImage*, nint, int>)(*(nint**)_helper)[3];
            var hr = initialize(_helper, &image, _data);
            if (hr < 0) throw new COMException($"初始化拖拽外观失败（HRESULT 0x{hr:X8}）。", hr);
            bitmap = 0;
        }
        finally { if (bitmap != 0) DeleteObject(bitmap); }
    }

    // Metadata for a large folder may still be arriving on the first selection. Show
    // the very same row visual immediately, with no preparing label or content IO.
    internal void ShowItemPreview(DragItemVisual visual)
    {
        _previewOffsetX = -visual.OffsetX; _previewOffsetY = -visual.OffsetY;
        _preview = CreateWindowExW(0x080800A8, "STATIC", "", 0x80000000,
            0, 0, visual.Width, visual.Height, 0, 0, 0, 0);
        if (_preview == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var info = new BitmapInfo { Size = 40, Width = visual.Width, Height = -visual.Height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(0, ref info, 0, out var pixels, 0, 0);
        if (bitmap == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        nint dc = 0, previous = 0;
        try
        {
            Marshal.Copy(visual.Pixels, 0, pixels, visual.Pixels.Length);
            dc = CreateCompatibleDC(0);
            if (dc == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(dc, bitmap);
            var source = new Point();
            var size = new Point { X = visual.Width, Y = visual.Height };
            var cursor = CursorPosition;
            var destination = new Point { X = cursor.X + _previewOffsetX, Y = cursor.Y + _previewOffsetY };
            var blend = new BlendFunction { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(_preview, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(_preview, -1, destination.X, destination.Y, visual.Width, visual.Height, 0x0050);
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
            if (dc != 0) DeleteDC(dc);
            DeleteObject(bitmap);
        }
    }
    */

    internal static void ReleaseMouseCapture() => ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [StructLayout(LayoutKind.Sequential)] private struct MouseMessage
    { internal nint Window; internal uint Id; internal nuint Keys; internal nint Position; internal uint Time; internal Point Screen; internal uint Private; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out MouseMessage message, nint window, uint min, uint max, uint remove);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wparam, nint lparam);
}
