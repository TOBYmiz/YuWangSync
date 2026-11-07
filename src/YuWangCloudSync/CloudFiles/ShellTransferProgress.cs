using System.Runtime.InteropServices;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// Follow Microsoft's CloudMirror upload/view progress contract: volatile Shell properties,
/// a UInt64 vector (completed, total), and SYNC_TRANSFER_STATUS. File property queries need
/// not return these volatile view values; successful submission does not prove visual display.
/// Statically typed native COM calls avoid runtime COM projection in NativeAOT.
/// </summary>
public static class ShellTransferProgress
{
    public const uint NeedsUpload = 0x1, Transferring = 0x4, Paused = 0x8, HasError = 0x10;
    private const ushort Ui4 = 19, VectorUi8 = 0x1015;
    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid ShellItemId = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");
    private static readonly PropertyKey ProgressKey = new(new("E77E90DF-6271-4F5B-834F-2DD1F245DDA4"), 4);
    private static readonly PropertyKey StatusKey = new(new("FCEFF153-E839-4CF3-A9E7-EA22832094B8"), 103);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id) { public readonly Guid Format = format; public readonly uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint ValueOrCount;
        [FieldOffset(16)] public IntPtr Values;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetValue(IntPtr self, in PropertyKey key, in PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValue(IntPtr self, in PropertyKey key, out PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Commit(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertyStore(IntPtr self, uint flags, in Guid interfaceId, out IntPtr store);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid interfaceId, out IntPtr item);
    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PSCreateMemoryPropertyStore(in Guid interfaceId, out IntPtr store);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    private static T Method<T>(IntPtr store, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(store), slot * IntPtr.Size));

    public static bool TrySet(string path, long total, long completed, uint status, out int result)
    {
        result = CoInitializeEx(IntPtr.Zero, 0);
        var uninitialize = result >= 0;
        if (result < 0 && result != unchecked((int)0x80010106)) return false;
        IntPtr store = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            result = SHCreateItemFromParsingName(path, IntPtr.Zero, ShellItemId, out item);
            if (result < 0) return false;
            if (item == IntPtr.Zero) { result = unchecked((int)0x80004003); return false; }
            result = Method<GetPropertyStore>(item, 8)(item, 0x1002, PropertyStoreId, out store);
            if (result < 0) return false;
            if (store == IntPtr.Zero) { result = unchecked((int)0x80004003); return false; }
            result = WriteStore(store, total, completed, status);
            return result >= 0;
        }
        finally
        {
            if (store != IntPtr.Zero) Method<Release>(store, 2)(store);
            if (item != IntPtr.Zero) Method<Release>(item, 2)(item);
            if (uninitialize) CoUninitialize();
        }
    }

    private static int WriteStore(IntPtr store, long total, long completed, uint status)
    {
        var progress = new PropVariant { Type = VectorUi8, ValueOrCount = 2, Values = Marshal.AllocCoTaskMem(16) };
        try
        {
            Marshal.WriteInt64(progress.Values, Math.Clamp(completed, 0, Math.Max(1, total)));
            Marshal.WriteInt64(progress.Values, 8, Math.Max(1, total));
            var set = Method<SetValue>(store, 6);
            var result = set(store, ProgressKey, progress);
            if (result < 0) return result;
            // Keep the correctly typed vector for terminal states too. VT_EMPTY is rejected
            // by the volatile sink on some Windows builds. The status removes the active bar.
            var transfer = new PropVariant { Type = Ui4, ValueOrCount = status };
            result = set(store, StatusKey, transfer);
            return result < 0 ? result : Method<Commit>(store, 7)(store);
        }
        finally { PropVariantClear(ref progress); }
    }

    /// <summary>Exercise the actual native property-store ABI, including counters larger than 4 GiB.</summary>
    public static bool ValidateNativeAbi(out int result)
    {
        result = CoInitializeEx(IntPtr.Zero, 0);
        var uninitialize = result >= 0;
        if (result < 0 && result != unchecked((int)0x80010106)) return false;
        IntPtr store = IntPtr.Zero;
        try
        {
            result = PSCreateMemoryPropertyStore(PropertyStoreId, out store);
            if (result < 0) return false;
            if (store == IntPtr.Zero) { result = unchecked((int)0x80004003); return false; }
            const long total = 6L * 1024 * 1024 * 1024, completed = 5L * 1024 * 1024 * 1024;
            foreach (var state in new uint[] { Transferring, HasError, Paused, 0 })
            {
                result = WriteStore(store, total, completed, state);
                if (result < 0) return false;
                var statusValue = default(PropVariant);
                var progressValue = default(PropVariant);
                try
                {
                    var get = Method<GetValue>(store, 5);
                    result = get(store, StatusKey, out statusValue);
                    if (result < 0) return false;
                    result = get(store, ProgressKey, out progressValue);
                    if (result < 0) return false;
                    if (statusValue.Type != Ui4 || statusValue.ValueOrCount != state || progressValue.Type != VectorUi8
                        || progressValue.ValueOrCount != 2 || progressValue.Values == IntPtr.Zero
                        || Marshal.ReadInt64(progressValue.Values) != completed || Marshal.ReadInt64(progressValue.Values, 8) != total)
                    {
                        result = unchecked((int)0x80004005);
                        return false;
                    }
                }
                finally { PropVariantClear(ref progressValue); PropVariantClear(ref statusValue); }
            }
            return true;
        }
        finally
        {
            if (store != IntPtr.Zero) Method<Release>(store, 2)(store);
            if (uninitialize) CoUninitialize();
        }
    }
}
