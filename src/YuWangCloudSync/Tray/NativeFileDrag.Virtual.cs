using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace YuWangCloudSync.Tray;

internal sealed record DragItemVisual(byte[] Pixels, int Width, int Height, int OffsetX, int OffsetY);

internal sealed unsafe partial class NativeFileDrag
{
    // The OLE source owns an independent STA and input queue. The WinUI dispatcher remains
    // available for exact progress, cancellation and async download continuations after drop.
    internal static Task<bool> DropVirtualAsync(VirtualFileDataObject data, DragItemVisual? visual, Func<bool> canContinue)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var drag = new NativeFileDrag(preparing: false);
                Marshal.Release(drag._data);
                drag._data = data.Pointer;
                Marshal.AddRef(drag._data);
                data.SetSourceThread();
                if (visual is not null) drag.SetItemVisual(visual);
                var source = new FileDropSource(canContinue);
                var pointer = ComInterfaceMarshaller<IFileDropSource>.ConvertToUnmanaged(source);
                try
                {
                    var hr = DoDragDrop(drag._data, (nint)pointer, 1, out var effect);
                    Marshal.ThrowExceptionForHR(hr);
                    completion.TrySetResult(hr == 0x40100 && effect == 1);
                }
                finally { ComInterfaceMarshaller<IFileDropSource>.Free(pointer); GC.KeepAlive(source); }
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { data.Dispose(); }
        }) { IsBackground = true, Name = "Virtual file drag" };
        data.Retain();
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { data.Dispose(); throw; }
        return completion.Task;
    }

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
            Marshal.ThrowExceptionForHR(initialize(_helper, &image, _data));
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

    internal static void ReleaseMouseCapture() => ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
