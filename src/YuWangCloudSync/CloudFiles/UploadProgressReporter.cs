using YuWangCloudSync.Net;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Keep upload UI active until verification, cleanup and local identity binding all succeed.</summary>
internal sealed class UploadProgressReporter : IDisposable
{
    private readonly string _path;
    private readonly long _total;
    private readonly Action<string>? _log;
    private readonly CancellationToken _stop;
    private readonly object _gate = new();
    private long _lastShell, _lastLog;
    private UploadStage _stage;
    private long _sent, _confirmed, _verified, _resumed;
    private bool _complete, _disposed, _shellFailureLogged;

    public UploadProgressReporter(string path, long total, Action<string>? log, CancellationToken stop)
    {
        _path = path;
        _total = total;
        _log = log;
        _stop = stop;
        Report(new(UploadStage.Preparing, total, 0, 0));
    }

    public void Report(UploadProgress progress)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var changed = _lastLog == 0 || _stage != progress.Stage;
            var edge = (_sent != _total && progress.SentBytes == _total)
                || (_confirmed != _total && progress.ConfirmedBytes == _total);
            _stage = progress.Stage;
            _sent = Math.Max(_sent, Math.Clamp(progress.SentBytes, 0, _total));
            _confirmed = Math.Max(_confirmed, Math.Clamp(progress.ConfirmedBytes, 0, _total));
            _verified = Math.Max(_verified, Math.Clamp(progress.VerifiedBytes, 0, _total));
            _resumed = Math.Max(_resumed, Math.Clamp(progress.ResumedBytes, 0, _total));
            var now = Environment.TickCount64;
            if (changed || edge || now - _lastShell >= 250)
            {
                // Do not show a completed transfer before the caller binds the verified ID.
                var displayed = Math.Min(_sent, Math.Max(0, _total - Math.Max(1, _total / 10_000)));
                Publish(displayed, ShellTransferProgress.Transferring);
                _lastShell = now;
            }
            if (changed || edge || now - _lastLog >= 1000)
            {
                var percent = _total == 0 ? 0 : (double)_sent / _total * 100;
                _log?.Invoke($"[UPLOAD] phase={_stage} sent={_sent}/{_total} confirmed={_confirmed} verified={_verified} resumed={_resumed} percent={percent:F1}% path={_path}");
                _lastLog = now;
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _complete = true;
            Publish(Math.Max(1, _total), 0);
            _log?.Invoke($"[UPLOAD] phase=Completed sent={_total}/{_total} resumed={_resumed} path={_path}");
        }
    }

    private void Publish(long current, uint status)
    {
        try
        {
            if (!ShellTransferProgress.TrySet(_path, _total, current, status, out var error) && !_shellFailureLogged)
            {
                _shellFailureLogged = true;
                _log?.Invoke($"[UPLOAD] Explorer 进度上报不可用 hr=0x{unchecked((uint)error):X8} path={_path}");
            }
            ExplorerStateNotifier.NotifyItem(_path);
        }
        catch (Exception error)
        {
            if (!_shellFailureLogged) _log?.Invoke($"[UPLOAD] Explorer 进度上报异常（忽略）: {error.GetType().Name}");
            _shellFailureLogged = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_complete) return;
            var cancelled = _stop.IsCancellationRequested;
            Publish(0, cancelled ? ShellTransferProgress.Paused : ShellTransferProgress.HasError);
            _log?.Invoke($"[UPLOAD] phase={(cancelled ? "Cancelled" : "Failed")} sent={_sent}/{_total} confirmed={_confirmed} resumed={_resumed} path={_path}");
        }
    }
}
