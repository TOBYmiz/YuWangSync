using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.CloudFiles;

internal static class PrivilegedOperations
{
    internal static bool Run(string operation, string syncRoot, string? registrationId = null,
        Action<string>? log = null)
    {
        if (operation is not ("--remove-machine-shell-registration" or "--ensure-search-index" or "--remove-search-index"))
            throw new ArgumentOutOfRangeException(nameof(operation));
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, ProductIdentity.Identifier + "Admin.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少权限辅助程序。", executable);
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add(operation);
            start.ArgumentList.Add("--user-sid");
            start.ArgumentList.Add(sid);
            start.ArgumentList.Add("--sync-root");
            start.ArgumentList.Add(Path.GetFullPath(syncRoot));
            if (registrationId is not null)
            {
                start.ArgumentList.Add("--registration-id");
                start.ArgumentList.Add(registrationId);
            }
            using var process = Process.Start(start) ?? throw new IOException("无法启动权限辅助程序。");
            process.WaitForExit();
            log?.Invoke($"权限辅助程序 operation={operation} exit={process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            log?.Invoke($"已取消权限辅助程序的 UAC 授权 operation={operation}");
            return false;
        }
        catch (Exception error)
        {
            log?.Invoke($"权限辅助程序失败 operation={operation}: {error.Message}");
            return false;
        }
    }
}
