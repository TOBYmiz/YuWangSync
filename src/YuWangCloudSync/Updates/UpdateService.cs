using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Serilog;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Updates;

internal enum UpdateActivity
{
    Idle,
    Checking,
    Connecting,
    Downloading,
    Launching,
}

internal enum UpdateCheckOutcome
{
    UpdateAvailable,
    NoUpdate,
    Failed,
    Busy,
    Canceled,
}

internal sealed record UpdateUiState(
    bool IsAvailable,
    GitHubRelease? Release,
    GitHubReleaseAsset? Asset,
    UpdateActivity Activity,
    long DownloadedBytes,
    long TotalBytes,
    string? ErrorMessage)
{
    internal static UpdateUiState Empty { get; } = new(false, null, null, UpdateActivity.Idle, 0, 0, null);

    internal int DownloadPercent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(DownloadedBytes * 100 / TotalBytes, 0, 100);
}

internal static class UpdateService
{
    private static readonly object StateGate = new();
    private static readonly object ScheduleGate = new();
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static UpdateUiState _state = UpdateUiState.Empty;
    private static GitHubReleaseClient? _releaseClient;
    private static string? _assetName;
    private static DateOnly? _lastSuccessfulCheckDate;
    private static CancellationToken _applicationToken;
    private static CancellationTokenSource? _scheduleCancellation;
    private static int _started;

    internal static event Action<UpdateUiState>? StateChanged;

    internal static UpdateUiState CurrentState
    {
        get { lock (StateGate) return _state; }
    }

    internal static void Start(bool automaticCheckEnabled, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        try
        {
            _applicationToken = cancellationToken;
            var variant = UpdateInstallVariantResolver.Resolve();
            _assetName = UpdateInstallVariantResolver.GetAssetName(variant);
            _releaseClient = new GitHubReleaseClient(HttpClient,
                ProductIdentity.DisplayNameEn + "/" + ProductVersion.Current);
            RestorePersistedState(ReadPersistedState(), _assetName);
            Publish(CurrentState);
            Log.Information("更新服务已初始化；安装变体={Variant}；Release 资产={AssetName}", variant, _assetName);
            SetAutomaticCheckEnabled(automaticCheckEnabled);
        }
        catch (Exception error)
        {
            Log.Warning(error, "自动更新检查初始化失败；同步程序继续运行");
            Interlocked.Exchange(ref _started, 0);
        }
    }

    internal static void SetAutomaticCheckEnabled(bool enabled)
    {
        lock (ScheduleGate)
        {
            if (Volatile.Read(ref _started) == 0 || _releaseClient is null) return;

            if (!enabled)
            {
                var previous = _scheduleCancellation;
                _scheduleCancellation = null;
                previous?.Cancel();
                Log.Information("自动检查更新已关闭；已发现的更新仍可手动安装");
                return;
            }

            if (_scheduleCancellation is not null) return;
            var scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
            _scheduleCancellation = scheduleCancellation;
            _ = Task.Run(() => RunScheduleLifetimeAsync(scheduleCancellation), CancellationToken.None);
            Log.Information("自动检查更新已启用");
        }
    }

