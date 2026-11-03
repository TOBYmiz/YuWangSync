using System.Runtime.InteropServices;
using YuWangCloudSync.Models;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.Core;

public static class ProviderIdentity
{
    public static readonly Guid Id = new("A75C7E2E-1048-4EF8-A11D-6A92B7840B23");
    public static byte[] RootIdentity => Id.ToByteArray();

    public static bool OwnsRoot(string path, bool allowLegacy = false, string? providerName = null)
    {
        var identityOffset = Marshal.OffsetOf<CF_SYNC_ROOT_STANDARD_INFO>(nameof(CF_SYNC_ROOT_STANDARD_INFO.SyncRootIdentity)).ToInt32();
        var capacity = Marshal.SizeOf<CF_SYNC_ROOT_STANDARD_INFO>() + 16;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var result = CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_STANDARD,
                buffer, (uint)capacity, out var returned);
            if (result.Failed || returned < identityOffset) return false;
            var info = Marshal.PtrToStructure<CF_SYNC_ROOT_STANDARD_INFO>(buffer);
            if (!IsRootPath(path, info.SyncRootFileId)) return false;
            if (info.SyncRootIdentityLength == 16 && returned >= identityOffset + 16)
            {
                var identity = new byte[16];
                Marshal.Copy(buffer + identityOffset, identity, 0, identity.Length);
                return identity.AsSpan().SequenceEqual(RootIdentity);
            }
            // The identity bytes are authoritative. Very old registrations may have none;
            // for those only, accept the configured ProviderName as a compatibility hint.
            return allowLegacy && info.SyncRootIdentityLength == 0
                && IsLegacyProviderName(info.ProviderName, providerName);
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsLegacyProviderName(string? registeredName, string? configuredName)
    {
        var registered = registeredName?.Trim();
        return !string.IsNullOrEmpty(registered)
            && (string.Equals(registered, configuredName?.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(registered, "育网云", StringComparison.OrdinalIgnoreCase)
                || string.Equals(registered, ProductIdentity.DisplayNameZh, StringComparison.OrdinalIgnoreCase));
    }

    public static bool OwnsPlaceholder(string path)
    {
        var identity = GetPlaceholderIdentity(path);
        return identity != null
            && unchecked((uint)BitConverter.ToInt32(identity, 0)) == FileIdentity.MagicExpected;
    }

    public static bool TryReadPlaceholderIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        var bytes = GetPlaceholderIdentity(path);
        if (bytes == null || BitConverter.ToUInt32(bytes, 0) != FileIdentity.MagicExpected) return false;
        identity = FileIdentity.Deserialize(bytes, bytes.Length);
        return identity.IsValid;
    }

    public static bool OwnsSyncRootPlaceholder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != (FileAttributes.Directory | FileAttributes.ReparsePoint))
                return false;
        }
        catch { return false; }

        var identity = GetPlaceholderIdentity(path);
        return identity != null && identity.AsSpan().SequenceEqual(FileIdentity.SyncRoot.Serialize());
    }

    private static byte[]? GetPlaceholderIdentity(string path)
    {
        var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        var identityOffset = Marshal.OffsetOf<CF_PLACEHOLDER_STANDARD_INFO>(nameof(CF_PLACEHOLDER_STANDARD_INFO.FileIdentity)).ToInt32();
        var lengthOffset = Marshal.OffsetOf<CF_PLACEHOLDER_STANDARD_INFO>(nameof(CF_PLACEHOLDER_STANDARD_INFO.FileIdentityLength)).ToInt32();
        var capacity = Marshal.SizeOf<CF_PLACEHOLDER_STANDARD_INFO>() + 4096;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var result = CfGetPlaceholderInfo((HFILE)handle, CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD,
                buffer, (uint)capacity, out var returned);
            if (result.Failed || returned < identityOffset + 29 || Marshal.ReadInt32(buffer, lengthOffset) < 29)
                return null;
            var identity = new byte[29];
            Marshal.Copy(buffer + identityOffset, identity, 0, identity.Length);
            return identity;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); CloseHandle(handle); }
    }

    private static bool IsRootPath(string path, long rootFileId)
    {
        var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return false;
        try
        {
            return GetFileInformationByHandle(handle, out var info)
                && ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow) == unchecked((ulong)rootFileId);
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME AccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(IntPtr handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
