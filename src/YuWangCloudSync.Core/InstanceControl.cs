using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace YuWangCloudSync.Core;

public static class InstanceControl
{
    private static string PipeName => ProductIdentity.ShutdownPipePrefix + CurrentUserSid.Value
        + "." + Process.GetCurrentProcess().SessionId;

    // PipeOptions.CurrentUserOnly compares WindowsIdentity.Owner, which is BUILTIN\Administrators
    // for an elevated admin token but the user SID for the normal tray process. The elevated
    // installer/uninstaller then fails with UnauthorizedAccessException while sending --stop.
    // Both sides therefore use the token user SID explicitly.
    private static SecurityIdentifier CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User!;
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var user = CurrentUserSid;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.SetOwner(user);
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static NamedPipeClientStream CreateClient()
        => new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    /// <summary>Reject a pipe squatted by another account; elevation of the same user is allowed.</summary>
    private static void ValidateServerOwner(NamedPipeClientStream client)
    {
        var owner = client.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is null || !owner.Equals(CurrentUserSid))
            throw new UnauthorizedAccessException("同步进程控制管道不属于当前用户。");
    }

    public static async Task ListenAsync(Action stop, CancellationToken cancellationToken)
        => await ListenAsync(command => string.Equals(command, "stop", StringComparison.Ordinal)
            ? Environment.ProcessId.ToString() : null, stop, cancellationToken);

    public static async Task ListenAsync(Func<string, string?> command,
        Action stop, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(requestTimeout.Token);
                if (line is null) continue;
                var response = command(line);
                if (response is not null)
                {
                    await writer.WriteLineAsync(response);
                    if (string.Equals(line, "stop", StringComparison.Ordinal))
                    {
                        stop();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Send one command line to the running instance; returns its response, or null if unavailable.</summary>
    public static string? TrySendCommand(string command, int timeoutMilliseconds = 2000)
    {
        try
        {
            using var client = CreateClient();
            client.Connect(timeoutMilliseconds);
            ValidateServerOwner(client);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds * 2));
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            writer.WriteLine(command);
            return reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task StopAsync()
    {
        using var client = CreateClient();
        try { await client.ConnectAsync(2000); }
        catch (TimeoutException)
        {
            if (IsRunning()) throw new IOException("同步进程未响应停止命令，请重启应用后重试。");
            return;
        }
        ValidateServerOwner(client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync("stop");
        if (!int.TryParse(await reader.ReadLineAsync(deadline.Token), out var processId))
            throw new IOException("同步进程返回了无效的停止响应。");
        try
        {
            using var process = Process.GetProcessById(processId);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                if (process.HasExited) return;
                throw new TimeoutException("同步进程在 60 秒内未退出。");
            }
        }
        catch (ArgumentException) { }
    }

    private static bool IsRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(ProductIdentity.SingleInstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
}
