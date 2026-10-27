using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace YuWangCloudSync.Core;

public static class InstanceControl
{
    private static string PipeName => ProductIdentity.ShutdownPipePrefix + WindowsIdentity.GetCurrent().User!.Value
        + "." + Process.GetCurrentProcess().SessionId;

    public static async Task ListenAsync(Action stop, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (await reader.ReadLineAsync(requestTimeout.Token) != "stop") continue;
                await writer.WriteLineAsync(Environment.ProcessId.ToString());
                stop();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    public static async Task StopAsync()
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await client.ConnectAsync(2000); }
        catch (TimeoutException)
        {
            if (IsRunning()) throw new IOException("同步进程未响应停止命令，请重启应用后重试。");
            return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync("stop");
        if (!int.TryParse(await reader.ReadLineAsync(deadline.Token), out var processId))
            throw new IOException("同步进程返回了无效的停止响应。");
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(deadline.Token);
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
