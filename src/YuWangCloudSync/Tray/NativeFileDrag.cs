using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace YuWangCloudSync.Tray;

[GeneratedComInterface, Guid("00000121-0000-0000-C000-000000000046")]
internal partial interface IFileDropSource
{
    [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
    [PreserveSig] int GiveFeedback(uint effect);
}

[GeneratedComClass]
internal sealed partial class FileDropSource(Func<bool> canContinue) : IFileDropSource
{
    public int QueryContinueDrag(int escapePressed, uint keyState)
        => escapePressed != 0 || !canContinue() ? 0x40101 : (keyState & 1) == 0 ? 0x40100 : 0;
    public int GiveFeedback(uint effect) => 0x40102;
}

/// <summary>One UI-thread OLE drag. Its data object contains no paths until preparation succeeds.</summary>
internal sealed unsafe partial class NativeFileDrag : IDisposable
{
    private nint _data;
    private nint _helper;
    private nint _preview;
    private bool _oleInitialized;
    private const int Width = 300;
    private const int Height = 64;

    internal NativeFileDrag()
    {
        try
        {
            Marshal.ThrowExceptionForHR(OleInitialize(0));
            _oleInitialized = true;
            var dataId = new Guid("0000010E-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(SHCreateDataObject(0, 0, 0, 0, ref dataId, out _data));
            var helperClass = new Guid("4657278A-411B-11D2-839A-00C04FD918D0");
            var helperId = new Guid("DE5BF786-477A-11D2-839D-00C04FD918D0");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref helperClass, 0, 1, ref helperId, out _helper));
            SetImage(preparing: true);
        }
        catch { Dispose(); throw; }
    }

    internal static bool IsLeftButtonDown => (GetAsyncKeyState(1) & 0x8000) != 0;
    internal static bool IsEscapeDown => (GetAsyncKeyState(27) & 0x8000) != 0;
    internal static (int X, int Y) CursorPosition
    {
        get
        {
            if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (point.X, point.Y);
        }
    }
    internal static (int X, int Y) DragThreshold => (GetSystemMetrics(68), GetSystemMetrics(69));

    // IDragSourceHelper stores an image; OLE only displays it inside DoDragDrop. A nonactivating,
    // click-through layered window displays the same bitmap while the async preparation runs.
    private void SetImage(bool preparing)
    {
        var bitmap = CreateDragBitmap(preparing);
        try
        {
            var image = new DragImage
            {
                Size = new Point { X = Width, Y = Height }, Offset = new Point { X = -16, Y = -20 },
                Bitmap = bitmap, ColorKey = 0xffffffff,
            };
            var vtable = *(nint**)_helper;
            var initialize = (delegate* unmanaged[Stdcall]<nint, DragImage*, nint, int>)vtable[3];
            Marshal.ThrowExceptionForHR(initialize(_helper, &image, _data));
            bitmap = 0; // InitializeFromBitmap takes ownership on success.
        }
        finally { if (bitmap != 0) DeleteObject(bitmap); }
        if (preparing) ShowPreparationImage();
    }