    internal static async Task DownloadAndLaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await UpdateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        string? transactionDirectory = null;
        var installerStarted = false;
        try
        {
            var current = CurrentState;
            if (!current.IsAvailable || current.Release is null || current.Asset is null || _releaseClient is null)
                return;

            Publish(current with
            {
                Activity = UpdateActivity.Connecting,
                DownloadedBytes = 0,
                TotalBytes = current.Asset.Size,
                ErrorMessage = null,
            });

            transactionDirectory = UpdateTemporaryFiles.CreateTransactionDirectory();
            var installerPath = Path.Combine(transactionDirectory, current.Asset.Name);
            var progress = new InlineProgress<DownloadProgress>(value =>
            {
                var state = CurrentState;
                Publish(state with
                {
                    Activity = value.BytesRead == 0 ? UpdateActivity.Connecting : UpdateActivity.Downloading,
                    DownloadedBytes = value.BytesRead,
                    TotalBytes = value.TotalBytes,
                });
            });
            await _releaseClient.DownloadAndVerifyAsync(current.Asset, installerPath, progress, cancellationToken)
                .ConfigureAwait(false);

            Publish(CurrentState with
            {
                Activity = UpdateActivity.Launching,
                DownloadedBytes = current.Asset.Size,
                TotalBytes = current.Asset.Size,
                ErrorMessage = null,
            });

            var startInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                WorkingDirectory = transactionDirectory,
            };
            startInfo.ArgumentList.Add("-quick-update");
            var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows 未能启动已校验的更新安装器。");
            installerStarted = true;
            _ = MonitorInstallerAsync(installer, transactionDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!installerStarted && transactionDirectory is not null)
                UpdateTemporaryFiles.TryDeleteTransactionDirectory(transactionDirectory);
            return;
        }
        catch (Exception error)
        {
            Log.Warning(error, "自动更新下载或启动失败");
            if (!installerStarted && transactionDirectory is not null)
                UpdateTemporaryFiles.TryDeleteTransactionDirectory(transactionDirectory);
            var state = CurrentState;
            Publish(state with
            {
                Activity = UpdateActivity.Idle,
                ErrorMessage = IsConnectionFailure(error)
                    ? "连接失败。"
                    : "更新失败，程序保持当前版本。" + Environment.NewLine + error.Message,
            });
        }
        finally
        {
            UpdateGate.Release();
        }
    }

    internal static async Task<UpdateCheckOutcome> CheckNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await CheckGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return UpdateCheckOutcome.Busy;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckOutcome.Canceled;
        }

        try
        {
            var succeeded = await CheckOnceCoreAsync(DateOnly.FromDateTime(DateTime.Now), cancellationToken,
                showFailure: true).ConfigureAwait(false);
            if (!succeeded) return UpdateCheckOutcome.Failed;
            return CurrentState.IsAvailable ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.NoUpdate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckOutcome.Canceled;
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static async Task RunScheduleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var checkDate = DateOnly.FromDateTime(DateTime.Now);
            if (UpdateSchedule.IsDue(_lastSuccessfulCheckDate, checkDate))
            {
                while (CurrentState.Activity is UpdateActivity.Checking or UpdateActivity.Connecting
                    or UpdateActivity.Downloading or UpdateActivity.Launching)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                }

                checkDate = DateOnly.FromDateTime(DateTime.Now);
            }
            if (UpdateSchedule.IsDue(_lastSuccessfulCheckDate, checkDate))
                await CheckOnceAsync(checkDate, cancellationToken).ConfigureAwait(false);

            var delay = UpdateSchedule.UntilNextLocalDay(DateTime.Now);
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static async Task RunScheduleLifetimeAsync(CancellationTokenSource scheduleCancellation)
    {
        try
        {
            await RunScheduleAsync(scheduleCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scheduleCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (ScheduleGate)
            {
                if (ReferenceEquals(_scheduleCancellation, scheduleCancellation))
                    _scheduleCancellation = null;
            }
            scheduleCancellation.Dispose();
        }
    }

    private static async Task CheckOnceAsync(DateOnly checkDate, CancellationToken cancellationToken)
    {
        try
        {
            if (!await CheckGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await CheckOnceCoreAsync(checkDate, cancellationToken, showFailure: false).ConfigureAwait(false);
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static async Task<bool> CheckOnceCoreAsync(
        DateOnly checkDate,
        CancellationToken cancellationToken,
        bool showFailure)
    {
        var previous = CurrentState;
        Publish(previous with { Activity = UpdateActivity.Checking, ErrorMessage = null });
        try
        {
            var client = _releaseClient ?? throw new InvalidOperationException("更新客户端尚未初始化。");
            var latest = await client.GetLatestReleaseAsync(_assetName!, cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                Log.Information("GitHub 仓库当前没有已发布 Release");
                PersistAndPublish(new PersistedUpdateState(checkDate, null, null, null, null), null, null, false);
                return true;
            }

            var asset = latest.Assets.FirstOrDefault(item =>
                string.Equals(item.Name, _assetName, StringComparison.Ordinal));
            var isNewer = UpdateSchedule.IsNewerVersion(latest.Version, ProductVersion.Current);
            var hasValidAsset = asset is not null && asset.Size > 0
                && GitHubReleaseClient.TryNormalizeDigest(asset.Sha256, out _);

            if (isNewer && !hasValidAsset)
                Log.Warning("发现较新版本 {Tag}，但 Release 缺少安装变体 {AssetName} 或有效 digest", latest.TagName, _assetName);
            else
                Log.Information("更新检查完成：本地={CurrentVersion}，最新={LatestVersion}，可更新={Available}",
                    ProductVersion.Current, latest.Version, isNewer);

            var persisted = new PersistedUpdateState(checkDate, latest.TagName,
                hasValidAsset ? asset!.Name : null,
                hasValidAsset ? asset!.Size : null,
                hasValidAsset ? asset!.Sha256 : null,
                latest.PublishedDate,
                latest.Body);
            PersistAndPublish(persisted, latest,
                hasValidAsset ? asset : null,
                isNewer && hasValidAsset);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(CurrentState with { Activity = UpdateActivity.Idle, ErrorMessage = null });
            throw;
        }
        catch (Exception error)
        {
            Log.Warning(error, "更新检查未完成；稍后启动或下个本地日历日会重试");
            Publish(CurrentState with
            {
                Activity = UpdateActivity.Idle,
                ErrorMessage = showFailure
                    ? error is HttpRequestException
                        ? "连接失败。"
                        : "检查更新失败，程序保持当前版本。" + Environment.NewLine + error.Message
                    : null,
            });
            return false;
        }
    }

    private static bool IsConnectionFailure(Exception error) =>
        error is HttpRequestException
        || error.InnerException is HttpRequestException or OperationCanceledException;

    private static void PersistAndPublish(
        PersistedUpdateState persisted,
        GitHubRelease? release,
        GitHubReleaseAsset? asset,
        bool isAvailable)
    {
        WritePersistedState(persisted);
        _lastSuccessfulCheckDate = persisted.LastSuccessfulCheckDate;
        Publish(new UpdateUiState(isAvailable, release, asset, UpdateActivity.Idle,
            0, asset?.Size ?? 0, null));
    }

    private static PersistedUpdateState ReadPersistedState()
    {
        try
        {
            var path = AppPaths.ResolveDataPath("update-state.json");
            return UpdateStateStore.Read(path)
                ?? new PersistedUpdateState(null, null, null, null, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Log.Warning(error, "无法读取自动更新状态；将重新检查");
            return new PersistedUpdateState(null, null, null, null, null);
        }
    }

    private static void RestorePersistedState(PersistedUpdateState persisted, string assetName)
    {
        _lastSuccessfulCheckDate = persisted.LastSuccessfulCheckDate;
        if (persisted.LatestTagName is null
            || !GitHubReleaseClient.TryParseReleaseVersion(persisted.LatestTagName, out var version)
            || !string.Equals(persisted.AssetName, assetName, StringComparison.Ordinal)
            || persisted.AssetSize is null or <= 0
            || !GitHubReleaseClient.TryNormalizeDigest(persisted.Sha256, out var digest))
        {
            _state = UpdateUiState.Empty;
            return;
        }

        var release = new GitHubRelease(persisted.LatestTagName, version,
            persisted.PublishedDate,
            persisted.ReleaseNotes ?? "",
        [new GitHubReleaseAsset(assetName, persisted.AssetSize.Value, digest)]);
        var asset = release.Assets[0];
        var available = UpdateSchedule.IsNewerVersion(version, ProductVersion.Current);
        _state = new UpdateUiState(available, release, asset, UpdateActivity.Idle, 0, asset.Size, null);
    }

    private static void WritePersistedState(PersistedUpdateState state)
    {
        var path = AppPaths.ResolveDataPath("update-state.json");
        UpdateStateStore.Write(path, state);
    }

    private static async Task MonitorInstallerAsync(
        Process installer,
        string transactionDirectory,
        CancellationToken cancellationToken)
    {
        using (installer)
        {
            try
            {
                await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return;

                UpdateTemporaryFiles.TryDeleteTransactionDirectory(transactionDirectory);
                var error = installer.ExitCode == 0
                    ? "安装器已退出，但当前程序仍在运行。请从托盘重新启动更新。"
                    : $"安装器退出代码：{installer.ExitCode}。";
                Log.Warning("快速更新安装器在旧程序仍运行时退出：{ExitCode}", installer.ExitCode);
                Publish(CurrentState with { Activity = UpdateActivity.Idle, ErrorMessage = error });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                Log.Warning(error, "等待快速更新安装器退出时失败");
                Publish(CurrentState with
                {
                    Activity = UpdateActivity.Idle,
                    ErrorMessage = "无法监视安装器状态：" + error.Message,
                });
            }
        }
    }

    private static void Publish(UpdateUiState state)
    {
        lock (StateGate) _state = state;
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (Action<UpdateUiState> handler in handlers.GetInvocationList())
        {
            try { handler(state); }
            catch (Exception error) { Log.Debug(error, "自动更新状态订阅方失败"); }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
