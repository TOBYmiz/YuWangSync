using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YuWangCloudSync.Tray;

/// <summary>Injects one Windows touch contact for the opt-in desktop drag self-test.</summary>
internal sealed class InjectedTouchContact : IDisposable
{
    private const uint PointerTypeTouch = 2;
    private const uint PointerInRange = 0x00000002;
    private const uint PointerInContact = 0x00000004;
    private const uint PointerDown = 0x00010000;
    private const uint PointerUpdate = 0x00020000;
    private const uint PointerUp = 0x00040000;
    private const uint TouchFeedbackNone = 3;
    private static readonly object InitializeGate = new();
    private static bool _initialized;
    private bool _active;
    private (int X, int Y) _position;

    internal InjectedTouchContact()
    {
        lock (InitializeGate)
        {
            if (_initialized) return;
            if (!InitializeTouchInjection(1, TouchFeedbackNone))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "初始化 Windows 触摸注入失败。");
            _initialized = true;
        }
    }

    internal void Down((int X, int Y) position)
    {
        if (_active) throw new InvalidOperationException("触摸注入已经按下。");
        Inject(PointerInRange | PointerInContact | PointerDown, position);
        _position = position;
        _active = true;
    }

    internal void Move((int X, int Y) position)
    {
        if (!_active) throw new InvalidOperationException("触摸注入尚未按下。");
        Inject(PointerInRange | PointerInContact | PointerUpdate, position);
        _position = position;
    }

    internal void Up()
    {
        if (!_active) return;
        // InjectTouchInput requires an UP frame to use the same pixel location as the prior frame.
        Inject(PointerUp, _position);
        _active = false;
    }

    private static void Inject(uint flags, (int X, int Y) position)
    {
        var contact = new PointerTouchInfo
        {
            PointerInfo = new NativePointerInfo
            {
                PointerType = PointerTypeTouch,
                PointerId = 0,
                PointerFlags = flags,
                PixelLocation = new NativePoint { X = position.X, Y = position.Y },
            },
            TouchMask = 0x00000001 | 0x00000002 | 0x00000004, // CONTACTAREA | ORIENTATION | PRESSURE
            Contact = new NativeRect
            {
                Left = position.X - 2,
                Top = position.Y - 2,
                Right = position.X + 2,
                Bottom = position.Y + 2,
            },
            Orientation = 90,
            Pressure = 32000,
        };
        if (!InjectTouchInput(1, [contact]))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"注入触摸指针帧失败；flags=0x{flags:X}; position={position}。");
    }

    public void Dispose()
    {
        if (!_active) return;
        try { Up(); }
        catch { _active = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePointerInfo
    {
        internal uint PointerType, PointerId, FrameId, PointerFlags;
        internal nint SourceDevice, TargetWindow;
        internal NativePoint PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        internal uint Time, HistoryCount;
        internal int InputData;
        internal uint KeyStates;
        internal ulong PerformanceCount;
        internal uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        internal NativePointerInfo PointerInfo;
        internal uint TouchFlags, TouchMask;
        internal NativeRect Contact, ContactRaw;
        internal uint Orientation, Pressure;
    }

    [DllImport("user32.dll", EntryPoint = "InitializeTouchInjection", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);

    [DllImport("user32.dll", EntryPoint = "InjectTouchInput", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);
}
