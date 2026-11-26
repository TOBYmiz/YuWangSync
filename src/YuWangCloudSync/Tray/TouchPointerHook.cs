using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace YuWangCloudSync.Tray;

internal enum PointerContact { InContact, Lifted, Unavailable }

/// <summary>
/// Follows one touch/pen contact through WM_POINTER messages on the WinUI window thread.
/// WM_POINTER input is posted to the window thread's queue, so WH_GETMESSAGE is required;
/// WH_CALLWNDPROC only observes sent messages and misses the contact entirely.
/// </summary>
internal static class TouchPointerHook
{
    private const int WhGetMessage = 3;
    private const uint PointerUpdate = 0x0245, PointerDown = 0x0246, PointerUp = 0x0247;
    private const uint InContactFlag = 0x4, CanceledFlag = 0x8000;

    private static readonly HookProc PointerHook = ObservePointerMessage;
    private static nint _window, _hook, _lastDownWindow, _pointerWindow;
    private static uint _lastDownId, _trackedId, _flags, _message;
    private static long _hookCallbacks;
    private static uint _lastQueueMessage;
    private static nint _lastQueueWindow;
    private static bool _tracking, _seen, _inContact, _positionFromPointerInfo, _positionFromWinUi;
    private static (int X, int Y) _position;
    private static Action<string>? _diagnostic;

    /// <summary>Observes pointer messages sent to any WinUI HWND on the window's UI thread.</summary>
    internal static void Install(nint window, Action<string>? diagnostic = null)
    {
        if (window == 0) throw new ArgumentException("主窗口句柄无效。", nameof(window));
        if (_window == window && _hook != 0) return;
        if (_window != 0) Uninstall(_window);

        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0)
        {
            Serilog.Log.Warning("安装触摸指针消息监听失败；无法获取窗口线程；error={Error}", Marshal.GetLastWin32Error());
            return;
        }

        _hook = SetWindowsHookExW(WhGetMessage, PointerHook, 0, thread);
        if (_hook == 0)
        {
            Serilog.Log.Warning("安装触摸指针线程监听失败；thread={Thread}; error={Error}", thread, Marshal.GetLastWin32Error());
            return;
        }

