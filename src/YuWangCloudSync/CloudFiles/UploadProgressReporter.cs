using System.Collections.Concurrent;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.CloudFiles;

/// <summary>Latest UI-facing state of one sync-folder upload.</summary>
/// <param name="StagePercent">Progress of the current stage (upload bytes or verified bytes), if measurable.</param>
/// <param name="OverallPercent">Monotonic progress of the whole operation, including verification when planned.</param>
internal readonly record struct UploadActivitySnapshot(UploadStage Stage, double? StagePercent, double OverallPercent,
    bool Verifies, long TotalBytes)
{
    public string Describe()
    {
        var percent = StagePercent is { } value ? $" {value:F0}%" : "";
        return Stage switch
        {
            UploadStage.Preparing => "正在准备上传",
            UploadStage.Uploading => "正在上传" + percent,
            UploadStage.Confirming => "已上传，正在等待云端确认",
            UploadStage.Verifying => "正在下载校验" + percent,
            _ => Verifies ? "校验通过，正在完成同步" : "正在完成同步",
        };
    }
}

/// <summary>Process-wide registry of active sync-folder uploads, keyed by full local path.</summary>
internal static class UploadActivity
{
    private static readonly ConcurrentDictionary<string, UploadActivitySnapshot> Items = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsEmpty => Items.IsEmpty;

    public static bool TryGet(string path, out UploadActivitySnapshot snapshot)
    {
        snapshot = default;
        return !Items.IsEmpty && Items.TryGetValue(Normalize(path), out snapshot);
    }

    internal static void Set(string path, UploadActivitySnapshot snapshot) => Items[Normalize(path)] = snapshot;
    internal static void Remove(string path) => Items.TryRemove(Normalize(path), out _);

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}

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
    private double _overall;
    private bool _verifies, _complete, _disposed, _shellFailureLogged;

    /// <param name="verifyPlanned">
    /// When true, the Explorer bar spans upload and verification as one continuous transfer
    /// (each half of the bar). The client may still verify unplanned during recovery.
    /// </param>
    public UploadProgressReporter(string path, long total, Action<string>? log, CancellationToken stop,
        bool verifyPlanned = true)
    {
        _path = path;
        _total = total;
        _log = log;
        _stop = stop;
        _verifies = verifyPlanned;
        Report(new(UploadStage.Preparing, total, 0, 0));
    }

    /// <summary>Overall fraction: upload alone, or upload and full download verification at equal weight.</summary>
    internal static double OverallFraction(long total, long sent, long verified, bool verifies)
    {
        if (total <= 0) return 0;
        var upload = Math.Clamp((double)sent / total, 0, 1);
        return verifies ? (upload + Math.Clamp((double)verified / total, 0, 1)) / 2 : upload;
    }

    public void Report(UploadProgress progress)
    {
        lock (_gate)
        {
            if (_disposed || _complete) return;
            var changed = _lastLog == 0 || _stage != progress.Stage;
            var edge = (_sent != _total && progress.SentBytes == _total)
                || (_confirmed != _total && progress.ConfirmedBytes == _total);
            _stage = progress.Stage;
            _sent = Math.Max(_sent, Math.Clamp(progress.SentBytes, 0, _total));
            _confirmed = Math.Max(_confirmed, Math.Clamp(progress.ConfirmedBytes, 0, _total));
            _verified = Math.Max(_verified, Math.Clamp(progress.VerifiedBytes, 0, _total));
            _resumed = Math.Max(_resumed, Math.Clamp(progress.ResumedBytes, 0, _total));
            _verifies |= _stage == UploadStage.Verifying;
            // Never move the bar backwards, even if recovery adds an unplanned verification.
            _overall = Math.Max(_overall, OverallFraction(_total, _sent, _verified, _verifies));
            UploadActivity.Set(_path, Snapshot());
            var now = Environment.TickCount64;
            if (changed || edge || now - _lastShell >= 250)
            {
                // Do not show a completed transfer before the caller binds the verified ID.
                var displayed = Math.Min((long)Math.Round(_overall * _total), Math.Max(0, _total - Math.Max(1, _total / 10_000)));
                Publish(displayed, ShellTransferProgress.Transferring);
                _lastShell = now;
            }
            if (changed || edge || now - _lastLog >= 1000)
            {
                _log?.Invoke($"[UPLOAD] phase={_stage} sent={_sent}/{_total} confirmed={_confirmed} verified={_verified} resumed={_resumed} overall={_overall * 100:F1}% path={_path}");
                _lastLog = now;
            }
        }
    }

    private UploadActivitySnapshot Snapshot()
    {
        double? stagePercent = _stage switch
        {
            UploadStage.Uploading when _total > 0 => (double)_sent / _total * 100,
            UploadStage.Verifying when _total > 0 => (double)_verified / _total * 100,
            _ => null,
        };
        // Keep 100% for the bound/in-sync state; the active indicator stops just short of it.
        return new(_stage, stagePercent, Math.Min(_overall * 100, 99.5), _verifies, _total);
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _complete = true;
            UploadActivity.Remove(_path);
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
            UploadActivity.Remove(_path);
            if (_complete) return;
            var cancelled = _stop.IsCancellationRequested;
            Publish(0, cancelled ? ShellTransferProgress.Paused : ShellTransferProgress.HasError);
            _log?.Invoke($"[UPLOAD] phase={(cancelled ? "Cancelled" : "Failed")} sent={_sent}/{_total} confirmed={_confirmed} resumed={_resumed} path={_path}");
        }
    }
}
