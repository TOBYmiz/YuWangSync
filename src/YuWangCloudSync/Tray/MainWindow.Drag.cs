using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    private readonly DispatcherTimer _dragIntentTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private (int X, int Y) _dragPressPosition;
    private bool _outboundDragActive;
    private CancellationTokenSource? _dragCancellation;

    private void CheckDragIntent()
    {
        if (_dragSourceRow is not { Tag: YuWangItem item } row || !NativeFileDrag.IsLeftButtonDown
            || NativeFileDrag.IsEscapeDown || _isClosed || _busyNow || _currentFolder is null)
        {
            _dragIntentTimer.Stop();
            _dragSourceRow = null;
            return;
        }
        var position = NativeFileDrag.CursorPosition;
        var threshold = NativeFileDrag.DragThreshold;
        if (Math.Abs(position.X - _dragPressPosition.X) < threshold.X
            && Math.Abs(position.Y - _dragPressPosition.Y) < threshold.Y) return;
        _dragIntentTimer.Stop();
        _dragSourceRow = null;
        if (!row.IsSelected) SelectDragSource(row);
        var items = row.IsSelected ? SelectedEntries().ToArray() : [item];
        RunUi(() => StartPreparedDragAsync(_currentFolder, items));
    }

    private async Task StartPreparedDragAsync(RemoteFolder folder, YuWangItem[] items)
    {
        if (_outboundDragActive || _busyNow || _isClosed || items.Length == 0) return;
        _outboundDragActive = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Program.ApplicationCancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(_config.DownloadTimeoutMinutes, 1, 120)));
        using var timeoutRegistration = timeout.Token.Register(cancellation.Cancel);
        _dragCancellation = cancellation;
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        NativeFileDrag? drag = null;
        Exception? monitorFailure = null;
        try
        {
            // No file paths are present. Display the preparing image before filesystem/network work.
            drag = new NativeFileDrag();
            monitor.Tick += (_, _) =>
            {
                try
                {
                    if (NativeFileDrag.IsLeftButtonDown && !NativeFileDrag.IsEscapeDown && !_isClosed
                        && Program.IsDragBackendAvailable)
                    {
                        drag.MovePreparationImage();
                        return;
                    }
                }
                catch (Exception error)
                {
                    monitorFailure = error;
                    Serilog.Log.Warning(error, "拖放视觉反馈失败");
                }
                cancellation.Cancel();
                drag.HidePreparationImage();
                monitor.Stop();
            };
            monitor.Start();
            await RunBusyAsync("正在准备文件…（按住鼠标等待，Esc 取消）", async () =>
            {
                try
                {
                    EnsureDragBackendAvailable();
                    var paths = await PrepareDragPathsAsync(folder, items, cancellation.Token);
                    var progress = new Progress<string>(message =>
                    {
                        if (!_isClosed && !cancellation.IsCancellationRequested) SetStatus(message);
                    });
                    using var prepared = await DragFilePreparation.CreateAsync(paths, () => Program.IsDragBackendAvailable,
                        message => ((IProgress<string>)progress).Report(message), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    EnsureDragBackendAvailable();
                    prepared.ValidateReady();
                    if (!NativeFileDrag.IsLeftButtonDown || NativeFileDrag.IsEscapeDown) cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    // XAML may still hold the pointer for list selection; OLE must take capture.
                    _items.ReleasePointerCaptures();
                    monitor.Stop();
                    SetStatus("文件已就绪，释放鼠标以复制");
                    var copied = drag.Drop(paths, () => !_isClosed && !cancellation.IsCancellationRequested
                        && Program.IsDragBackendAvailable);
                    EnsureDragBackendAvailable();
                    if (!_isClosed) SetStatus(copied ? "已交给目标复制" : "已取消拖放");
                }
                catch (OperationCanceledException)
                {
                    drag.HidePreparationImage();
                    if (_isClosed) return;
                    SetStatus("已取消拖放");
                    if (monitorFailure is not null)
                        ShowInfo(InfoBarSeverity.Error, "拖放失败", $"无法继续显示拖放反馈：{monitorFailure.Message}");
                    else if (timeout.IsCancellationRequested)
                        ShowInfo(InfoBarSeverity.Error, "拖放失败", "准备文件超时，请检查网络与同步引擎后重试。");
                    else if (!Program.IsDragBackendAvailable)
                        ShowInfo(InfoBarSeverity.Error, "拖放失败", "同步引擎已停止，无法继续复制文件。");
                }
                catch (Exception error)
                {
                    drag.HidePreparationImage();
                    Serilog.Log.Warning(error, "向外拖放准备失败");
                    if (!_isClosed)
                    {
                        SetStatus("拖放失败");
                        ShowInfo(InfoBarSeverity.Error, "拖放失败", error.Message);
                    }
                }
            });
        }
        catch (Exception error)
        {
            if (!_isClosed) ShowInfo(InfoBarSeverity.Error, "无法开始拖放", error.Message);
        }
        finally
        {
            monitor.Stop();
            drag?.Dispose();
            _dragCancellation = null;
            _outboundDragActive = false;
        }
    }

    private static void EnsureDragBackendAvailable()
    {
        if (!Program.IsDragBackendAvailable)
            throw new IOException("同步引擎尚未连接或正在停止，请等待同步恢复后再拖放。");
    }

    private async Task<IReadOnlyList<string>> PrepareDragPathsAsync(RemoteFolder folder, YuWangItem[] items,
        CancellationToken token)
    {
        var paths = new List<string>();
        var staging = Path.Combine(Path.GetTempPath(), ProductIdentity.Identifier, "dragged-files", Guid.NewGuid().ToString("N"));
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            EnsureDragBackendAvailable();
            var path = await PrepareLocalItemPathAsync(folder, item, staging, token);
            paths.Add(path);
        }
        return paths;
    }
}