        _window = window;
        _diagnostic = diagnostic;
        _lastDownId = _trackedId = _flags = _message = 0;
        _lastDownWindow = _pointerWindow = 0;
        _hookCallbacks = 0;
        _lastQueueMessage = 0;
        _lastQueueWindow = 0;
        _tracking = _seen = _inContact = _positionFromPointerInfo = _positionFromWinUi = false;
        Serilog.Log.Information("触摸指针线程监听已就绪；window=0x{Window:X}; thread={Thread}", (nuint)window, thread);
    }

    internal static void Uninstall(nint window)
    {
        if (_window != window || _window == 0) return;
        _tracking = false;
        if (_hook != 0 && !UnhookWindowsHookEx(_hook))
            Serilog.Log.Debug("移除触摸指针线程监听失败；error={Error}", Marshal.GetLastWin32Error());
        _hook = _window = 0;
        _diagnostic = null;
    }

    /// <summary>Bind the WinUI pointer ID directly; some WinUI input paths do not expose WM_POINTERDOWN.</summary>
    internal static PointerContact BindXamlPointer(uint pointerId, out (int X, int Y) position, out string detail)
    {
        _trackedId = pointerId;
        _tracking = pointerId != 0;
        _seen = _inContact = false;
        _positionFromPointerInfo = _positionFromWinUi = false;
        _pointerWindow = 0;
        _message = 0;
        position = default;
        if (!_tracking)
        {
            detail = "WinUI pointer id is zero";
            return PointerContact.Unavailable;
        }

        if (TryReadPointerSample(pointerId, out detail))
        {
            position = _position;
            return _inContact ? PointerContact.InContact : PointerContact.Lifted;
        }

        detail = $"GetPointerInfo failed for XAML pointer {_trackedId}; {detail}; last WM_POINTERDOWN={_lastDownId}; hookCalls={_hookCallbacks}; lastQueue=0x{_lastQueueMessage:X4} hwnd=0x{(nuint)_lastQueueWindow:X}";
        return PointerContact.Unavailable;
    }

    internal static void Stop() => _tracking = false;

    internal static bool IsOutsideWindow((int X, int Y) position)
    {
        if (_window == 0 || !GetWindowRect(_window, out var bounds)) return false;
        return position.X < bounds.Left || position.X >= bounds.Right
            || position.Y < bounds.Top || position.Y >= bounds.Bottom;
    }

    internal static PointerContact Read(out (int X, int Y) position, out string detail)
    {
        position = _position;
        if (!_tracking) { detail = $"no touch pointer bound; last WM_POINTERDOWN={_lastDownId}"; return PointerContact.Unavailable; }
        var infoAvailable = TryReadPointerSample(_trackedId, out var infoDetail);
        position = _position;
        if (!_seen)
        {
            detail = $"no pointer info for id {_trackedId}; hook={(_hook != 0 ? "installed" : "unavailable")}; hookCalls={_hookCallbacks}; lastQueue=0x{_lastQueueMessage:X4} hwnd=0x{(nuint)_lastQueueWindow:X}; {infoDetail}";
            return PointerContact.Unavailable;
        }
        if (!infoAvailable) detail = $"{infoDetail}; using last pointer sample; {LastMessageDetail}";
        else detail = infoDetail;
        return _inContact ? PointerContact.InContact : PointerContact.Lifted;
    }

    internal static bool TryGetPosition(uint pointerId, out (int X, int Y) position, out string detail)
    {
        if (!TryReadPointerSample(pointerId, out detail))
        {
            position = default;
            return false;
        }
        position = _position;
        return true;
    }

    private static bool TryReadPointerSample(uint pointerId, out string detail)
    {
        if (TryReadPointerInfo(pointerId, out detail)) return true;
        var nativeDetail = detail;
        if (TryReadWinUiPointerPoint(pointerId, out _, out _, out var winUiDetail))
        {
            detail = $"{nativeDetail}; {winUiDetail}";
            return true;
        }
        detail = $"{nativeDetail}; {winUiDetail}";
        return false;
    }

    private static bool TryReadWinUiPointerPoint(uint pointerId, out (int X, int Y) position,
        out bool inContact, out string detail)
    {
        position = default;
        inContact = false;
        try
        {
            var point = PointerPoint.GetCurrentPoint(pointerId);
            var pointDip = point.Position;
            var scale = _window != 0 ? GetDpiForWindow(_window) / 96d : 1d;
            var origin = new Point();
            if (_window != 0) _ = ClientToScreen(_window, ref origin);
            position = (origin.X + (int)Math.Round(pointDip.X * scale), origin.Y + (int)Math.Round(pointDip.Y * scale));
            inContact = point.IsInContact;
            detail = $"WinUI PointerPoint id={pointerId}; contact={inContact}; atDip=({pointDip.X:F1},{pointDip.Y:F1}); atScreen={position}";
            if (pointerId == _trackedId)
            {
                _seen = true;
                _pointerWindow = _window;
                _position = position;
                _inContact = inContact;
                _positionFromPointerInfo = false;
                _positionFromWinUi = true;
            }
            return true;
        }
        catch (Exception error)
        {
            detail = $"WinUI PointerPoint failed for id={pointerId}; {error.GetType().Name}; {error.Message}";
            return false;
        }
    }

    private static string LastMessageDetail
        => $"msg=0x{_message:X4}; hwnd=0x{(nuint)_pointerWindow:X}; flags=0x{_flags:X}; position={(_positionFromPointerInfo ? "GetPointerInfo" : _positionFromWinUi ? "WinUI PointerPoint" : "WM_POINTER lParam")}; at={_position}";

    private static bool TryReadPointerInfo(out string detail)
        => TryReadPointerInfo(_trackedId, out detail);

    private static bool TryReadPointerInfo(uint pointerId, out string detail)
    {
        if (!GetPointerInfo(pointerId, out var pointerInfo))
        {
            var error = Marshal.GetLastWin32Error();
            detail = $"GetPointerInfo error={error}; id={pointerId}";
            return false;
        }

        var pointerPosition = (pointerInfo.PixelLocation.X, pointerInfo.PixelLocation.Y);
        var pointerFlags = pointerInfo.PointerFlags;
        if (pointerId == _trackedId)
        {
            _seen = true;
            _pointerWindow = pointerInfo.TargetWindow;
            _position = pointerPosition;
            _flags = pointerFlags;
            _positionFromPointerInfo = true;
            _positionFromWinUi = false;
            _inContact = (_flags & InContactFlag) != 0 && (_flags & CanceledFlag) == 0;
        }
        detail = $"GetPointerInfo type={pointerInfo.PointerType}; id={pointerId}; hwnd=0x{(nuint)pointerInfo.TargetWindow:X}; flags=0x{pointerFlags:X}; at={pointerPosition}";
        return true;
    }

    private static nint ObservePointerMessage(int code, nuint wParam, nint lParam)
    {
        if (code >= 0 && lParam != 0)
        {
            try
            {
                var message = Marshal.PtrToStructure<ThreadMessage>(lParam);
                _hookCallbacks++;
                _lastQueueMessage = message.Message;
                _lastQueueWindow = message.Window;
                ObservePointerMessage(message.Message, message.WParam, message.LParam, message.Window);
            }
            catch (Exception error)
            {
                Serilog.Log.Debug(error, "读取触摸指针窗口消息失败");
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void ObservePointerMessage(uint message, nuint wParam, nint lParam, nint targetWindow)
    {
        if (message is < PointerUpdate or > PointerUp) return;
        var id = (uint)(wParam & 0xFFFF);
        var flags = (uint)((wParam >> 16) & 0xFFFF);
        if (message == PointerDown)
        {
            _lastDownId = id;
            _lastDownWindow = targetWindow;
        }
        if (!_tracking || id != _trackedId) return;

        _seen = true;
        _message = message;
        _flags = flags;
        if (GetPointerInfo(id, out var pointerInfo))
        {
            _pointerWindow = pointerInfo.TargetWindow;
            _position = (pointerInfo.PixelLocation.X, pointerInfo.PixelLocation.Y);
            _flags = pointerInfo.PointerFlags;
            _positionFromPointerInfo = true;
            _positionFromWinUi = false;
        }
        else
        {
            _pointerWindow = targetWindow;
            // WM_POINTER lParam carries signed physical screen coordinates.
            _position = ((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            _positionFromPointerInfo = false;
            _positionFromWinUi = false;
        }
        _inContact = message != PointerUp
            && (_flags & InContactFlag) != 0 && (_flags & CanceledFlag) == 0;
        NativeFileDrag.ObserveTouchPointer(_position, _inContact);
        if (message == PointerUp || (_flags & CanceledFlag) != 0)
            _diagnostic?.Invoke($"Touch pointer contact ended; id={id}; message=0x{message:X4}; at={_position}; window=0x{(nuint)targetWindow:X}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePointerInfo
    {
        internal uint PointerType, PointerId, FrameId, PointerFlags;
        internal nint SourceDevice, TargetWindow;
        internal Point PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        internal uint Time, HistoryCount;
        internal int InputData;
        internal uint KeyStates;
        internal ulong PerformanceCount;
        internal uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc hookProc, nint module, uint threadId);

    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("user32.dll", EntryPoint = "ClientToScreen", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", SetLastError = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetPointerInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerInfo(uint pointerId, out NativePointerInfo pointerInfo);
}
