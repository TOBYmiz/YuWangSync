using System.Runtime.InteropServices;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.Core;

public static class CloudFilesPlatform
{
    // WinUI 3 / Windows App SDK sets the application floor, not CFAPI (1709).
    public const int MinimumWindowsBuild = 17763;
    private static readonly Lazy<CF_PLATFORM_INFO> Platform = new(QueryPlatform);

    public static CF_PLATFORM_INFO Info => Platform.Value;

    public static string Describe()
    {
        try
        {
            return $"Windows={Environment.OSVersion.Version}; "
                + $"CFAPI={Info.BuildNumber}.{Info.RevisionNumber}; Integration=0x{Info.IntegrationNumber:X}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // CfGetPlatformInfo can be denied by the filter/security configuration.
            // Diagnostic metadata must not prevent an otherwise working provider.
            return $"Windows={Environment.OSVersion.Version}; CFAPI 平台版本查询不可用：{ex.Message}";
        }
    }

    public static void EnsureSupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild)
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "需要 Windows 10 1809（17763，包括 LTSC 2019）或更高版本的 x64 Windows，并启用 Cloud Files。");
    }

    private static CF_PLATFORM_INFO QueryPlatform()
    {
        try
        {
            var result = CfGetPlatformInfo(out var info);
            if (result.Failed)
                throw new InvalidOperationException($"无法查询 Windows Cloud Files 平台：0x{unchecked((uint)(int)result):X8}。");
            return info;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("此 Windows 缺少 Cloud Files API（cldapi.dll）。请安装完整的受支持 Windows 组件。", ex);
        }
    }
}
