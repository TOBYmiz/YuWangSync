using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace YuWangCloudSync.Tray;

internal sealed unsafe partial class NativeFileDrag
{
    internal static bool CheckOriginalItemVisual(DragItemVisual visual)
    {
        void Exercise()
        {
            using var data = new VirtualFileDataObject([new("外观测试.txt", false, 0, null)], CancellationToken.None);
            using var drag = new NativeFileDrag(preparing: false);
            Marshal.Release(drag._data);
            drag._data = data.Pointer;
            Marshal.AddRef(drag._data);
            drag.SetItemVisual(visual);
            drag.ShowItemPreview(visual);
            drag.MovePreparationImage();
            drag.HidePreparationImage();
            if (!VirtualFileDragSelfTest.SupportsDescriptors(data.Pointer)) throw new IOException("拖拽图像覆盖了虚拟文件描述符。");
        }
        Exercise();
        var process = GetCurrentProcess();
        var gdi = GetGuiResources(process, 0); var user = GetGuiResources(process, 1);
        for (var i = 0; i < 5; i++) Exercise();
        return GetGuiResources(process, 0) <= gdi + 1 && GetGuiResources(process, 1) <= user + 1;
    }

    // Exercises the actual Shell IDataObject and generated IDropSource ABI without entering OLE's loop.
    internal static bool CheckNativeDataObject(string path)
    {
        using var drag = new NativeFileDrag();
        var format = new FormatEtc { Format = 15, Aspect = 1, Index = -1, Medium = 1 };
        var vtable = *(nint**)drag._data;
        var query = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, int>)vtable[5];
        var getData = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, StorageMedium*, int>)vtable[3];
        // The Shell may advertise an empty HDROP even with zero PIDLs. It must contain no paths.
        var initial = new StorageMedium();
        if (getData(drag._data, &format, &initial) >= 0)
        {
            try { if (DragQueryFileW(initial.Handle, uint.MaxValue, null, 0) != 0) return false; }
            finally { ReleaseStgMedium(ref initial); }
        }
        drag.SetPaths([path]);
        drag.SetImage(preparing: false);
        drag.HidePreparationImage();
        // SetPaths replaces the preparation object. Read the new object's vtable.
        vtable = *(nint**)drag._data;
        query = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, int>)vtable[5];
        getData = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, StorageMedium*, int>)vtable[3];
        if (query(drag._data, &format) != 0) return false;
        var medium = new StorageMedium();
        Marshal.ThrowExceptionForHR(getData(drag._data, &format, &medium));
        try
        {
            var name = new StringBuilder(32768);
            if (DragQueryFileW(medium.Handle, uint.MaxValue, null, 0) != 1
                || DragQueryFileW(medium.Handle, 0, name, (uint)name.Capacity) == 0 || name.ToString() != path)
                return false;
        }
        finally { ReleaseStgMedium(ref medium); }

        var shellFormat = new FormatEtc
        {
            Format = checked((ushort)RegisterClipboardFormatW("Shell IDList Array")), Aspect = 1, Index = -1, Medium = 1,
        };
        medium = new StorageMedium();
        Marshal.ThrowExceptionForHR(getData(drag._data, &shellFormat, &medium));
        try
        {
            var buffer = GlobalLock(medium.Handle);
            if (buffer == 0) return false;
            try { if (Marshal.ReadInt32(buffer) != 1) return false; }
            finally { GlobalUnlock(medium.Handle); }
        }
        finally { ReleaseStgMedium(ref medium); }

        var allowed = true;
        var source = new FileDropSource(() => allowed);
        var pointer = ComInterfaceMarshaller<IFileDropSource>.ConvertToUnmanaged(source);
        try
        {
            var sourceVtable = *(nint**)pointer;
            var proceed = (delegate* unmanaged[Stdcall]<void*, int, uint, int>)sourceVtable[3];
            var feedback = (delegate* unmanaged[Stdcall]<void*, uint, int>)sourceVtable[4];
            if (proceed(pointer, 0, 1) != 0 || proceed(pointer, 1, 1) != 0x40101
                || proceed(pointer, 0, 0) != 0x40100 || feedback(pointer, 1) != 0x40102) return false;
            allowed = false;
            return proceed(pointer, 0, 1) == 0x40101;
        }
        finally { ComInterfaceMarshaller<IFileDropSource>.Free(pointer); GC.KeepAlive(source); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? name, uint length);
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref StorageMedium medium);

    internal static bool CheckNativeResourceCleanup(string path)
    {
        if (!CheckNativeDataObject(path)) return false;
        var process = GetCurrentProcess();
        var gdiBefore = GetGuiResources(process, 0);
        var userBefore = GetGuiResources(process, 1);
        for (var attempt = 0; attempt < 10; attempt++)
            if (!CheckNativeDataObject(path)) return false;
        return GetGuiResources(process, 0) <= gdiBefore + 1 && GetGuiResources(process, 1) <= userBefore + 1;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string format);
}
