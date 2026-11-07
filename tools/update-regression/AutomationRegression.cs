using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Updates;

internal static class AutomationRegression
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(new DiagnosticSink()).CreateLogger();
        var testTemp = Path.Combine(AppContext.BaseDirectory, "automation-temp-" + Guid.NewGuid().ToString("N"));
        var previousTemp = Environment.GetEnvironmentVariable("TEMP");
        var previousTmp = Environment.GetEnvironmentVariable("TMP");
        Directory.CreateDirectory(testTemp);
        try
        {
            // Keep installer cache tests isolated from real updater files and their ACLs.
            Environment.SetEnvironmentVariable("TEMP", testTemp);
            Environment.SetEnvironmentVariable("TMP", testTemp);
            CheckConfiguration(check);
            await CheckCleanupScheduleAsync(check);
            await CheckAutomaticDownloadAsync(check);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", previousTemp);
            Environment.SetEnvironmentVariable("TMP", previousTmp);
            Directory.Delete(testTemp, recursive: true);
        }
    }

    private static void CheckConfiguration(Action<string, bool> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "config-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(path, "{}");
            var defaults = ConfigurationStore.Load(configurationPath: path, decryptPassword: false, migrate: false);
            check("legacy config leaves new automation opt-in with a daily interval",
                !defaults.AutomaticFileCacheCleanup && !defaults.AutomaticUpdateDownload
                && defaults.FileCacheCleanupHours == 24);
            foreach (var hours in new[] { 8, 12, 24, 168 })
            {
                defaults.ProtectedPassword = "test-only-placeholder";
                defaults.AutomaticFileCacheCleanup = true;
                defaults.FileCacheCleanupHours = hours;
                defaults.AutomaticUpdateDownload = true;
                ConfigurationStore.SaveEncrypted(defaults, path);
                var restored = ConfigurationStore.Load(configurationPath: path, decryptPassword: false, migrate: false);
                check($"configuration round-trips automation with {hours} hour interval",
                    restored.AutomaticFileCacheCleanup && restored.AutomaticUpdateDownload
                    && restored.FileCacheCleanupHours == hours);
            }
            check("invalid cache cleanup intervals fall back to daily",
                new[] { 0, -1, 7, 999 }.All(value => SyncRootContext.NormalizeFileCacheCleanupHours(value) == 24));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckCleanupScheduleAsync(Action<string, bool> check)
    {
        var delays = Channel.CreateUnbounded<PendingDelay>();
        var cleanupCount = 0;
        var service = new FileCacheCleanupService(_ => Interlocked.Increment(ref cleanupCount),
            message => throw new Exception(message), (interval, token) =>
            {
                var pending = new PendingDelay(interval, token);
                delays.Writer.TryWrite(pending);
                return pending.Completion.Task.WaitAsync(token);
            });
        try
        {
            service.Configure(true, 8);
            var first = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            check("enabling automatic cleanup waits the selected interval before the first scan",
                first.Interval == TimeSpan.FromHours(8) && cleanupCount == 0);
            service.Configure(true, 8);
            check("applying unrelated settings does not reset the cleanup timer",
                !first.Token.IsCancellationRequested && !delays.Reader.TryRead(out _));
            first.Completion.SetResult();
            var second = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            check("automatic cleanup repeats after its interval", cleanupCount == 1
                && second.Interval == TimeSpan.FromHours(8));
            service.Configure(true, 168);
            var third = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            check("changing cleanup interval cancels the old timer and uses seven days",
                second.Token.IsCancellationRequested && third.Interval == TimeSpan.FromDays(7));
            service.Configure(false, 168);
            service.Stop();
            check("disabling automatic cleanup cancels the timer without an extra scan",
                third.Token.IsCancellationRequested && cleanupCount == 1);
        }
        finally { service.Stop(); }

        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanFinished = false;
        var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeService = new FileCacheCleanupService(token =>
        {
            scanStarted.SetResult();
            try
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                token.ThrowIfCancellationRequested();
            }
            finally { scanFinished = true; }
        }, message => throw new Exception(message), (_, token) => run.Task.WaitAsync(token));
        try
        {
            activeService.Configure(true, 12);
            run.SetResult();
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            activeService.Stop();
            check("stopping cleanup waits for the active scan before provider disconnect", scanFinished);
        }
        finally { activeService.Stop(); }
    }

    private static async Task CheckAutomaticDownloadAsync(Action<string, bool> check)
    {
        var payload = Encoding.UTF8.GetBytes("test payload, never executed: " + Guid.NewGuid());
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var asset = new GitHubReleaseAsset(ProductIdentity.Identifier + ".exe", payload.Length, digest);
        var release = new GitHubRelease("v99.0.0.0", new Version(99, 0, 0, 0), null, "test", [asset]);
        var directory = UpdateTemporaryFiles.GetDownloadDirectory(digest);
        var path = Path.Combine(directory, asset.Name);
        var handler = new DownloadHandler(payload);
        using var http = new HttpClient(handler);
        var idle = new UpdateUiState(true, release, asset, UpdateActivity.Idle, 0, asset.Size, null);
        SetField("_state", idle);
        SetField("_releaseClient", new GitHubReleaseClient(http, "regression"));
        SetField("_started", 1);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(UpdateUiState state)
        {
            if (state.ErrorMessage is not null) ready.TrySetException(new InvalidOperationException(state.ErrorMessage));
            if (state.IsDownloaded && state.Activity == UpdateActivity.Idle) ready.TrySetResult();
        }
        UpdateService.StateChanged += Observe;
        try
        {
            check("an available update initially offers the update action", idle.UpdateActionLabel == "更新");
            UpdateService.SetAutomaticDownloadEnabled(true);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Wait for the complete operation, including gate release after its final notification.
            var gate = (SemaphoreSlim)GetField("UpdateGate")!;
            await gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            gate.Release();
            var state = UpdateService.CurrentState;
            check("automatic download stops at ready and offers restart update without launching",
                state.IsDownloaded && state.Activity == UpdateActivity.Idle
                && state.UpdateActionLabel == "重启更新" && state.DownloadPercent == 100
                && handler.RequestCount == 1 && File.ReadAllBytes(path).SequenceEqual(payload));
            UpdateService.SetAutomaticDownloadEnabled(false);
            check("disabling automatic download retains the verified ready package", File.Exists(path)
                && UpdateService.CurrentState.IsDownloaded);

            SetField("_state", idle);
            await PrepareAsync(allowDownload: false);
            check("restart restores the ready action by verifying the cached installer offline",
                UpdateService.CurrentState.IsDownloaded && handler.RequestCount == 1);

            var damaged = (byte[])payload.Clone();
            damaged[0] ^= 1;
            File.WriteAllBytes(path, damaged);
            check("same-size modified cached installers fail SHA-256 verification",
                !await GitHubReleaseClient.IsVerifiedInstallerAsync(asset, path, CancellationToken.None));
            SetField("_state", idle);
            await PrepareAsync(allowDownload: false);
            check("corrupted cached package cannot restore restart update or cause a download when disabled",
                !UpdateService.CurrentState.IsDownloaded && !File.Exists(path) && handler.RequestCount == 1);
            await PrepareAsync(allowDownload: true);
            check("a missing or corrupted package can be downloaded again and becomes ready",
                UpdateService.CurrentState.IsDownloaded && handler.RequestCount == 2);

            UpdateTemporaryFiles.TryDeleteTransactionDirectory(directory);
            SetField("_state", idle);
            var slowHandler = new CancellableDownloadHandler();
            using var slowHttp = new HttpClient(slowHandler);
            SetField("_releaseClient", new GitHubReleaseClient(slowHttp, "regression"));
            UpdateService.SetAutomaticDownloadEnabled(true);
            await slowHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            UpdateService.SetAutomaticDownloadEnabled(false);
            await gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            gate.Release();
            check("disabling automatic download cancels an active transfer and removes partial files",
                !Directory.Exists(directory) && !UpdateService.CurrentState.IsDownloaded
                && UpdateService.CurrentState.Activity == UpdateActivity.Idle
                && UpdateService.CurrentState.ErrorMessage is null);
        }
        finally
        {
            UpdateService.SetAutomaticDownloadEnabled(false);
            UpdateService.StateChanged -= Observe;
            SetField("_started", 0);
            SetField("_state", UpdateUiState.Empty);
            SetField("_releaseClient", null);
            UpdateTemporaryFiles.TryDeleteTransactionDirectory(directory);
        }
    }

    private static Task PrepareAsync(bool allowDownload) => (Task)typeof(UpdateService)
        .GetMethod("PrepareUpdateAsync", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [false, allowDownload, CancellationToken.None])!;

    private static object? GetField(string name) => typeof(UpdateService)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    private static void SetField(string name, object? value) => typeof(UpdateService)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    private sealed record PendingDelay(TimeSpan Interval, CancellationToken Token)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DownloadHandler(byte[] payload) : HttpMessageHandler
    {
        internal int RequestCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    private sealed class DiagnosticSink : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent value)
        {
            if (value.Exception is not null) Console.Error.WriteLine(value.Exception);
        }
    }

    private sealed class CancellableDownloadHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled download unexpectedly continued.");
        }
    }
}
