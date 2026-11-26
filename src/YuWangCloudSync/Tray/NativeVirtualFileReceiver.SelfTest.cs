using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace YuWangCloudSync.Tray;

[StructLayout(LayoutKind.Sequential)]
internal struct VirtualDropPoint { internal int X, Y; }

[GeneratedComInterface, Guid("00000122-0000-0000-C000-000000000046")]
internal partial interface IVirtualFileDropTarget
{
    [PreserveSig] int DragEnter(nint data, uint keys, VirtualDropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, VirtualDropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(nint data, uint keys, VirtualDropPoint point, ref uint effect);
}

[GeneratedComClass]
internal sealed partial class NativeVirtualFileReceiver(Action<int> over, Func<nint, Task> receive) : IVirtualFileDropTarget
{
    private bool _accepted;
    public int DragEnter(nint data, uint keys, VirtualDropPoint point, ref uint effect)
    {
        try { _accepted = VirtualFileDragSelfTest.SupportsDescriptors(data); return DragOver(keys, point, ref effect); }
        catch (Exception error) { effect = 0; return Marshal.GetHRForException(error); }
    }
    public int DragOver(uint keys, VirtualDropPoint point, ref uint effect)
    { effect = _accepted ? effect & 1u : 0; over(point.X); return 0; }
    public int DragLeave() { _accepted = false; return 0; }
    public int Drop(nint data, uint keys, VirtualDropPoint point, ref uint effect)
    {
        try { effect = _accepted ? effect & 1u : 0; if (effect != 0) _ = receive(data); return 0; }
        catch (Exception error) { effect = 0; return Marshal.GetHRForException(error); }
    }

    internal static unsafe IDisposable Register(nint window, NativeVirtualFileReceiver receiver)
    {
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        var pointer = ComInterfaceMarshaller<IVirtualFileDropTarget>.ConvertToUnmanaged(receiver);
        try
        {
            RevokeDragDrop(window); // This isolated test window has no other drop behavior to restore.
            Marshal.ThrowExceptionForHR(RegisterDragDrop(window, (nint)pointer));
            return new Registration(window, receiver);
        }
        catch { OleUninitialize(); throw; }
        finally { ComInterfaceMarshaller<IVirtualFileDropTarget>.Free(pointer); }
    }
    private sealed class Registration(nint window, NativeVirtualFileReceiver receiver) : IDisposable
    { public void Dispose() { RevokeDragDrop(window); OleUninitialize(); GC.KeepAlive(receiver); } }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, nint target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
}

/// <summary>Runs the isolated OLE drop target on its own STA with a normal Win32 message loop.</summary>
internal sealed class NativeVirtualFileReceiverWindow : IDisposable
{
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly uint _threadId;
    private nint _window;

    internal NativeVirtualFileReceiverWindow(string title, int x, int y, int width, int height,
        Func<NativeVirtualFileReceiver> createReceiver)
    {
        _thread = new Thread(() => Run(title, x, y, width, height, createReceiver))
        { IsBackground = true, Name = "Virtual file drop receiver" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Handle = _ready.Task.GetAwaiter().GetResult();
        _threadId = GetWindowThreadProcessId(Handle, out _);
        if (_threadId == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal nint Handle { get; }

    private void Run(string title, int x, int y, int width, int height,
        Func<NativeVirtualFileReceiver> createReceiver)
    {
        IDisposable? registration = null;
        try
        {
            _window = CreateWindowExW(0, "STATIC", title, 0x10CF0000, x, y, width, height,
                0, 0, 0, 0);
            if (_window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            registration = NativeVirtualFileReceiver.Register(_window, createReceiver());
            PeekMessageW(out _, 0, 0, 0, 0); // Create this thread's message queue before publishing readiness.
            _ready.TrySetResult(_window);
            while (true)
            {
                var result = GetMessageW(out var message, 0, 0, 0);
                if (result <= 0) break;
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        catch (Exception error) { _ready.TrySetException(error); }
        finally
        {
            registration?.Dispose();
            if (_window != 0) DestroyWindow(_window);
            _window = 0;
        }
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            PostThreadMessageW(_threadId, 0x0012, 0, 0); // WM_QUIT
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("独立拖放接收窗口未能正常关闭。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal NativePoint Position;
        internal uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessageW(out NativeMessage message,
        nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessageW(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessageW(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessageW(uint thread,
        uint message, nuint wParam, nint lParam);
}
