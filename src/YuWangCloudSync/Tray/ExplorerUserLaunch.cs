using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace YuWangCloudSync.Tray;

/// <summary>Undo an elevated installer's inherited token before creating any UI or singleton.</summary>
internal static class ExplorerUserLaunch
{
    internal static bool RelaunchIfElevated()
    {
        IntPtr process = IntPtr.Zero, currentToken = IntPtr.Zero, shellToken = IntPtr.Zero,
            primaryToken = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 8, out currentToken)) return false;
            if (!IsElevated(currentToken)) return false;
            var shell = GetShellWindow();
            if (shell != IntPtr.Zero)
            {
                GetWindowThreadProcessId(shell, out var processId);
                process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
                if (process != IntPtr.Zero) OpenProcessToken(process, 0x000A, out shellToken);
            }
            if (shellToken != IntPtr.Zero && IsElevated(shellToken))
            {
                CloseHandle(shellToken);
                shellToken = IntPtr.Zero;
            }
            if (shellToken == IntPtr.Zero)
            {
                // Setup may launch us before the scheduled Explorer restart has finished.
                // TOKEN_ELEVATION_TYPE_FULL has a standard linked token; UAC-off DEFAULT
                // has no split token and keeps the system context without forcing a change.
                if (!GetTokenInformation(currentToken, 18, out var type, sizeof(int), out _) || type != 2) return false;
                if (!GetLinkedToken(currentToken, 19, out shellToken, IntPtr.Size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using var user = new WindowsIdentity(currentToken);
            using var shellUser = new WindowsIdentity(shellToken);
            if (user.User != shellUser.User) return false;
            if (!DuplicateTokenEx(shellToken, 0x02000000, IntPtr.Zero, 2, 1, out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder(Marshal.PtrToStringUni(GetCommandLineW()));
            if (!CreateProcessWithTokenW(primaryToken, 1, Environment.ProcessPath!, commandLine,
                    0x00000400, environment, AppContext.BaseDirectory, ref startup, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(child.Thread);
            CloseHandle(child.Process);
            Serilog.Log.Information("主程序已使用 Explorer 的普通用户令牌重新启动，恢复桌面拖放。");
            return true;
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "无法使用 Explorer 用户权限重新启动主程序");
            return false;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            foreach (var handle in new[] { primaryToken, shellToken, currentToken, process })
                if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    private static bool IsElevated(IntPtr token)
    {
        if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return elevated != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCommandLineW();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int returned);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetLinkedToken(IntPtr token, int kind, out IntPtr value, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr duplicate);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string app,
        StringBuilder commandLine, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
}
