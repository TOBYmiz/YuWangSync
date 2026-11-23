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