    private void ShowPreparationImage()
    {
        _preview = CreateWindowExW(0x080800A8 /* NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT */,
            "STATIC", "", 0x80000000, 0, 0, Width, Height, 0, 0, 0, 0);
        if (_preview == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var bitmap = CreateDragBitmap(preparing: true);
        var dc = CreateCompatibleDC(0);
        if (dc == 0) { DeleteObject(bitmap); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var previous = SelectObject(dc, bitmap);
        try
        {
            var source = new Point();
            var size = new Point { X = Width, Y = Height };
            var cursor = CursorPosition;
            var destination = new Point { X = cursor.X + 16, Y = cursor.Y + 20 };
            var blend = new BlendFunction { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(_preview, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(_preview, -1, destination.X, destination.Y, Width, Height, 0x0050);
        }
        finally { SelectObject(dc, previous); DeleteDC(dc); DeleteObject(bitmap); }
    }

    internal void MovePreparationImage()
    {
        if (_preview == 0) return;
        var point = CursorPosition;
        SetWindowPos(_preview, -1, point.X + 16, point.Y + 20, 0, 0, 0x0011);
    }

    internal void HidePreparationImage()
    {
        if (_preview != 0) DestroyWindow(_preview);
        _preview = 0;
    }

    internal bool Drop(IReadOnlyList<string> paths, Func<bool> canContinue)
    {
        if (paths.Count == 0) throw new IOException("没有可拖放的文件。");
        if (!IsLeftButtonDown || IsEscapeDown || !canContinue()) return false;
        SetPaths(paths);
        SetImage(preparing: false);
        if (!IsLeftButtonDown || IsEscapeDown || !canContinue()) return false;
        HidePreparationImage();
        var source = new FileDropSource(canContinue);
        var pointer = ComInterfaceMarshaller<IFileDropSource>.ConvertToUnmanaged(source);
        try
        {
            var hr = DoDragDrop(_data, (nint)pointer, 1 /* DROPEFFECT_COPY */, out var effect);
            Marshal.ThrowExceptionForHR(hr);
            return hr == 0x40100 && effect == 1;
        }
        finally { ComInterfaceMarshaller<IFileDropSource>.Free(pointer); GC.KeepAlive(source); }
    }

    private void SetPaths(IReadOnlyList<string> paths)
    {
        // Explorer can prefer Shell IDList Array over HDROP. Rebuild the Shell object
        // from the *ready* files so every advertised format describes the same items.
        // An empty preparation object with only an injected HDROP can still advertise
        // an empty Shell IDList, which some receivers interpret as an empty selection.
        var items = new nint[paths.Count];
        var desktop = Marshal.AllocCoTaskMem(sizeof(ushort));
        Marshal.WriteInt16(desktop, 0); // Empty PIDL = the Shell desktop namespace root.
        try
        {
            for (var index = 0; index < paths.Count; index++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index], 0, out items[index], 0, out _));
            var dataId = new Guid("0000010E-0000-0000-C000-000000000046");
            nint preparedData;
            fixed (nint* children = items)
                Marshal.ThrowExceptionForHR(SHCreateDataObject(desktop, (uint)items.Length, (nint)children, 0, ref dataId, out preparedData));
            var oldData = _data;
            _data = preparedData;
            if (oldData != 0) Marshal.Release(oldData);
        }
        finally
        {
            Marshal.FreeCoTaskMem(desktop);
            foreach (var item in items)
                if (item != 0) Marshal.FreeCoTaskMem(item);
        }
        // CF_HDROP is first installed here, after the caller's final hydration check.
        var bytes = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var memory = GlobalAlloc(0x42, (nuint)(20 + bytes.Length));
        if (memory == 0) throw new OutOfMemoryException();
        try
        {
            var buffer = GlobalLock(memory);
            if (buffer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Marshal.WriteInt32(buffer, 0, 20); // DROPFILES.pFiles
                Marshal.WriteInt32(buffer, 16, 1); // fWide
                Marshal.Copy(bytes, 0, buffer + 20, bytes.Length);
            }
            finally { GlobalUnlock(memory); }
            var format = new FormatEtc { Format = 15, Aspect = 1, Index = -1, Medium = 1 };
            var medium = new StorageMedium { Type = 1, Handle = memory };
            var setData = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, StorageMedium*, int, int>)(*(nint**)_data)[7];
            Marshal.ThrowExceptionForHR(setData(_data, &format, &medium, 1));
            memory = 0;
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }

    private static nint CreateDragBitmap(bool preparing)
    {
        var info = new BitmapInfo
        {
            Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32,
        };
        var bitmap = CreateDIBSection(0, ref info, 0, out var pixels, 0, 0);
        if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var dc = CreateCompatibleDC(0);
        if (dc == 0) { DeleteObject(bitmap); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var oldBitmap = SelectObject(dc, bitmap);
        var font = CreateFontW(-17, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Microsoft YaHei UI");
        var oldFont = SelectObject(dc, font);
        var succeeded = false;
        try
        {
            var span = new Span<uint>((void*)pixels, Width * Height);
            span.Fill(0xffeff5fc);
            // A file silhouette and status marker remain legible without Shell icon/thumbnail IO.
            for (var y = 12; y < 49; y++)
                for (var x = 12; x < 39; x++) span[y * Width + x] = 0xff3478bd;
            SetBkMode(dc, 1);
            SetTextColor(dc, 0x00202020);
            var rect = new Rect { Left = 52, Top = 9, Right = Width - 8, Bottom = 34 };
            DrawTextW(dc, preparing ? "正在准备文件…" : "复制文件", -1, ref rect, 0x24);
            rect.Top = 34; rect.Bottom = 59;
            DrawTextW(dc, preparing ? "按住鼠标等待 · Esc 取消" : "释放鼠标以复制", -1, ref rect, 0x24);
            GdiFlush();
            // GDI text does not preserve alpha. Premultiply every pixel for the layered/OLE image.
            for (var index = 0; index < span.Length; index++)
            {
                var color = span[index];
                span[index] = 0xe6000000 | (((color >> 16) & 255) * 230 / 255 << 16)
                    | (((color >> 8) & 255) * 230 / 255 << 8) | ((color & 255) * 230 / 255);
            }
            succeeded = true;
            return bitmap;
        }
        finally
        {
            SelectObject(dc, oldFont); SelectObject(dc, oldBitmap);
            if (font != 0) DeleteObject(font);
            DeleteDC(dc);
            if (!succeeded) DeleteObject(bitmap);
        }
    }

    public void Dispose()
    {
        HidePreparationImage();
        if (_helper != 0) Marshal.Release(_helper);
        if (_data != 0) Marshal.Release(_data);
        _helper = _data = 0;
        if (_oleInitialized) OleUninitialize();
        _oleInitialized = false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct DragImage { internal Point Size, Offset; internal nint Bitmap; internal uint ColorKey; }
    [StructLayout(LayoutKind.Sequential)] private struct FormatEtc { internal ushort Format; internal nint TargetDevice; internal uint Aspect; internal int Index; internal uint Medium; }
    [StructLayout(LayoutKind.Sequential)] private struct StorageMedium { internal uint Type; internal nint Handle, Owner; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { internal byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
        internal uint Compression, ImageSize; internal int XPixels, YPixels; internal uint Colors, Important;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint result);
    [DllImport("shell32.dll")] private static extern int SHCreateDataObject(nint folder, uint count, nint children, nint inner, ref Guid iid, out nint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, nint bindContext, out nint itemIdList, uint attributes, out uint resultAttributes);
    [DllImport("ole32.dll")] private static extern int DoDragDrop(nint data, nint source, uint effects, out uint effect);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref Point destination, ref Point size, nint sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int height, int width, int escape, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charset, uint output, uint clip, uint quality, uint family, string face);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(nint dc, string text, int length, ref Rect rect, uint format);
}
