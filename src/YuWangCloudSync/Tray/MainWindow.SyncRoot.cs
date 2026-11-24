using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    private CancellationTokenSource? _syncDownloadCancellation;
    private readonly DispatcherTimer _fileStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<(YuWangItem Item, Border Cell)> _availabilityRows = [];
    private bool _availabilityPending;
    private enum FileSyncError { None, Download, Writeback }
    private static readonly TimeSpan IdleStatusInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActiveStatusInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>An in-progress transfer shown in the status column instead of the availability icon.</summary>
    /// <param name="Percent">Null while the transfer cannot be measured (the ring spins).</param>
    private readonly record struct CellActivity(string Glyph, double? Percent, string Description);

    // Main-window downloads outside the sync folder (save as / open fallback), keyed by cloud ID.
    // UI thread only; the download's own progress timer updates the visible cell directly.
    private readonly Dictionary<ulong, (long Completed, long Total)> _browserDownloads = [];

    private void ResetFileAvailabilityRows() => _availabilityRows.Clear();

    private void AddAvailabilityCell(Grid row, YuWangItem item)
    {
        // A directory's own attributes do not tell us whether its descendants are available offline.
        if (item.IsFolder) return;
        var version = _fileRenderVersion;
        var cell = new Border { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center };
        var entry = (item, cell);
        cell.Loaded += (_, _) => { if (version == _fileRenderVersion) _availabilityRows.Add(entry); };
        cell.Unloaded += (_, _) => _availabilityRows.Remove(entry);
        Grid.SetColumn(cell, 1);
        row.Children.Add(cell);
        SetAvailability(cell, SyncRootAvailability.OnlineOnly);
    }

    private void RefreshFileAvailability()
    {
        if (_availabilityPending || _isClosed || _currentFolder is not { } folder || _availabilityRows.Count == 0) return;
        _availabilityPending = true;
        var version = _fileRenderVersion;
        var rows = _availabilityRows.ToArray();
        var failures = Callbacks.DownloadFailures;
        RunUi(async () =>
        {
            try
            {
                var states = await Task.Run(() => rows.Select(row => ReadFileSyncStatus(folder.Disk.Id, folder.Id,
                    row.Item, failures)).ToArray());
                if (_isClosed || version != _fileRenderVersion || !Equals(folder, _currentFolder)
                    || !ReferenceEquals(failures, Callbacks.DownloadFailures)) return;
                var active = false;
                for (var i = 0; i < rows.Length; ++i)
                {
                    if (!_availabilityRows.Contains(rows[i])) continue;
                    var activity = BrowserDownloadActivity(rows[i].Item.Id) ?? states[i].Activity;
                    active |= activity is not null;
                    SetAvailability(rows[i].Cell, states[i].Availability, states[i].Error, activity);
                }
                // Sample faster only while something in this folder is transferring.
                var interval = active ? ActiveStatusInterval : IdleStatusInterval;
                if (_fileStatusTimer.Interval != interval) _fileStatusTimer.Interval = interval;
            }
            finally { _availabilityPending = false; }
        });
    }

    private static (SyncRootAvailability Availability, FileSyncError Error, CellActivity? Activity) ReadFileSyncStatus(
        ulong disk, ulong folder, YuWangItem item, SyncRootDownloadFailures failures)
    {
        var identity = new FileIdentity(disk, folder, item.Id, 1);
        var observed = failures.Observe(identity);
        var availability = SyncRootAvailability.Unknown;
        try
        {
            var path = Program.ResolveLocalItemPath(disk, folder, item);
            // An active local-change upload (including a retry after an earlier failure) wins.
            if (path is not null && UploadActivity.TryGet(path, out var upload))
                return (SyncRootAvailability.Local, FileSyncError.None, UploadCellActivity(upload));
            availability = path is null ? SyncRootAvailability.OnlineOnly : DragFilePreparation.ReadAvailability(path);
            // Sync-folder hydration from any source: this window's "下载", Explorer, or another app.
            if (path is not null && availability == SyncRootAvailability.OnlineOnly && Callbacks.IsHydrationActive(item.Id))
            {
                var (size, ready, isReady) = DragFilePreparation.ReadMetadata(path);
                if (!isReady) return (availability, FileSyncError.None, DownloadCellActivity(ready, size));
            }
            if (path is not null && Callbacks.HasWritebackFailure(path)) return (availability, FileSyncError.Writeback, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { /* Inaccessible metadata is not itself a transfer failure. */ }
        return (availability, failures.HasFailure(identity, availability, observed) ? FileSyncError.Download : FileSyncError.None, null);
    }

    private void SetAvailability(Border cell, SyncRootAvailability state, FileSyncError error = FileSyncError.None,
        CellActivity? activity = null)
    {
        if (cell.Tag is ValueTuple<SyncRootAvailability, FileSyncError, CellActivity?> previous
            && previous == (state, error, activity)) return;
        cell.Tag = (state, error, activity);
        var dark = _root.ActualTheme == ElementTheme.Dark;
        if (activity is { } active)
        {
            SetCellActivity(cell, active, dark);
            return;
        }
        var green = new SolidColorBrush(dark ? Microsoft.UI.Colors.LightGreen : Windows.UI.Color.FromArgb(255, 16, 124, 16));
        var description = error switch
        {
            FileSyncError.Download => "下载失败。可右键选择“下载”重试。",
            FileSyncError.Writeback => "同步错误：无法将本地更改上传到云端。",
            _ => state switch
            {
                SyncRootAvailability.OnlineOnly => "仅联机可用",
                SyncRootAvailability.Local => "在此设备上可用",
                SyncRootAvailability.Pinned => "始终在此设备上可用",
                _ => "无法读取可用状态",
            },
        };
        ToolTipService.SetToolTip(cell, description);
        AutomationProperties.SetName(cell, description);
        if (error != FileSyncError.None)
        {
            var red = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 255, 112, 112) : Windows.UI.Color.FromArgb(255, 196, 43, 28));
            cell.Child = new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(8), Background = red,
                Child = new FontIcon { Glyph = "\uE711", FontSize = 8, Foreground = new SolidColorBrush(dark
                    ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White) },
            };
            return;
        }
        cell.Child = state switch
        {
            SyncRootAvailability.OnlineOnly => new FontIcon { Glyph = "\uE753", FontSize = 15,
                Foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.DeepSkyBlue : Windows.UI.Color.FromArgb(255, 0, 120, 212)) },
            SyncRootAvailability.Local or SyncRootAvailability.Pinned => new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.4),
                BorderBrush = green, Background = state == SyncRootAvailability.Pinned ? green : null,
                Child = new FontIcon { Glyph = "\uE73E", FontSize = 8, Foreground = state == SyncRootAvailability.Pinned
                    ? new SolidColorBrush(dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White) : green },
            },
            _ => null,
        };
    }

    /// <summary>
    /// Upload: upload arrow, then download arrow while the uploaded copy is read back and
    /// compared, then a check mark while the local identity is bound. The ring spans upload
    /// and verification; it spins while waiting for the server.
    /// </summary>
    private static CellActivity UploadCellActivity(UploadActivitySnapshot upload) => new(
        upload.Stage switch
        {
            UploadStage.Verifying => "\uE896",
            UploadStage.Finalizing => "\uE73E",
            _ => "\uE898",
        },
        upload.Stage is UploadStage.Preparing or UploadStage.Confirming ? null : upload.OverallPercent,
        upload.Describe());

    private static CellActivity DownloadCellActivity(long? completed, long total)
    {
        if (completed is not { } done || total <= 0) return new("\uE896", null, "正在下载");
        var percent = Math.Clamp(done * 100d / total, 0, 100);
        return new("\uE896", Math.Min(percent, 99.5), $"正在下载 {percent:F0}% · {FormatSize(done)} / {FormatSize(total)}");
    }

    private CellActivity? BrowserDownloadActivity(ulong id)
        => _browserDownloads.TryGetValue(id, out var download) ? DownloadCellActivity(download.Completed, download.Total) : null;

    /// <summary>Called from a main-window download's progress timer; null completed ends the download.</summary>
    private void SetBrowserDownloadProgress(ulong id, long? completed, long total)
    {
        if (completed is { } done) _browserDownloads[id] = (done, total);
        else if (!_browserDownloads.Remove(id)) return;
        var activity = BrowserDownloadActivity(id);
        if (activity is null)
        {
            RefreshFileAvailability();
            return;
        }
        foreach (var (item, cell) in _availabilityRows)
        {
            if (item.Id != id) continue;
            var (state, error) = cell.Tag is ValueTuple<SyncRootAvailability, FileSyncError, CellActivity?> previous
                ? (previous.Item1, previous.Item2) : (SyncRootAvailability.OnlineOnly, FileSyncError.None);
            SetAvailability(cell, state, error, activity);
        }
    }

    /// <summary>Progress ring around a stage glyph; reused across updates so the ring animates smoothly.</summary>
    private static void SetCellActivity(Border cell, CellActivity activity, bool dark)
    {
        ToolTipService.SetToolTip(cell, activity.Description);
        AutomationProperties.SetName(cell, activity.Description);
        if (cell.Child is not Grid { Tag: "transfer" } visual
            || visual.Children is not [ProgressRing ring, FontIcon icon])
        {
            var accent = dark ? Microsoft.UI.Colors.DeepSkyBlue : Windows.UI.Color.FromArgb(255, 0, 120, 212);
            ring = new ProgressRing
            {
                Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, Minimum = 0, Maximum = 100,
                IsActive = true, IsHitTestVisible = false,
                Foreground = new SolidColorBrush(accent),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(56, accent.R, accent.G, accent.B)),
            };
            icon = new FontIcon
            {
                FontSize = 8, Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            visual = new Grid { Tag = "transfer", Width = 16, Height = 16 };
            visual.Children.Add(ring);
            visual.Children.Add(icon);
            cell.Child = visual;
        }
        ring.IsIndeterminate = activity.Percent is null;
        if (activity.Percent is { } percent) ring.Value = percent;
        icon.Glyph = activity.Glyph;
    }

    private static async Task<IReadOnlyList<YuWangItem>> ListSyncRootFolderChildrenAsync(YuWangClient client,
        ulong disk, ulong parent, YuWangItem item, CancellationToken token)
    {
        await Program.PrepareSyncRootItemAsync(client, disk, parent, item, token).ConfigureAwait(false);
        var listing = await client.ListFolderWithPermissionsAsync(disk, item.Id).WaitAsync(token).ConfigureAwait(false);
        if (!listing.IsValidPage) throw new IOException($"“{item.Name}”的目录列表不完整。");
        token.ThrowIfCancellationRequested();
        if (!Program.IsDragBackendAvailable || !ReferenceEquals(client, Program.ClientForUi))
            throw new IOException("同步连接已更改，请重试。");
        Callbacks.StoreMainWindowDirectoryListing(new FileIdentity(disk, parent, item.Id, 0),
            parent, item.Name, listing.Items, listing.Permission);
        return listing.Items;
    }

    private async Task HydrateItemsAsync(ItemActionTarget? target)
    {
        if (target is null || !IsItemTargetAvailable(target)) return;
        await RunBusyAsync($"正在下载 {target.Items.Count} 项到同步目录…", async () =>
        {
            var client = Client ?? throw new IOException("尚未连接到云端。");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Program.ApplicationCancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_config.DownloadTimeoutMinutes, 1, 120)));
            _syncDownloadCancellation = cancellation;
            var token = cancellation.Token;
            var progress = new DragProgressState();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                var current = progress.Latest;
                var text = current.FileName is { Length: > 0 } name ? $"{current.Stage} · {ShortenFileName(name)}" : current.Stage;
                if (current.Percent is { } percent)
                    text += $" · {percent:F2}% · {FormatSize(current.CompletedBytes)} / {FormatSize(current.TotalBytes)}";
                SetTransferProgress(text, current.Percent ?? 0);
                _transferProgress.IsIndeterminate = current.Percent is null;
            };
            timer.Start();
            // Show per-file rings in the status column promptly; it slows down again when idle.
            _fileStatusTimer.Interval = ActiveStatusInterval;
            try
            {
                var ancestors = new HashSet<ulong> { target.Parent.Id };
                foreach (var item in target.Items)
                    await HydrateSyncRootEntryAsync(client, target.Parent.Disk.Id, target.Parent.Id, item,
                        ancestors, progress.Report, token);
                SetStatus("");
                ShowInfo(InfoBarSeverity.Success, "下载完成", $"已将 {target.Items.Count} 项下载到同步目录。");
            }
            finally
            {
                timer.Stop();
                _transferProgress.IsIndeterminate = false;
                _syncDownloadCancellation = null;
                RefreshFileAvailability();
            }
        }, "下载失败");
    }

    private static async Task HydrateSyncRootEntryAsync(YuWangClient client, ulong disk, ulong parent, YuWangItem item,
        HashSet<ulong> ancestors, Action<DragPreparationProgress> report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        report(new(item.IsFolder ? "正在读取目录" : "正在准备文件", item.Name));
        if (item.IsFolder)
        {
            if (!ancestors.Add(item.Id)) throw new IOException("云端目录层级成环。");
            try
            {
                var children = await ListSyncRootFolderChildrenAsync(client, disk, parent, item, token).ConfigureAwait(false);
                foreach (var child in children)
                    await HydrateSyncRootEntryAsync(client, disk, item.Id, child, ancestors, report, token).ConfigureAwait(false);
            }
            finally { ancestors.Remove(item.Id); }
            return;
        }
        using var content = await HydrateSyncRootFileAsync(client, disk, parent, item, report, token).ConfigureAwait(false);
    }

    private static async Task<VirtualFileContent> HydrateSyncRootFileAsync(YuWangClient client, ulong disk, ulong parent,
        YuWangItem item, Action<DragPreparationProgress> report, CancellationToken token)
    {
        var failures = Callbacks.DownloadFailures;
        var identity = new FileIdentity(disk, parent, item.Id, 1);
        var observed = failures.Observe(identity);
        try
        {
            var path = await Program.PrepareSyncRootItemAsync(client, disk, parent, item, token).ConfigureAwait(false);
            var lease = await DragFilePreparation.CreateAsync([path], () => Program.IsDragBackendAvailable
                && ReferenceEquals(client, Program.ClientForUi), _ => { }, token, report).ConfigureAwait(false);
            failures.HasFailure(identity, SyncRootAvailability.Local, observed);
            return new(path, lease);
        }
        catch (Exception error) when (error is not OperationCanceledException && !token.IsCancellationRequested
            && ReferenceEquals(client, Program.ClientForUi))
        {
            failures.Record(identity);
            throw;
        }
    }
}
