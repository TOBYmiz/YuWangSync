using YuWangCloudSync.Models;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Owns one cleanup loop; a replacement waits for the previous scan to stop.</summary>
internal sealed class FileCacheCleanupService(Action<CancellationToken> cleanup, Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private int _hours;

    internal void Configure(bool enabled, int hours)
    {
        hours = SyncRootContext.NormalizeFileCacheCleanupHours(hours);
        lock (_gate)
        {
            if (enabled == (_cancellation is not null) && _hours == hours) return;
            _hours = hours;
            _cancellation?.Cancel();
            _cancellation = null;
            if (!enabled) return;

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var previous = _worker;
            _worker = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    while (!cancellation.IsCancellationRequested)
                    {
                        await (delay?.Invoke(TimeSpan.FromHours(hours), cancellation.Token)
                            ?? Task.Delay(TimeSpan.FromHours(hours), cancellation.Token)).ConfigureAwait(false);
                        try { cleanup(cancellation.Token); }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                        catch (Exception error) { log($"自动清理文件缓存失败：{error.Message}"); }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally { cancellation.Dispose(); }
            });
        }
    }

    internal void Stop()
    {
        Task worker;
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            worker = _worker;
        }
        // The provider must stay connected until the active native operation returns.
        worker.GetAwaiter().GetResult();
    }
}
