using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace YuWangCloudSync.Core;

/// <summary>Current-user DPAPI wrapper used for the installed account password.</summary>
public static class DpapiSecret
{
    private const uint CryptprotectUiForbidden = 0x1;
    public static string Protect(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var input = Encoding.UTF8.GetBytes(value);
        var inputBlob = new DataBlob(input);
        try
        {
            if (!CryptProtectData(ref inputBlob.Blob, null, IntPtr.Zero, null, IntPtr.Zero,
                    CryptprotectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData 失败");
            try { return Convert.ToBase64String(BlobToArray(output)); }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally { inputBlob.Dispose(); Array.Clear(input); }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";
        byte[] input;
        try { input = Convert.FromBase64String(protectedValue); }
        catch (FormatException ex) { throw new InvalidDataException("DPAPI 密文不是有效的 Base64", ex); }

        var inputBlob = new DataBlob(input);
        try
        {
            if (!CryptUnprotectData(ref inputBlob.Blob, out var description, IntPtr.Zero, null, IntPtr.Zero,
                    CryptprotectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData 失败");
            try
            {
                var plaintext = BlobToArray(output);
                try { return Encoding.UTF8.GetString(plaintext); }
                finally { Array.Clear(plaintext); }
            }
            finally
            {
                ClearBlob(output);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }
        finally { inputBlob.Dispose(); }
    }

    private sealed class DataBlob : IDisposable
    {
        internal DATA_BLOB Blob;

        internal DataBlob(byte[] data)
        {
            Blob.cbData = (uint)data.Length;
            Blob.pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            if (data.Length > 0) Marshal.Copy(data, 0, Blob.pbData, data.Length);
        }

        public void Dispose()
        {
            if (Blob.pbData == IntPtr.Zero) return;
            ClearBlob(Blob);
            Marshal.FreeHGlobal(Blob.pbData);
            Blob.pbData = IntPtr.Zero;
            Blob.cbData = 0;
        }
    }

    private static byte[] BlobToArray(DATA_BLOB blob)
    {
        var result = new byte[blob.cbData];
        if (result.Length > 0) Marshal.Copy(blob.pbData, result, 0, result.Length);
        return result;
    }

    private static void ClearBlob(DATA_BLOB blob)
    {
        for (var offset = 0; offset < blob.cbData; offset++) Marshal.WriteByte(blob.pbData, offset, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        string? pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, out IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        string? pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
