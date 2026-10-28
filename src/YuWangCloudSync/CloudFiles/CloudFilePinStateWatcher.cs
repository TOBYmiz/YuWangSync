using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Vanara.PInvoke;
using YuWangCloudSync.Core;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync.CloudFiles;

[SupportedOSPlatform("windows")]
internal sealed class CloudFilePinStateWatcher
{
    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (CF_PIN_STATE PinState, long OnDiskDataSize)> _lastState =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _idle = new(true);
    private FileSystemWatcher? _watcher;
    private volatile bool _stopping;
    private int _active;

    public CloudFilePinStateWatcher(string rootPath, Action<string> log)
    {
        _rootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _rootPrefix = _rootPath;
        _log = log;
    }

    public void Start()
    {
        if (_watcher != null) return;
        if (!Directory.Exists(_rootPath)) throw new DirectoryNotFoundException(_rootPath);

        var watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = false
        };
        watcher.Changed += (_, e) => Queue(e.FullPath);
        watcher.Error += (_, e) => _log($"[PIN] 目录属性监视器错误: {e.GetException().Message}");
        _watcher = watcher;
        watcher.EnableRaisingEvents = true;
        _log($"[PIN] 已开始监视 SyncRoot 属性变化: {_rootPath}");
    }

    public void Stop()
    {
        _stopping = true;
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        if (!_idle.Wait(TimeSpan.FromSeconds(10)))
            _log("[PIN] 等待正在执行的水化/脱水操作超时，将继续关闭 Provider");
    }

    private void Queue(string path)
    {
        if (_stopping) return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        if (!fullPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!_queued.TryAdd(fullPath, 0)) return;

        Interlocked.Increment(ref _active);
        _idle.Reset();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                await ProcessPathAsync(fullPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[PIN] 属性变化处理异常: {ex.Message}");
            }
            finally
            {
                _queued.TryRemove(fullPath, out _);
                if (Interlocked.Decrement(ref _active) == 0) _idle.Set();
            }
        });
    }

    private async Task ProcessPathAsync(string path)
    {
        if (_stopping || !File.Exists(path) || Directory.Exists(path)
            || !ProviderIdentity.OwnsPlaceholder(path))
            return;
        if (!PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state))
        {
            _log("[PIN] 收到占位属性变化，但读取 CFAPI 状态失败");
            return;
        }

        long fileSize;
        try { fileSize = new FileInfo(path).Length; }
        catch { return; }

        var currentState = (state.PinState, state.OnDiskDataSize);
        if (_lastState.TryGetValue(path, out var previousState) && previousState == currentState)
            return;
        _lastState[path] = currentState;
        _log($"[PIN] 捕获占位属性变化 file={state.FileId} pin={state.PinState} " +
             $"inSync={state.InSyncState} onDisk={state.OnDiskDataSize}/{fileSize}");

        switch (state.PinState)
        {
            case CF_PIN_STATE.CF_PIN_STATE_PINNED:
                if (state.OnDiskDataSize >= fileSize) return;
                var hydrated = PlaceholderUpdater.TryHydratePlaceholder(path, _log);
                _log($"[PIN] 水化请求结束 file={state.FileId} success={hydrated}");
                break;

            case CF_PIN_STATE.CF_PIN_STATE_UNPINNED:
                await DehydrateUnpinnedFileAsync(path, state.FileId).ConfigureAwait(false);
                break;
        }
    }

    private async Task DehydrateUnpinnedFileAsync(string path, long fileId)
    {
        int[] retryDelaysMs = [0, 250, 500, 1_000, 2_000];
        for (var attempt = 0; attempt < retryDelaysMs.Length; attempt++)
        {
            if (_stopping) return;
            if (retryDelaysMs[attempt] > 0)
                await Task.Delay(retryDelaysMs[attempt]).ConfigureAwait(false);

            if (!File.Exists(path)
                || !PlaceholderUpdater.TryGetPlaceholderDataState(path, out var state)
                || state.PinState != CF_PIN_STATE.CF_PIN_STATE_UNPINNED)
                return;
            if (state.OnDiskDataSize <= 0) return;
            if (state.InSyncState != CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
            {
                _log($"[PIN] 暂缓脱水 file={fileId}：当前不在同步状态 ({state.InSyncState})");
                continue;
            }

            var dehydrated = PlaceholderUpdater.TryDehydrate(path, out var freedBytes, _log);
            if (dehydrated)
            {
                _log($"[PIN] UNPINNED 脱水完成 file={fileId} freed={freedBytes}");
                return;
            }
        }

        _log($"[PIN] UNPINNED 脱水尚未完成 file={fileId}；稍后属性变化时会再次检查");
    }
}
