using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.Core;

/// <summary>
/// CFAPI unions use eight-byte alignment on x64. Avoid Vanara's reflection-based
/// GetParam/SetParam helpers: their fallback can throw "Unsupported type parameter."
/// in a trimmed NativeAOT callback, before the provider can acknowledge the request.
/// </summary>
public static class CloudFilesInterop
{
    private const int UnionOffset = 8;

    public static bool TryReadFetchData(in CF_CALLBACK_PARAMETERS parameters,
        out CF_CALLBACK_PARAMETERS.FETCHDATA fetch)
    {
        fetch = default;
        // Only Flags, RequiredFileOffset and RequiredLength are required. Older
        // callback versions may omit the optional dehydration fields at the end.
        if (!TryGetContent(parameters, 24, out var content)) return false;
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<CF_CALLBACK_PARAMETERS.FETCHDATA>()];
        bytes.Clear();
        content[..Math.Min(content.Length, bytes.Length)].CopyTo(bytes);
        fetch = MemoryMarshal.Read<CF_CALLBACK_PARAMETERS.FETCHDATA>(bytes);
        return fetch.RequiredFileOffset >= 0 && fetch.RequiredLength > 0
            && fetch.RequiredFileOffset <= long.MaxValue - fetch.RequiredLength;
    }

    public static string? ReadRenameTarget(in CF_CALLBACK_PARAMETERS parameters)
    {
        if (!TryGetContent(parameters, 8 + IntPtr.Size, out var content))
            throw new InvalidDataException("CFAPI rename callback parameters are truncated.");
        var target = MemoryMarshal.Read<IntPtr>(content[8..]);
        return target == IntPtr.Zero ? null : Marshal.PtrToStringUni(target);
    }

    public static bool TryReadCallbackFlags(in CF_CALLBACK_PARAMETERS parameters, out uint flags)
    {
        flags = 0;
        if (!TryGetContent(parameters, sizeof(uint), out var content)) return false;
        flags = MemoryMarshal.Read<uint>(content);
        return true;
    }

    private static bool TryGetContent(in CF_CALLBACK_PARAMETERS parameters, int minimumLength,
        out ReadOnlySpan<byte> content)
    {
        content = default;
        if (parameters.Content == null || parameters.ParamSize < UnionOffset + minimumLength)
            return false;
        var length = (int)Math.Min((uint)parameters.Content.Length, parameters.ParamSize - UnionOffset);
        if (length < minimumLength) return false;
        content = parameters.Content.AsSpan(0, length);
        return true;
    }

    public static CF_OPERATION_PARAMETERS CreateOperationParameters<T>(in T value) where T : unmanaged
    {
        var result = default(CF_OPERATION_PARAMETERS);
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
        var length = Unsafe.SizeOf<T>();
        if (length > bytes.Length - UnionOffset)
            throw new ArgumentOutOfRangeException(nameof(value));
        result.ParamSize = checked((uint)(UnionOffset + length));
        MemoryMarshal.Write(bytes[UnionOffset..], in value);
        return result;
    }

    public static CF_OPERATION_INFO CreateOperationInfo(in CF_CALLBACK_INFO callback, CF_OPERATION_TYPE type)
    {
        // RequestKey was appended to CF_CALLBACK_INFO. Never use a field beyond
        // the size advertised by the platform; older callbacks use the default key.
        var requestKeyEnd = Marshal.OffsetOf<CF_CALLBACK_INFO>(nameof(CF_CALLBACK_INFO.RequestKey)).ToInt32()
            + Unsafe.SizeOf<CF_REQUEST_KEY>();
        return new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = type,
            ConnectionKey = callback.ConnectionKey,
            TransferKey = callback.TransferKey,
            CorrelationVector = callback.CorrelationVector,
            RequestKey = callback.StructSize >= requestKeyEnd ? callback.RequestKey : default
        };
    }

    public static bool TryGetProviderName(string path, out string? name)
    {
        name = null;
        var capacity = Marshal.SizeOf<CF_SYNC_ROOT_PROVIDER_INFO>();
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var result = CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_PROVIDER,
                buffer, (uint)capacity, out var returned);
            if (result.Failed || returned < capacity) return false;
            // The concrete Marshal call is visible to the NativeAOT compiler.
            name = Marshal.PtrToStructure<CF_SYNC_ROOT_PROVIDER_INFO>(buffer).ProviderName;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
