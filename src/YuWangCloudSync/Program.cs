using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;
using Vanara.PInvoke;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;
using YuWangCloudSync.Storage;
using YuWangCloudSync.Tray;
using YuWangCloudSync.Core;
using YuWangCloudSync.Updates;
using static Vanara.PInvoke.CldApi;

namespace YuWangCloudSync;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static SyncRootContext _ctx = new();
    private static YuWangClient? _client;
    private static SqliteCache? _cache;
    private static CF_CONNECTION_KEY _connectionKey;
    // 回调表必须保持存活：CfConnectSyncRoot 后 P/Invoke marshaller 会 pin 一段时间，
    // 但回调是进程生命周期的，数组本身不能被 GC 回收，因此存为静态字段。
    private static CF_CALLBACK_REGISTRATION[]? _callbackTable;
    private static CloudFilePinStateWatcher? _pinStateWatcher;
    private static readonly CancellationTokenSource _cts = new();
    internal static CancellationToken ApplicationCancellationToken => _cts.Token;

    // ============== 控制台控制事件（关闭窗口 / 停止进程 / 注销 / 关机）==============
    // 仅 Console.CancelKeyPress 只能捕获 Ctrl+C / Ctrl+Break；点击控制台窗口右上角 X、
    // 任务管理器"结束进程"之外的系统关闭、注销、关机都走 SetConsoleCtrlHandler。
    // 这里统一注册一个处理器，把以上事件全部引到与 Ctrl+C 相同的优雅退出流程
    // （_cts.Cancel() → 主循环退出 → 断开连接 + 反注册）。
    private enum CtrlType : uint
    {
        CTRL_C_EVENT = 0,
        CTRL_BREAK_EVENT = 1,
        CTRL_CLOSE_EVENT = 2,
        CTRL_LOGOFF_EVENT = 5,
        CTRL_SHUTDOWN_EVENT = 6,
    }

    private delegate bool ConsoleCtrlDelegate(CtrlType ctrlType);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handlerRoutine, bool add);

    // 控制台附着（双模收口：GUI 默认无控制台；--console 显式 AllocConsole 调试；
    // 默认 GUI 若从 cmd 启动可 AttachConsole 复用父控制台）。
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();
    private const int ATTACH_PARENT_PROCESS = -1;
    private const uint Utf8CodePage = 65001;
    private static uint _originalConsoleInputCodePage;
    private static uint _originalConsoleOutputCodePage;
    private static bool _restoreConsoleInputCodePage;
    private static bool _restoreConsoleOutputCodePage;

    private static ConsoleCtrlDelegate? _consoleCtrl;
    /// <summary>优雅清理完成后置位，供关闭/关机事件的处理器在系统超时前等待</summary>
    private static readonly System.Threading.ManualResetEventSlim _exitDone = new(false);
    /// <summary>退出流程幂等保护：保证清理只跑一次</summary>
    private static int _shutdownStarted;

    private static void StartPinStateWatcher()
    {
        StopPinStateWatcher();
        var watcher = new CloudFilePinStateWatcher(_ctx.SyncRootPath,
            message => Log.Information("{Msg}", message));
        try
        {
            watcher.Start();
            _pinStateWatcher = watcher;
        }
        catch (Exception ex)
        {
            watcher.Stop();
            Log.Warning(ex, "无法监视 SyncRoot 固定/释放属性变化；右键操作可能不会执行水化/脱水");
        }
    }

    private static void StopPinStateWatcher()
    {
        var watcher = Interlocked.Exchange(ref _pinStateWatcher, null);
        watcher?.Stop();
    }

    /// <summary>--quiet/--q：无头模式，无 UI、无控制台、仅写文件日志；无有效会话时直接退出（不能交互登录）。</summary>
    private static bool _quiet;
    /// <summary>--console：显式分配调试控制台（重新启用控制台日志与控制台读验证码）。</summary>
    private static bool _console;
    /// <summary>是否存在可用控制台：决定日志是否挂 Console sink，以及验证码用控制台还是 UI 弹窗读取。</summary>
    private static bool _hasConsole;
    /// <summary>--open-folder：启动同步后打开目录；若已有实例则只打开目录并退出。</summary>
    private static bool _openFolder;
    /// <summary>-about：完成单实例检查并初始化更新状态后显示关于窗口。</summary>
    private static bool _showAbout;
    private static bool _accountSwitchRestart;
    private static SyncRootContext? _pendingAccountSwitch;
    private static bool _restartAfterAccountSwitch;
    private static int _crashDialogShown;
    private static int _crashHandlersInstalled;
    private static string _startupStage = "进程入口";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // Keep the custom entry point's process-requirement check in sync with WinUI's generated main.
    [System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll", ExactSpelling = true)]
    private static extern void XamlCheckProcessRequirements();

    private static void ValidateWinUiResourceIndex()
    {
        // WinUI 3 uses MRT Core. Loading PRI files into the OS MRT ResourceManager
        // does not affect WinUI's resource resolution, even if those maps look complete.
        var resourceManager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();
        Log.Information("MRT Core 默认资源索引：{PriPath}",
            Microsoft.Windows.ApplicationModel.Resources.ResourceLoader.GetDefaultResourceFilePath());
        foreach (var resource in new[] { "Files/Microsoft.UI.Xaml/Themes/themeresources.xbf",
                     "Files/Microsoft.UI.Xaml/Themes/generic.xbf" })
        {
            var candidate = resourceManager.MainResourceMap.GetValue(resource);
            if (candidate.Kind != Microsoft.Windows.ApplicationModel.Resources.ResourceCandidateKind.EmbeddedData
                || candidate.ValueAsBytes.Length == 0)
                throw new InvalidDataException($"WinUI 主题资源不是有效的嵌入式 XBF：{resource}");
            Log.Information("MRT Core 资源：{Resource}; 类型={Kind}; 字节数={ByteCount}",
                resource, candidate.Kind, candidate.ValueAsBytes.Length);
        }
    }

    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSystemModal = 0x00001000;
    private const uint MessageBoxIconInformation = 0x00000040;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopMost = 0x00040000;

    /// <summary>
    /// 单实例互斥名。用 Local\ 前缀（会话命名空间）：任务计划以 Password 方式在用户会话内启动，
    /// 与本程序的其它实例同会话、同用户，跨完整性级别也能看到该对象。
    /// </summary>
    private const string SingleInstanceMutexName = ProductIdentity.SingleInstanceMutexName;
    /// <summary>单实例互斥体句柄：必须长期持有（静态字段防 GC），进程退出时句柄自动关闭、对象随之销毁。</summary>
    private static Mutex? _singleInstanceMutex;

    private static bool _uiSelfTest;
    private static int _uiSelfTestHoldSeconds;
    private static bool _uiSelfTestShot;

    /// <summary>解析 <c>--wpf-ui-selftest-hold=N</c>（也接受 <c>=N</c> 分开写）；非法值当 0。</summary>
    private static int ParseHoldSeconds(string[] args)
    {
        string[] prefixes = ["--ui-selftest-hold", "--wpf-ui-selftest-hold"];
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            foreach (var prefix in prefixes)
            {
                string? value = null;
                if (a.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase)) value = a[(prefix.Length + 1)..];
                else if (string.Equals(a, prefix, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) value = args[i + 1];
                if (value != null && int.TryParse(value, out var seconds)) return Math.Clamp(seconds, 0, 300);
            }
        }
        return 0;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        _quiet = args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--q", StringComparison.OrdinalIgnoreCase));
        InstallCrashHandlers();
        try
        {
            ConfigureLogger();
            Log.Information("启动入口：进程={Process}; Windows={Windows}; .NET={Runtime}",
                Environment.ProcessPath, Environment.OSVersion.VersionString, Environment.Version);
            if (args.Any(argument => string.Equals(argument, "--cleanup-update-temp", StringComparison.OrdinalIgnoreCase)))
                _ = Task.Run(UpdateTemporaryFiles.CleanupAfterUpdateAsync);
            var resourceBaseDirectory = Environment.GetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY");
            var resourceBaseProcessId = Environment.GetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID");
            Log.Information(
                "WinUI资源查找上下文：应用目录={AppBase}; WindowsAppRuntime目录={RuntimeBase}; Runtime目录进程号={RuntimePid}; 当前进程号={ProcessId}; ResourcesPri={ResourcesPri}; LegacyProjectPri={LegacyProjectPri}; ControlsPri={ControlsPri}; MicrosoftUiPri={MicrosoftUiPri}",
                AppContext.BaseDirectory,
                resourceBaseDirectory,
                resourceBaseProcessId,
                Environment.ProcessId,
                File.Exists(Path.Combine(AppContext.BaseDirectory, "resources.pri")),
                File.Exists(Path.Combine(AppContext.BaseDirectory, ProductIdentity.Identifier + ".pri")),
                File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.UI.Xaml.Controls.pri")),
                File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.UI.pri")));
            SetStartupStage("校验 WinUI XAML 进程要求");
            XamlCheckProcessRequirements();
            SetStartupStage("初始化 WinRT COM 包装器");
            App.LaunchArguments = args;
            WinRT.ComWrappersSupport.InitializeComWrappers();
            // Read the embedded XBF through the same MRT Core API that WinUI uses, so a
            // missing native resource implementation is reported before XAML initialization.
            SetStartupStage("校验 MRT Core 主题资源");
            ValidateWinUiResourceIndex();
            SetStartupStage("启动 WinUI 应用");
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                try
                {
                    SetStartupStage("创建 WinUI Application");
                    SetStartupStage("获取 WinUI DispatcherQueue");
                    var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                        ?? throw new InvalidOperationException("Application.Start 回调中没有 WinUI DispatcherQueue。");
                    SetStartupStage("设置 WinUI SynchronizationContext");
                    SynchronizationContext.SetSynchronizationContext(
                        new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue));
                    SetStartupStage("构造 WinUI App 实例");
                    new App();
                    SetStartupStage("WinUI App 构造完成");
                }
                catch (Exception error)
                {
                    Environment.ExitCode = 1;
                    try { HandleFatalException(error, _startupStage); } catch { }
                    try { Log.CloseAndFlush(); } catch { }
                    // Exceptions must not escape the native Application.Start callback. Doing so
                    // can terminate the process before the outer Main try/catch sees them.
                    Environment.Exit(1);
                }
            });
            SetStartupStage("WinUI 消息循环结束");
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            HandleFatalException(error, _startupStage);
            Log.CloseAndFlush();
        }
    }

    internal static void SetStartupStage(string stage)
    {
        _startupStage = stage;
        try { Log.Information("启动阶段：{Stage}", stage); } catch { }
    }

    internal static async Task RunApplicationAsync(string[] args)
    {
        SetStartupStage("后台同步流程启动");
        _uiSelfTest = args.Any(a => string.Equals(a, "--ui-selftest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--wpf-ui-selftest", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (_uiSelfTest)
            {
                await RunUiSelfTestAsync(args).ConfigureAwait(false);
                return;
            }
            await RunAsync(args);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            TrayHost.Shutdown();
            _client?.Dispose();
            _cache?.Dispose();
            Log.Information("启动已取消，同步进程退出");
            Log.CloseAndFlush();
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            HandleFatalException(error, "后台同步流程");
            Log.CloseAndFlush();
        }
    }

    private static async Task RunAsync(string[] args)
    {
        // Parse this early so fatal startup failures still honor --quiet.
        _quiet = args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(a, "--q", StringComparison.OrdinalIgnoreCase));
        _console = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
        _openFolder = args.Any(a => string.Equals(a, "--open-folder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "-open-folder", StringComparison.OrdinalIgnoreCase));
        _showAbout = args.Any(a => string.Equals(a, "-about", StringComparison.OrdinalIgnoreCase));
        if (args.Any(argument => argument.Equals("--stop", StringComparison.OrdinalIgnoreCase)))
        {
            try { await InstanceControl.StopAsync(); }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                HandleFatalException(error, "--stop");
            }
            return;
        }
        // 1. 加载配置
        try
        {
            LoadConfig(args);
        }
        catch (FileNotFoundException ex) when (args.Any(a =>
                   string.Equals(a, "--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            // 用户数据可能已被提前删除。无配置时不能假设默认路径就是实际挂载点；
            // 先枚举当前用户中带有本 ProviderId 的注册，再检查默认路径。只有确认两处都没有
            // 本程序的 SyncRoot 时才把卸载当作已完成，仍发现注册则保留失败状态以免遗留它。
            var registeredRoots = SyncRootShellRegistration.GetRegisteredRootPaths();
            var defaultRootRegistered = ProviderIdentity.OwnsRoot(
                _ctx.SyncRootPath, allowLegacy: true, providerName: _ctx.ProviderName);
            if (registeredRoots.Length == 0 && !defaultRootRegistered)
            {
                Log.Information(ex, "卸载配置文件缺失，且未发现本程序注册的 SyncRoot；跳过清理和反注册");
                Log.CloseAndFlush();
                return;
            }

            var roots = registeredRoots.ToList();
            if (defaultRootRegistered && !roots.Contains(_ctx.SyncRootPath, StringComparer.OrdinalIgnoreCase))
                roots.Add(_ctx.SyncRootPath);
            throw new InvalidOperationException(
                $"卸载配置文件缺失，但仍发现本程序注册的 SyncRoot（{roots.Count} 个）。请恢复配置或重新安装后再卸载。",
                ex);
        }
        // 双模收口：解析运行模式。
        //   --quiet  : 无头模式（无 UI、无控制台、仅文件日志；无会话则直接退出）。
        //   --console: 显式分配调试控制台（控制台日志 + 控制台读验证码）。
        //   默认     : GUI 模式；若从 cmd 启动则 AttachConsole 复用父控制台（仅用于控制台日志可见），
        //              但【验证码一律走 WinUI 弹窗】（不因为附带了控制台就退回控制台输入）。
        //              仅显式 --console 才用控制台输入验证码（开发调试用）。
        // 须在 ConfigureLogger 之前确定 _hasConsole，以便日志器决定是否挂 Console sink。
        SetupConsole();
        ConfigureLogger();

        // 1a-b. --probe[=<路径>]：只探测挂载位置能否作为 SyncRoot，打印结果后退出。
        //       纯诊断：不注册、不连接、不登录、不建占位，**也不会占用单实例**
        //       （主程序正在跑时也能用它预先验一个候选目录）。
        //       用途：换挂载点之前先确认新位置靠得住（NTFS / 支持重解析点 / 可写可删 / 不与别的云同步根冲突）。
        var probeIdx = Array.FindIndex(args,
            a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)
              || a.StartsWith("--probe=", StringComparison.OrdinalIgnoreCase)
              || a.StartsWith("--probe:", StringComparison.OrdinalIgnoreCase));
        if (probeIdx >= 0)
        {
            var arg = args[probeIdx];
            var target = arg.Length > "--probe".Length
                ? arg["--probe".Length..].Trim('=', ':', '"')
                : (probeIdx + 1 < args.Length ? args[probeIdx + 1] : "");
            if (string.IsNullOrWhiteSpace(target)) target = _ctx.SyncRootPath;   // 不带参数 = 探当前挂载点

            var r = CloudFiles.SyncRootProbe.Probe(target, _ctx.SyncRootPath, _ctx.ProviderName);
            Log.Information("[PROBE] 路径={Path}", r.Path);
            Log.Information("[PROBE] 结论={Verdict}", r.Ok ? "可用" : "**不可用**");
            Log.Information("[PROBE] 摘要={Detail}", r.Describe(true));
            foreach (var f in r.Failures) Log.Information("[PROBE] ✗ {Item}", f);
            foreach (var w in r.Warnings) Log.Information("[PROBE] ! {Item}", w);
            if (r.SyncRootHitPath != null)
                Log.Information("[PROBE] 命中已注册 SyncRoot: {Hit}（Provider={Provider}）",
                    r.SyncRootHitPath, r.SyncRootHitProvider ?? "未知");
            return;
        }

        // 1b. 单实例检查（在任何耗时动作之前）：
        //     两个实例同时 Connect 同一个 SyncRoot 会撞 0x17A（ERROR_CLOUD_FILE_ALREADY_CONNECTED），
        //     后启动的那个如果不被拦住，还会走"反注册→清空→重注册"破坏前者的水化状态。
        //     这里用命名互斥体在会话内做权威判定（比遍历进程名更可靠、无竞态），
        //     已有实例则弹窗提示并取消本次启动。
        _accountSwitchRestart = args.Any(a => string.Equals(a, "--account-switch-restart", StringComparison.OrdinalIgnoreCase));
        if (!EnsureSingleInstance(_openFolder, _accountSwitchRestart))
            return;

        _ = InstanceControl.ListenAsync(RequestShutdownFromUi, _cts.Token);

        Log.Information("{ProductName} 启动 - SyncRoot={Path} 模式={Mode}",
            ProductIdentity.DisplayNameZh,
            _ctx.SyncRootPath,
            _quiet ? "无头(--quiet)"
                : (_console ? "控制台(--console,验证码走控制台输入)"
                    : (_hasConsole ? "GUI(附带控制台日志,验证码走弹窗)" : "GUI(验证码走弹窗)")));
        Log.Information("启动上下文：{Context}", DescribeStartupSecurityContext());

        // 1a. --dehydrate：清除本地已下载的数据缓存，把已水化文件还原为占位。
        //     释放磁盘空间（脱水后再次打开会按需重新下载）。
        //     只处理磁盘上**确实占有空间**的文件；正在被其它进程打开的会跳过。
        //     不需要登录；但**必须先 CfConnectSyncRoot**——cldapi 只能通过已建立的
        //     connection 通道下发 CfDehydratePlaceholder 的 FSCTL，只注册不连接时
        //     调用会挂住不返回（实测日志停在"开始清除"后再无输出、文件也没释放）。
        //     建议在引擎未运行时执行，避免两个 connection 抢 hydration 回调。
        if (args.Any(a => string.Equals(a, "--dehydrate", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ProviderIdentity.OwnsRoot(_ctx.SyncRootPath, allowLegacy: true, providerName: _ctx.ProviderName))
                throw new InvalidOperationException("目标不是本程序的 SyncRoot，拒绝脱水。");
            Log.Information("收到 --dehydrate：开始清除本地数据缓存，还原为占位: {Path}", _ctx.SyncRootPath);

            var connected = false;
            try
            {
                _callbackTable = CallbackTable.Build();
                var hr = CfConnectSyncRoot(_ctx.SyncRootPath, _callbackTable, IntPtr.Zero,
                    CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey);
                if (hr.Failed)
                {
                    Log.Warning("CfConnectSyncRoot 失败: {Hr}（若该目录从未注册过 SyncRoot，请先正常运行一次本程序）", hr);
                }
                else
                {
                    connected = true;
                    Log.Information("已连接 SyncRoot，开始脱水扫描");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "连接 SyncRoot 异常（脱水可能失败）");
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (total, dehydrated, freedBytes, skipped) =
                    PlaceholderUpdater.DehydrateTree(_ctx.SyncRootPath, msg => Log.Information("{Msg}", msg));
                Log.Information(
                    "脱水完成：扫描 {Total} 个文件 / 成功还原 {Dehydrated} 个 / 跳过 {Skipped} 个" +
                    "（未水化或正被占用），释放 {FreedMB:F1} MB，用时 {Elapsed:F1}s",
                    total, dehydrated, skipped, freedBytes / 1024.0 / 1024.0, sw.Elapsed.TotalSeconds);
                Log.Information("提示：脱水后再次打开这些文件会按需重新下载");
            }
            finally
            {
                if (connected)
                {
                    var d = CfDisconnectSyncRoot(_connectionKey);
                    if (d.Failed) Log.Warning("CfDisconnectSyncRoot 失败: {Hr}", d);
                }
                Log.CloseAndFlush();
            }
            return;
        }

        // 1b. --unregister：彻底卸载（常驻注册的逃生开关）
        //     连接 → 清空占位内容 → 断开 → 反注册。
        //     关键顺序：清理占位必须在【连接仍建立时】做（见步骤 11 注释）——Provider 仍连接时，
        //     cldflt 才会把删除路由到栈并放行占位删除；一旦 Unregister/CfDisconnectSyncRoot，
        //     孤儿重解析点会被 cldflt 拒绝（SAFE_DELETE_FAIL_CLOSED / 0x80070194），清理失效。
        //     故先连接再清空，最后才反注册（旧版"先 Unregister 再清空"的顺序是错的）。
        if (args.Any(a => string.Equals(a, "--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ProviderIdentity.OwnsRoot(_ctx.SyncRootPath, allowLegacy: true, providerName: _ctx.ProviderName))
            {
                Log.Warning("目标未注册为本程序的 SyncRoot，跳过清理: {Path}", _ctx.SyncRootPath);
                Log.CloseAndFlush();
                return;
            }
            Log.Information("收到 --unregister：开始彻底卸载 {Path}", _ctx.SyncRootPath);
            var connected = false;
            try
            {
                _callbackTable = CallbackTable.BuildForCleanup();
                var hr = CfConnectSyncRoot(_ctx.SyncRootPath, _callbackTable, IntPtr.Zero,
                    CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey);
                if (hr.Failed)
                    Log.Warning("CfConnectSyncRoot 失败: {Hr}（将尝试以孤儿占位方式清理）", hr);
                else
                {
                    connected = true;
                    Log.Information("已连接 SyncRoot，开始清空占位内容（Provider 仍连接，删除才会被 cldflt 放行）");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "连接 SyncRoot 异常（将以孤儿占位方式清理）");
            }

            try
            {
                var keepDownloadedFiles = args.Any(a => string.Equals(a, "--keep-downloaded-files", StringComparison.OrdinalIgnoreCase));
                if (CleanupSyncRootContents(_ctx.SyncRootPath, keepDownloadedFiles) != 0)
                    throw new IOException("部分占位未能清理，保留 SyncRoot 注册以便重试。");
                Log.Information("已清空占位内容");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "清空占位内容失败");
                Environment.ExitCode = 1;
            }

            if (connected)
            {
                try
                {
                    var d = CfDisconnectSyncRoot(_connectionKey);
                    if (d.Failed) Log.Warning("CfDisconnectSyncRoot 失败: {Hr}", d);
                }
                catch (Exception ex) { Log.Warning(ex, "CfDisconnectSyncRoot 异常（忽略，继续反注册）"); }
            }

            var syncRootUnregistered = false;
            if (Environment.ExitCode != 0)
            {
                Log.CloseAndFlush();
                return;
            }
            try
            {
                if (!ProviderIdentity.OwnsRoot(_ctx.SyncRootPath, allowLegacy: true, providerName: _ctx.ProviderName))
                {
                    Log.Information("SyncRoot 已不存在或不再由本程序持有，跳过反注册");
                }
                else
                {
                    SyncRootRegistrar.Unregister(_ctx.SyncRootPath, _ctx.ProviderName);
                    syncRootUnregistered = true;
                    Log.Information("已反注册 SyncRoot");
                }
            }
            catch (Exception ex)
            {
                // 反注册期间状态可能已被其他清理流程移除。只有路径仍明确由本程序持有时，
                // 反注册失败才阻止卸载；未注册或已被移除均视为清理目标已不存在。
                if (!ProviderIdentity.OwnsRoot(_ctx.SyncRootPath, allowLegacy: true, providerName: _ctx.ProviderName))
                {
                    syncRootUnregistered = true;
                    Log.Information(ex, "反注册时 SyncRoot 已不存在或不再由本程序持有，按已清理处理");
                }
                else
                {
                    Log.Warning(ex, "反注册失败，SyncRoot 仍由本程序持有");
                    Environment.ExitCode = 1;
                }
            }
            if (syncRootUnregistered && Environment.ExitCode == 0)
                RemoveEmptySyncRootDirectory(_ctx.SyncRootPath);
            Log.Information("卸载完成");
            Log.CloseAndFlush();
            return;
        }

        // cfapi 回调在线程池线程上触发，而我们的回调实现是**阻塞式同步**的
        // （列目录要 .GetAwaiter().GetResult()，下载整文件更是几十秒）。
        // Explorer 的重复枚举会在瞬间打出成百上千个回调，默认线程池每秒只注入 1~2 个线程，
        // 结果就是 FETCH_DATA 排在枚举回调后面迟迟得不到线程 → 双击后长时间无响应。
        // 这里抬高最小线程数，让突发回调立刻有线程可用。
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        if (minWorker < 64)
        {
            ThreadPool.SetMinThreads(64, Math.Max(minIo, 64));
            Log.Information("线程池最小线程数 {Old} → 64（避免回调突发时线程注入延迟）", minWorker);
        }

        // 1c. 初始化 WinUI UI 线程上的系统托盘。
        //     须在登录之前启动：纯 GUI 模式下验证码走 WinUI 弹窗，需要 DispatcherQueue 就绪；
        //     即便 --quiet 无头模式也启动（仅建立关机监听窗口，不显示托盘图标/不弹验证码）。
        TrayHost.Start(!_quiet, _ctx.TrayUpdateHintEnabled);
        TrayHost.WaitForUiReady();
        if (!_quiet)
        {
            UpdateService.Start(_ctx.AutomaticUpdateCheck, _cts.Token);
            if (_showAbout) WinUiUiHost.ShowAbout();
        }

        // 2. 初始化 HTTP 客户端
        _client = new YuWangClient(_ctx.Server, _ctx.Username, _ctx.Password,
            msg => Log.Information("{Msg}", msg),
            _ctx.HttpRequestTimeoutSeconds, _ctx.DownloadTimeoutMinutes, _cts.Token);
        Callbacks.Client = _client;
        Callbacks.Log = msg => Log.Information("{Msg}", msg);

        Log.Information("超时设置: 轻量请求 {Req}s / 单文件下载 {Dl}min（直链 Range 分片，无本地下载缓存）",
            _ctx.HttpRequestTimeoutSeconds, _ctx.DownloadTimeoutMinutes);

        // 3. 初始化缓存
        _cache = OpenCacheWithFallback(_ctx.DatabasePath, out var actualDatabasePath);
        Log.Information("SQLite 元数据缓存路径: {DatabasePath}", actualDatabasePath);
        // 启动时清空列举缓存：保证占位文件与缓存状态一致
        // （上次退出可能清了占位但缓存还在，否则会命中缓存跳过创建 → 目录为空）
        Callbacks.Cache = _cache;
        Callbacks.ContainerTypeId = _ctx.RootContainerTypeId;
        Callbacks.UserKey = CurrentUserKey();
        // 统一设置 SyncRootPath（关键修复）：
        // SyncRootPath 仅在 SyncRootRegistrar.Register 内设置（CloudFilesBridge.cs line 67），
        // 但常驻复用路径（reused=true）跳过 Register → SyncRootPath 仍是默认 Environment.CurrentDirectory
        // （bin\Release\...\win-x64 目录）→ 浏览期祖先链 break 条件 anc.FullName==rootPath 永不成立
        // （localDir 是 D:\YuWang\...，rootPath 是 bin 目录）→ 一路上浮到盘符根 D:\
        // → CfUpdatePlaceholder 报 ERROR_NOT_A_CLOUD_FILE + Path must not be a drive（日志 line 137-139）。
        // 在此统一设置，reused 与全新路径都覆盖，且在任何回调建立之前。
        Callbacks.SyncRootPath = Path.GetFullPath(_ctx.SyncRootPath);

        // 3b. 应用占位策略（目录填充完成后是否关闭其按需填充）
        Callbacks.DisablePopulationAfterPopulate = true;
        Log.Information("填充后关闭按需填充: true（固定开启）");

        // 3c. 应用统一目录缓存 / 自动刷新周期（IsFolderCached 的过期时间）。
        //     **无效值重置**：≤0（含 0 / 负数 / 缺失导致的 0）或 >120 分钟 → 重置为 30 分钟并告警。
        //     保证既不出现"永久不过期"看不到云端变更，也不出现"超长有效期"长期停留在旧内容。
        _ctx.FolderCacheMinutes = NormalizeCacheMinutes(_ctx.FolderCacheMinutes,
            nameof(SyncRootContext.FolderCacheMinutes));
        Callbacks.FolderCache = TimeSpan.FromMinutes(_ctx.FolderCacheMinutes);
        Log.Information("目录缓存与自动刷新周期: {Minutes}分钟（合法区间 {Min}~{Max}，无效值已重置为 {Default}）",
            _ctx.FolderCacheMinutes,
            MinCacheMinutes, MaxCacheMinutes, DefaultCacheMinutes);

        // 4. 启动期：登录育网云
        //    优先复用上次会话（cookies.json 里的 JSESSIONID），失效才走人工验证码。
        var captchaPath = AppPaths.ResolveDataPath("captcha.png");
        var legacyCookiesPath = AppPaths.MigrateDataFile("cookies.json", AppContext.BaseDirectory);
        var cookiesPath = AccountSessionPath(_ctx.Username);
        if (!File.Exists(cookiesPath) && File.Exists(legacyCookiesPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cookiesPath)!);
            File.Move(legacyCookiesPath, cookiesPath, overwrite: false);
            Log.Information("已把旧会话凭据迁移为当前账户独立会话文件");
        }
        else if (File.Exists(cookiesPath) && File.Exists(legacyCookiesPath))
        {
            File.Delete(legacyCookiesPath);
        }
        Log.Information("正在登录育网云 {Server}", _ctx.Server);

        var ok = false;
        var sid = Net.YuWangClient.LoadSessionFrom(cookiesPath);
        if (!string.IsNullOrEmpty(sid))
        {
            _client.SetSession(sid);
            if (await _client.CheckSessionAsync(_ctx.RootContainerTypeId))
            {
                ok = true;
                Log.Information("已复用上次会话，跳过验证码");
                await _client.InitializeWorkspaceAsync();
            }
            else
            {
                Log.Warning("上次会话已失效，改为人工输入验证码");
            }
        }

        if (!ok)
        {
            // --quiet 无头模式无法输入验证码：不能静默卡死，直接退出并提示。
            // 自动化/无头运行应预先用普通模式登录一次（cookies.json 已落盘），或保证会话仍有效。
            if (_quiet)
            {
                Log.Error("--quiet 模式不进行交互登录（无法输入验证码）。请先用普通模式登录一次（会话存入 cookies.json），" +
                          "或确保 cookies.json 中会话仍有效后再以 --quiet 运行");
                return;
            }
            // 验证码读取方式：仅显式 --console 才用控制台输入（开发调试）；
            // GUI 模式（即使从 cmd 启动附带了控制台，仅用于日志）一律走 WinUI 弹窗输入。
            // 这样双击运行与"从终端启动看日志"两种场景都能正常弹出验证码窗口。
            Func<string> readCode = _console
                ? () => Task.Run(ReadCaptchaCodeFromConsole).WaitAsync(_cts.Token).GetAwaiter().GetResult()
                : () =>
                {
                    try
                    {
                        return TrayHost.PromptCaptchaAsync(captchaPath).WaitAsync(_cts.Token)
                            .GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        Log.Information("用户取消验证码输入，本次启动已取消");
                        _cts.Cancel();
                        throw new OperationCanceledException(_cts.Token);
                    }
                };
            // 手输易错：最多重试 3 次，每次 LoginInteractivelyAsync 重新拉验证码（新 session/新图），
            // 避免手抖一次即崩启动。
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts && !ok; attempt++)
            {
                Log.Information("尝试交互登录（第 {Attempt}/{Max} 次）", attempt, maxAttempts);
                try
                {
                    ok = await _client.LoginInteractivelyAsync(captchaPath, readCode);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // 验证码弹窗失败（窗口创建/显示异常）等会一路冒泡冲垮 Main 而无日志，
                    // 这里先截住并记录，便于定位（不再静默崩溃）。
                    Log.Error(ex, "交互登录异常（含验证码弹窗失败），第 {Attempt} 次", attempt);
                    YuWangCloudSync.Program.WriteCrashFile(ex, $"LoginInteractivelyAsync(attempt{attempt})");
                    break;
                }
            }
            if (!ok)
            {
                ReportStartupFailure("登录失败（多次验证码错误或网络问题）。");
                return;
            }
        }
        _cts.Token.ThrowIfCancellationRequested();
        _client.SaveSessionTo(cookiesPath);
        Log.Information("登录成功");

        // 5. 创建 SyncRoot 目录
        Directory.CreateDirectory(_ctx.SyncRootPath);
        // 确保目录为空（CfRegisterSyncRoot 要求）
        // 若目录非空会返回错误，需要先清理或换路径

        // 5b. 清理历史 SyncRoot（换过路径后旧条目会残留在 Explorer 导航窗格）
        foreach (var legacy in _ctx.LegacySyncRootPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(legacy)) continue;
            if (!ProviderIdentity.OwnsRoot(legacy, allowLegacy: true, providerName: _ctx.ProviderName)) continue;
            if (string.Equals(Path.GetFullPath(legacy).TrimEnd('\\'),
                    Path.GetFullPath(_ctx.SyncRootPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue;   // 与当前路径相同，跳过
            // 先清内容再反注册：占位带 ReadOnly 且可能被 cldapi.sys 锁定，
            // 反注册后更容易删掉（顺序与正常退出时一致）
            if (Directory.Exists(legacy))
            {
                Log.Information("清理历史 SyncRoot 内容: {Path}", legacy);
                CleanupSyncRootContents(legacy);
            }
            try
            {
                SyncRootRegistrar.Unregister(legacy, _ctx.ProviderName);
                Log.Information("已反注册历史 SyncRoot: {Path}", legacy);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "反注册历史 SyncRoot 失败（可能本来就没注册）: {Path}", legacy);
            }
        }

        // 6. 注册 SyncRoot（常驻模式优先复用上次注册，保留占位与水化状态）
        //
        // 常驻（PersistSyncRoot=true，默认）：若目录已有内容或仍有本程序注册，说明上次注册过 → 直接
        //   CfConnectSyncRoot 复用，跳过"反注册→清空→重注册→列根建占位"。
        //   收益：跨会话保留已水化文件（再次打开瞬时，不必再受 3.5MB/s 带宽限制）。
        //   连接失败不代表未注册：开机时 CFAPI 可能尚未就绪。对非空/已注册根只重试并安全退出，
        //   绝不据此反注册或清空用户数据；仅当目录为空且没有本程序注册时才走全新注册流程。
        var reused = false;
        var syncRootHasContent = DirectoryHasContent(_ctx.SyncRootPath);
        var syncRootIsRegisteredToUs = _ctx.PersistSyncRoot
            && ProviderIdentity.OwnsRoot(_ctx.SyncRootPath, allowLegacy: true, providerName: _ctx.ProviderName);
        if (_ctx.PersistSyncRoot && (syncRootHasContent || syncRootIsRegisteredToUs))
        {
            // 这是当前已注册的 SyncRoot；探测应跳过向其中写临时文件的自测。
            var rootProbe = SyncRootProbe.Probe(_ctx.SyncRootPath, _ctx.SyncRootPath, _ctx.ProviderName);
            if (!rootProbe.Ok) throw new InvalidOperationException(rootProbe.Describe(true));
            if (rootProbe.IsSelfRegistration)
            {
                var registeredProviderName = rootProbe.SyncRootHitProvider;
                if (!string.IsNullOrWhiteSpace(registeredProviderName))
                {
                    var configuredProviderName = _ctx.ProviderName;
                    _ctx.ProviderName = registeredProviderName!;
                    try { SaveConfig(); }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "保存继承的 SyncRoot Provider 名称失败，将在本次运行中继续使用：{Name}", registeredProviderName);
                    }

                    if (!string.Equals(configuredProviderName, registeredProviderName, StringComparison.Ordinal))
                    {
                        try
                        {
                            // 已注册根的显示名属于可自定义设置，接管时沿用系统记录的原值。
                            // 占位和水化内容保持原样。
                            SyncRootRegistrar.Register(_ctx.SyncRootPath, _ctx.ProviderName,
                                _ctx.RootContainerTypeId, update: true);
                        }
                        catch (Exception ex)
                        {
                            // 元数据更新失败不应阻止对同一稳定身份的根目录继续连接；后续启动仍可重试。
                            Log.Warning(ex, "更新现有 SyncRoot Provider 元数据失败，将继续连接：{OldName} → {NewName}",
                                configuredProviderName, _ctx.ProviderName);
                        }
                    }

                    Log.Information("已继承现有 SyncRoot Provider 名称：{Name}", _ctx.ProviderName);
                }
            }
            // 已有注册的策略也需要随应用升级刷新（例如渐进水化和自动脱水策略）。
            // 更新失败不应让常驻 Provider 无法启动；旧策略会保留，并记录原因供诊断。
            if (syncRootIsRegisteredToUs)
            {
                try
                {
                    if (SyncRootShellRegistration.NeedsHydrationPolicyRefresh(_ctx.SyncRootPath))
                    {
                        SyncRootRegistrar.Register(_ctx.SyncRootPath, _ctx.ProviderName,
                            _ctx.RootContainerTypeId, update: true);
                        Log.Information("已更新现有 SyncRoot 注册策略");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "更新现有 SyncRoot 注册策略失败，将继续连接；系统仍可能使用旧策略");
                }
            }

            HRESULT tryHr = default;
            const int connectAttempts = 8;
            for (var attempt = 1; attempt <= connectAttempts; attempt++)
            {
                _callbackTable = CallbackTable.Build();
                tryHr = CfConnectSyncRoot(_ctx.SyncRootPath, _callbackTable,
                    IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey);
                if (!tryHr.Failed || (uint)tryHr.Code == 0x17Au) break;

                if (attempt < connectAttempts)
                {
                    // 登录触发的计划任务可能早于 Cloud Files provider 完成初始化。
                    // E_ACCESSDENIED 用有界退避覆盖这段启动窗口；其它错误保持短重试。
                    var errorCode = (uint)tryHr.Code;
                    var isAccessDenied = errorCode is 0x5 or 0x80070005;
                    var delay = isAccessDenied
                        ? TimeSpan.FromSeconds(Math.Min(5, Math.Pow(2, attempt - 1)))
                        : TimeSpan.FromMilliseconds(500);
                    Log.Warning("连接已有 SyncRoot 失败（第 {Attempt}/{MaxAttempts} 次，0x{Hr:X}），等待 {DelayMs}ms 后重试",
                        attempt, connectAttempts, tryHr.Code, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }

            if (!tryHr.Failed)
            {
                reused = true;
                Log.Information("复用已有 SyncRoot 注册与占位（**水化状态跨会话保留**）: {Path}", _ctx.SyncRootPath);
                // 常驻复用也要刷新磁盘树并对账顶层占位：
                // 服务器端磁盘树可能变化（改名/增删容器），旧顶层目录（含旧版数字 id 命名残留）
                // 需清理、缺失的需补建。此刻 Provider 已连接，占位删除可被 cldflt 放行。
                try { await RefreshDiskTreeAndReconcileAsync(); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "常驻模式磁盘树刷新/顶层对账失败（忽略，回调枚举时沿用 DB 缓存的磁盘树）");
                }
            }
            else
            {
                // 0x17A ERROR_CLOUD_FILE_ALREADY_CONNECTED：另一个实例已作为 provider 连接此 SyncRoot。
                // 此时绝不能走"反注册→清空→重注册"——那会销毁另一实例的水化状态、且重注册后仍连不上
                // （连接被对方占着），还会因 Unregister 在前 → 占位目录变孤儿 → 删不掉（实测数百个
                // "删除占位目录失败"）。直接告警退出，让用户先结束另一实例。
                if ((uint)tryHr.Code == 0x17Au)
                {
                    ReportStartupFailure($"SyncRoot 已被另一个 {ProductIdentity.DisplayNameZh} 实例连接（0x17A）。"
                                         + "请检查托盘或任务管理器中的已有实例；本次启动已取消，未清理占位。");
                    return;
                }

                if (syncRootHasContent || syncRootIsRegisteredToUs)
                {
                    ReportStartupFailure(
                        $"检测到已有 SyncRoot 内容或注册，但连接连续失败（0x{tryHr.Code:X}，{tryHr}）。\r\n"
                        + "为保护已有占位和已下载文件，本次启动已停止，未反注册、清空或重注册。\r\n"
                        + "请稍后重试并检查 Windows Cloud Files 服务状态。\r\n"
                        + DescribeStartupSecurityContext());
                    _callbackTable = null;
                    return;
                }

                Log.Information("当前 SyncRoot 为空且无本程序注册（0x{Hr:X}），走全新注册流程", tryHr.Code);
                _callbackTable = null;
            }
        }

        if (!reused)
        {
            // 防御性反注册：上一次若被强杀（任务管理器/IDE 终止），SyncRoot 注册项会残留，
            //  cldflt 仍认得上次注册 → 直接 Register 可能失败或重复条目。先反注册一次（忽略失败）。
            // 注意顺序：必须先 Unregister（与 Provider 解绑），孤儿重解析点才可能被
            // FSCTL_DELETE_REPARSE_POINT 剥离；若先清内容再反注册，未连接 Provider 时删除会被拒。
            try
            {
                SyncRootRegistrar.Unregister(_ctx.SyncRootPath, _ctx.ProviderName);
                Log.Information("已清理上次残留的 SyncRoot 注册: {Path}", _ctx.SyncRootPath);
            }
            catch { /* 本来就没注册时抛错，忽略 */ }

            if (Directory.Exists(_ctx.SyncRootPath))
            {
                try { CleanupSyncRootContents(_ctx.SyncRootPath); }
                catch (Exception ex) { Log.Warning(ex, "清理当前 SyncRoot 旧内容失败（忽略，继续注册）: {Path}", _ctx.SyncRootPath); }
            }
            try
            {
                SyncRootRegistrar.Register(_ctx.SyncRootPath, _ctx.ProviderName,
                    _ctx.RootContainerTypeId);
                Log.Information("SyncRoot 注册成功");
            }
            catch (Exception ex)
            {
                ReportStartupFailure("SyncRoot 注册失败；启动已停止，未继续创建占位。", ex);
                return;
            }

            // 7. 拉取磁盘树并创建初始占位（多盘映射：三组容器为顶层，其下挂真实磁盘）
            //    此路径目录刚被清空重注册，对账的删除段无事可做，仅建顶层占位。
            try
            {
                await RefreshDiskTreeAndReconcileAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "磁盘树拉取/根目录占位创建失败");
                return;
            }
        }

        // 8. 连接 SyncRoot，进入回调循环
        //    回调表在此处传入（不是注册时）。CfConnectSyncRoot 建立双向通信通道，
        //    之后系统才会把 FETCH_DATA / FETCH_PLACEHOLDERS 等回调分发给我们的方法。
        //    常驻模式下第 6 步已连上，这里跳过重复连接。
        try
        {
            if (!reused)
            {
                _callbackTable = CallbackTable.Build();
                var hresult = CfConnectSyncRoot(_ctx.SyncRootPath, _callbackTable,
                    IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey);
                if (hresult.Failed)
                {
                    if ((uint)hresult.Code == 0x17Au)
                        Log.Error($"SyncRoot 已被另一个 {ProductIdentity.DisplayNameZh} 实例连接（0x17A）。"
                                  + $"请先在任务管理器结束所有 {ProductIdentity.ExecutableFileName} 进程再启动。本进程退出，不清理占位。");
                    else
                        Log.Error("CfConnectSyncRoot 失败: 0x{Hresult:X} ({Message})", hresult.Code, hresult.ToString());
                    return;
                }
            }
            Log.Information("SyncRoot 已连接，回调通道已建立");
            // 系统托盘 / UI 线程已在启动早期（登录前）启动，此处无需再次启动。
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CfConnectSyncRoot 异常");
            return;
        }

        // 8b. 监视 Explorer 固定/释放意图，并启动目录时间修正线程。
        // Cloud Files 不提供独立 pin-state callback；平台通过文件属性变化表达菜单操作。
        StartPinStateWatcher();
        PlaceholderFixer.Start(msg => Log.Information("{Msg}", msg));

        if (_openFolder)
            OpenSyncFolder();

        // 8b-2. 目录时间对账：改为后台预拉取完成后触发（开机启动场景）。
        //       之前立即跑对账时 files/folders 表空（浏览前）→ derived 全 null → "刷新 0 个"。
        //       现在后台预拉取全部磁盘的目录结构入库（folders.local_path + files），
        //       完成后 ReconcileDirectoryTimes 才有效（对账时 DB 有数据）。
        //       不阻塞主循环：用户可立即用 Explorer 浏览（回调照常，浏览期 self Force 仍工作）。
        _ = Task.Run(() => PreFetchAllDisksAsync(_cts.Token));

        // 9. 后台线程：session 续期检测
        _ = Task.Run(() => SessionWatcherAsync(_cts.Token));

        // 10. 主线程消息循环（保持进程存活）
        Log.Information("{ProductName} 运行中，按 Ctrl+C 退出", ProductIdentity.DisplayNameZh);

        // 注册控制台控制处理器：捕获 Ctrl+C / Ctrl+Break / 关闭窗口 / 注销 / 关机，
        // 全部引到 RequestShutdown → 与 Ctrl+C 完全一致的优雅退出流程。
        // 注意：委托必须保持引用（_consoleCtrl 字段），否则 GC 回收后回调会崩溃。
        _consoleCtrl = OnConsoleCtrl;
        try
        {
            if (!SetConsoleCtrlHandler(_consoleCtrl, true))
                Log.Warning("注册控制台控制处理器失败（关闭窗口可能无法优雅退出）: 0x{Err:X}",
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            else
                Log.Information("已注册控制台控制处理器（Ctrl+C/Break/关闭窗口/注销/关机 → 优雅退出）");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "注册控制台控制处理器异常（忽略，仅 Ctrl+C 可用）");
        }
        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) { }

        // 11. 清理与反注册
        //     正确顺序：先停后台修正线程 → 清理占位内容（Provider 仍连接）→ 断开回调通道
        //     → 释放占位数组 → 反注册 SyncRoot。
        //     关键：清理占位必须在【断开连接之前】做！Provider 仍连接时，cldflt 才会把
        //     删除路由到栈并放行占位删除；一旦 CfDisconnectSyncRoot，孤儿重解析点会被
        //     cldflt 拒绝（SAFE_DELETE_FAIL_CLOSED / 0x80070194），清理失效。
        //     即便如此，DeletePlaceholderSafe 仍带 FSCTL_DELETE_REPARSE_POINT 兜底。

        // 11a. 先停属性监视器和后台目录时间线程，避免清理/断开时继续发起 CFAPI 操作。
        StopPinStateWatcher();
        PlaceholderFixer.Stop();

        // 11b. 清理占位内容（可选，仅在 Provider 仍连接时执行才有效）
        //   按目录权限创建的文件占位可能带 ReadOnly 属性；真实数据在育网云服务器，删除占位安全。
        //   只删 SyncRoot 目录**内的内容**，目录本身保留。
        //   常驻模式下**一律不清理**：占位与水化状态要留给下次启动复用。
        if (_pendingAccountSwitch is { } accountSwitch)
        {
            Log.Information("账户切换：清除旧账户同步目录中的所有文件与占位");
            Callbacks.SuppressDeleteRecovery = true;
            var failures = CleanupSyncRootContents(_ctx.SyncRootPath, deleteForeign: true);
            try
            {
                if (failures == 0)
                {
                    _cache?.ClearContentCache();
                    _ctx = accountSwitch;
                    SaveConfig();
                    _restartAfterAccountSwitch = true;
                    Log.Information("旧账户目录和索引已清除，已保存新账户配置");
                }
                else
                {
                    Log.Error("账户切换清理失败 {Failures} 项；没有写入新账户配置，也不会自动重启", failures);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "账户切换清理或新账户配置保存失败；没有自动重启");
                _restartAfterAccountSwitch = false;
            }
        }
        else if (_ctx.PersistSyncRoot)
        {
            Log.Information("常驻模式：保留 SyncRoot 注册与占位内容（水化状态跨会话保留）");
        }
        else
        {
            Log.Information("正在清理 SyncRoot 占位内容（Provider 仍连接）...");
            CleanupSyncRootContents(_ctx.SyncRootPath);
        }

        // 11c. 断开回调通道
        Log.Information("正在断开 SyncRoot...");
        try
        {
            var disconnect = CfDisconnectSyncRoot(_connectionKey);
            if (disconnect.Failed)
                Log.Warning("CfDisconnectSyncRoot 失败: 0x{Hresult:X}（可能未连接或已断开）", disconnect.Code);
            else
                Log.Information("回调通道已断开");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CfDisconnectSyncRoot 异常（忽略，继续反注册）");
        }

        // 11d. 断开后再释放按目录缓存的占位数组非托管内存并统计
        CloudFiles.Callbacks.ReleasePlaceholderArrays();
        CloudFiles.Callbacks.LogCallbackStats();

        if (_ctx.PersistSyncRoot)
        {
            Log.Information("常驻模式：保留 SyncRoot 注册（下次启动直接复用；彻底卸载请用 --unregister）");
        }
        else
        {
            try
            {
                SyncRootRegistrar.Unregister(_ctx.SyncRootPath, _ctx.ProviderName);
                Log.Information("SyncRoot 反注册完成");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SyncRoot 反注册失败（可能已反注册或目录状态异常）");
            }
        }

        // 11e. 退出托盘：在 UI 线程释放图标并结束 XAML 循环，再等待其线程结束，保证干净退出。
        TrayHost.Shutdown();
        TrayHost.Join(TimeSpan.FromSeconds(3));

        _client?.Dispose();
        _cache?.Dispose();
        _callbackTable = null;
        Log.Information("已退出（日志: {LogPath}）", _currentLogPath);
        Log.CloseAndFlush();
        if (_restartAfterAccountSwitch)
        {
            try
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法解析应用程序路径，无法重启新账户。");
                var restart = new ProcessStartInfo(executable) { UseShellExecute = true };
                restart.ArgumentList.Add("--account-switch-restart");
                Process.Start(restart);
            }
            catch (Exception ex)
            {
                try { Log.Error(ex, "启动新账户进程失败，请手动重新启动应用"); } catch { }
            }
        }
        // 通知可能在等待的控制台控制处理器（关闭/注销/关机事件）清理已完成，可立即返回
        _exitDone.Set();
    }

    /// <summary>
    /// 递归删除 SyncRoot 目录内的占位内容（不动目录本身）。
    /// 关键：只读目录中的文件占位带 ReadOnly 属性，必须先清除属性才能删除；
    ///      删除应在 Provider 仍连接时执行（cldflt 才放行）；若孤儿重解析点删除被拒，
    ///      由 DeletePlaceholderSafe 经 FSCTL_DELETE_REPARSE_POINT 剥离兜底。
    /// </summary>
    private static int CleanupSyncRootContents(string dir, bool keepDownloadedFiles = false, bool deleteForeign = false)
    {
        if (!Directory.Exists(dir)) return 0;

        // 安全闸：SyncRootPath 改成 D 盘目录后，误删的代价变大。
        // 盘符根目录 / 与下载缓存目录同路径 → 拒绝清理，避免误伤真实数据。
        var full = Path.GetFullPath(dir);
        if (Path.GetPathRoot(full) == full)
        {
            Log.Warning("拒绝清理盘符根目录: {Dir}", full);
            return 1;
        }

        var deletedFiles = 0;
        var deletedDirs = 0;
        var failed = 0;
        var skippedForeign = 0;
        var keptDownloaded = 0;

        // 先删文件
        string[] filesInDir;
        try
        {
            filesInDir = Directory.GetFiles(dir);
        }
        catch (Exception ex)
        {
            failed++;
            Log.Warning(ex, "无法枚举文件: {Dir}", dir);
            filesInDir = [];
        }

        foreach (var file in filesInDir)
        {
            try
            {
                if (string.Equals(Path.GetFileName(file), ".yw-setup-probe", StringComparison.OrdinalIgnoreCase))
                {
                    var probeAttributes = File.GetAttributes(file);
                    if ((probeAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                        && new FileInfo(file).Length == 0)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        if (File.Exists(file))
                        {
                            failed++;
                            Log.Warning("无法删除遗留的安装探测文件: {File}", file);
                        }
                        else
                        {
                            deletedFiles++;
                            Log.Information("已删除遗留的安装探测文件: {File}", file);
                        }
                        continue;
                    }
                }

                var ownPlaceholder = ProviderIdentity.OwnsPlaceholder(file);
                if (!ownPlaceholder && !deleteForeign)
                {
                    skippedForeign++;
                    Log.Debug("保留用户文件或其他 Provider 的文件: {File}", file);
                    continue;
                }
                if (keepDownloadedFiles)
                {
                    var attributes = File.GetAttributes(file);
                    const FileAttributes incompleteContent = (FileAttributes)0x00441000; // OFFLINE | RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS
                    if ((attributes & incompleteContent) == 0)
                    {
                        if (!PlaceholderUpdater.RevertFile(file, message => Log.Debug("{Message}", message)))
                        {
                            failed++;
                            continue;
                        }
                        keptDownloaded++;
                        Log.Debug("保留已下载文件并移除占位状态: {File}", file);
                        continue;
                    }
                }
                // 清 ReadOnly / Hidden / System 等属性，否则删除会失败
                File.SetAttributes(file, FileAttributes.Normal);
                var deleted = ownPlaceholder
                    ? PlaceholderUpdater.DeletePlaceholderSafe(file, false, s => Log.Debug(s))
                    : DeletePlainFile(file);
                if (deleted)
                    deletedFiles++;
                else
                {
                    failed++;
                    Log.Warning("删除占位文件失败: {File}", file);
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning(ex, "删除占位文件异常: {File}", file);
            }
        }

        // 再递归删子目录
        string[] subs;
        try
        {
            subs = Directory.GetDirectories(dir);
        }
        catch (Exception ex)
        {
            // 目录占位处于 cfapi 特殊状态时，连枚举都会失败：
            // IOException "执行的名称请求无效。此时无法检索请求的名称。"
            // 此时直接整目录递归删一次（这类目录通常没有已水化的内容）
            failed++;
            Log.Warning(ex, "无法枚举子目录: {Dir}", dir);
            subs = [];
        }

        foreach (var sub in subs)
        {
            try
            {
                var attributes = File.GetAttributes(sub);
                var ownPlaceholder = ProviderIdentity.OwnsPlaceholder(sub);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !ownPlaceholder)
                {
                    if (deleteForeign)
                    {
                        Directory.Delete(sub, recursive: false);
                        deletedDirs++;
                    }
                    continue;
                }
                failed += CleanupSyncRootContents(sub, keepDownloadedFiles, deleteForeign);
                if (!ownPlaceholder && !deleteForeign) continue;
                if (Directory.EnumerateFileSystemEntries(sub).Any())
                {
                    if (deleteForeign)
                    {
                        failed++;
                        Log.Warning("账户切换时目录仍含未能删除的项目: {Dir}", sub);
                    }
                    else if (!PlaceholderUpdater.RevertDirectory(sub, message => Log.Debug(message))) failed++;
                    continue;
                }
                var deleted = ownPlaceholder
                    ? PlaceholderUpdater.DeletePlaceholderSafe(sub, true, s => Log.Debug(s))
                    : DeletePlainDirectory(sub);
                if (deleted)
                {
                    deletedDirs++;
                }
                else
                {
                    failed++;
                    Log.Warning("删除占位目录失败: {Dir}", sub);
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning(ex, "删除占位目录异常: {Dir}", sub);
            }
        }

        Log.Information("占位清理完成：删除 {Files} 个文件 / {Dirs} 个目录，保留已下载文件 {Kept} 个，失败 {Failed} 项，跳过非占位 {Skipped} 项",
            deletedFiles, deletedDirs, keptDownloaded, failed, skippedForeign);
        return failed;
    }

    private static bool DeletePlainFile(string path)
    {
        File.Delete(path);
        return !File.Exists(path);
    }

    private static bool DeletePlainDirectory(string path)
    {
        Directory.Delete(path, recursive: false);
        return !Directory.Exists(path);
    }

    /// <summary>Unregister leaves retained/user files untouched; remove the root only when it is empty.</summary>
    private static void RemoveEmptySyncRootDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Path.GetPathRoot(full) == full)
            {
                Log.Warning("拒绝删除磁盘根目录作为 SyncRoot: {Path}", full);
                return;
            }
            if (!Directory.Exists(full)) return;

            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Log.Warning("SyncRoot 本身是重解析目录，保留目录: {Path}", full);
                return;
            }
            if (Directory.EnumerateFileSystemEntries(full).Any())
            {
                Log.Information("SyncRoot 仍含保留内容，保留目录: {Path}", full);
                return;
            }

            Directory.Delete(full, recursive: false);
            Log.Information("已删除空 SyncRoot 目录: {Path}", full);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除空 SyncRoot 目录失败，保留目录: {Path}", path);
        }
    }

    /// <summary>目录是否存在且非空（用于判断"上次注册过、占位还在"）</summary>
    private static bool DirectoryHasContent(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string AccountSessionPath(string username)
    {
        var accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant())))[..20];
        return AppPaths.ResolveDataPath($"cookies-{accountHash}.json");
    }

    internal static void SaveAccountSession(string username, Net.YuWangClient client)
    {
        if (string.IsNullOrWhiteSpace(client.CurrentSessionId))
            throw new InvalidOperationException("登录成功，但服务器没有返回会话。");

        var path = AccountSessionPath(username);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        client.SaveSessionTo(path);
        if (!string.Equals(Net.YuWangClient.LoadSessionFrom(path), client.CurrentSessionId,
                StringComparison.Ordinal))
            throw new IOException("登录成功，但账户会话未能写入本地配置目录。");
    }

    /// <summary>账号隔离键。旧版自定义 UserKey 仍作为命名空间前缀。</summary>
    private static string CurrentUserKey() =>
        string.IsNullOrWhiteSpace(_ctx.UserKey) ? _ctx.Username : $"{_ctx.UserKey}:{_ctx.Username}";

    // === 目录缓存有效期（分钟）取值约束 ===
    // 合法区间：1 ~ 120 分钟。
    // 0 / 负数 / 异常值（如超大值、缺失导致的 0）以及 **> 120** 均视为无效配置，
    // 启动时统一重置为 30 分钟并告警——避免"永久不过期"（0）看不到云端变更，
    // 也避免"超长有效期"（几百/几千分钟）长时间停留在过期目录内容上。
    private const int MinCacheMinutes = 1;
    private const int MaxCacheMinutes = 120;
    private const int DefaultCacheMinutes = 30;

    /// <summary>
    /// 校验并归一化目录缓存有效期配置（分钟）。
    /// **无效值（≤0 或 &gt;120）在启动时重置为 30 分钟**（同时写 Warning 日志），
    /// 保证 IsFolderCached 的过期时间永远落在可控区间内。
    /// </summary>
    private static int NormalizeCacheMinutes(int minutes, string configName)
    {
        if (minutes >= MinCacheMinutes && minutes <= MaxCacheMinutes) return minutes;
        Log.Warning("配置项 {Name}={Value} 无效（合法区间 {Min}~{Max} 分钟），已重置为 {Default} 分钟",
            configName, minutes, MinCacheMinutes, MaxCacheMinutes, DefaultCacheMinutes);
        return DefaultCacheMinutes;
    }

    /// <summary>
    /// 拉取账号下完整磁盘树（容器→磁盘）并入库 disk_tree。
    /// 解析失败/会话异常时回落到单盘兼容模式（仅挂载 RootDiskId），保证不彻底失联。
    /// 若解析始终为空，日志会打出 [DISKTREE] 未解析到任何子节点（不再落盘 HTML 样本，2026-10-09 起）。
    /// </summary>
    /// <summary>
    /// 拉取磁盘树入库。返回 true 表示数据库现有真实完整磁盘树（本次新拉成功，或拉取为空时沿用了 DB 缓存）；
    /// 返回 false 表示解析为空且无缓存、已回落单盘兼容节点（此时绝不触发顶层对账删除，避免误删）。
    /// </summary>
    private static async Task<bool> FetchAndStoreDiskTreeAsync()
    {
        var tree = await _client!.FetchDiskTreeAsync(_ctx.RootContainerTypeId);
        if (tree.Count == 0)
        {
            // 拉取为空（HTML 解析失败或会话异常）：DB 已有缓存树则沿用，避免网络抖动把好数据冲掉
            if (_cache!.IsDiskTreeFetched(CurrentUserKey()))
            {
                Log.Warning("磁盘树拉取为空（会话异常？），沿用数据库已缓存的磁盘树");
                return true;
            }
            Log.Warning("磁盘树为空（HTML 解析失败或会话异常，已落盘 HTML 供排查）。回落单盘兼容：RootDiskId={Disk}",
                _ctx.RootDiskId);
            tree = new List<DiskTreeNode>
            {
                new() { Id = _ctx.RootDiskId, NodeType = "disk", Name = $"{_ctx.Username}(兼容)", ParentId = _ctx.RootContainerTypeId }
            };
            _cache!.SaveDiskTree(CurrentUserKey(), tree);
            Log.Information("磁盘树已入库：{Count} 个节点（userKey={User}）", tree.Count, CurrentUserKey());
            return false;
        }
        _cache!.SaveDiskTree(CurrentUserKey(), tree);
        Log.Information("磁盘树已入库：{Count} 个节点（userKey={User}）", tree.Count, CurrentUserKey());
        return true;
    }

    /// <summary>
    /// 刷新磁盘树并对账 SyncRoot 顶层占位（常驻复用与全新注册两条路径共用）：
    /// 1) 重新拉取磁盘树入库（服务器端可能改名/增删容器）；
    /// 2) 补建缺失的顶层容器占位（CreateVirtualBatch 自带按名跳过），失败时保留旧目录；
    /// 3) 删除磁盘树顶层已不存在的旧占位目录（旧版数字 id 命名残留、服务器端改名后的旧目录）。
    /// 安全约束：
    /// - 仅在真实磁盘树可用时才删除（回落/失败时绝不删，防网络抖动误删）；
    /// - 仅删除带 ReparsePoint 的占位目录（用户自建的普通目录跳过不动）；
    /// - 删除须在 Provider 已连接时执行（cldflt 放行），失败仅告警不中断。
    /// </summary>
    private static async Task RefreshDiskTreeAndReconcileAsync()
    {
        var realTree = await FetchAndStoreDiskTreeAsync();
        var topNodes = _cache!.ListChildNodes(CurrentUserKey(), _ctx.RootContainerTypeId);

        // 虚拟节点的 DiskId/FileId 均为 0；入口或磁盘树变化后清除所有虚拟列举快照。
        Callbacks.InvalidateFolderListing(0, 0);
        // 容器改名或入口切换也会改变真实磁盘的本地路径，旧位置的创建记录不能复用。
        Callbacks.ReleasePlaceholderArrays();
        PlaceholderCreator.CreateVirtualBatch(_ctx.SyncRootPath, topNodes);

        if (realTree && topNodes.Count > 0)
        {
            var expected = topNodes.Select(n => n.LocalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(_ctx.SyncRootPath))
            {
                var name = Path.GetFileName(dir.TrimEnd('\\'));
                if (expected.Contains(name)) continue;
                try
                {
                    // 只清理本程序拥有的占位目录；普通目录和其他 Provider 的重解析点都保留。
                    var attributes = File.GetAttributes(dir);
                    if ((attributes & FileAttributes.ReparsePoint) == 0 || !ProviderIdentity.OwnsPlaceholder(dir))
                    {
                        Log.Information("跳过非本程序占位的顶层目录: {Dir}", dir);
                        continue;
                    }
                    CleanupSyncRootContents(dir);
                    if (PlaceholderUpdater.DeletePlaceholderSafe(dir, true, s => Log.Debug(s)))
                        Log.Information("已清理过期顶层占位目录: {Dir}（当前磁盘树顶层不含该名称）", dir);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "清理过期顶层占位目录失败（忽略）: {Dir}", dir);
                }
            }
        }

        ExplorerStateNotifier.NotifyDirectory(_ctx.SyncRootPath);
        Log.Information("根目录（磁盘树）占位对账完成：{Count} 个顶层容器", topNodes.Count);
    }

    /// <summary>
    /// 后台预拉取全部磁盘的目录结构入库（开机启动场景）。
    /// 递归 ListFolder 每个磁盘的每个目录，入库 folders（含 local_path）+ files。
    /// 完成后触发 ReconcileDirectoryTimes（此时 files/folders 有数据，对账有效）。
    ///
    /// **为什么需要**：之前启动对账在浏览前跑，files/folders 表空 → derived 全 null → "刷新 0 个"。
    ///   浏览期 self Force 虽能修正被浏览的目录，但未浏览目录时间仍错（开机自启后用户不浏览则全错）。
    ///   预拉取让启动对账立即有全盘数据，开机后所有目录时间一次到位。
    ///
    /// **不阻塞主循环**：后台 Task 跑，用户可立即用 Explorer 浏览（回调照常工作）。
    ///   预拉取与回调可能请求同一目录：IsFolderCached 去重（预拉取前 + 回调前都检查）避免重复 HTTP。
    /// **限流**：每目录间 100ms，避免服务器封禁（育网云限速 ~3.5MB/s，ListFolder 响应 30~80KB）。
    /// **容错**：单目录失败 log + 继续；整体异常也兜底调一次对账。
    /// </summary>
    private static async Task PreFetchAllDisksAsync(CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 从 disk_tree 递归构造每个磁盘的 localPath（SyncRoot\容器名\磁盘名）
            var diskPaths = new List<(ulong diskId, string localPath)>();
            void WalkTree(ulong parentId, string parentPath)
            {
                foreach (var n in _cache!.ListChildNodes(CurrentUserKey(), parentId))
                {
                    var p = Path.Combine(parentPath, n.LocalName);
                    if (n.IsDisk) diskPaths.Add((n.Id, p));
                    else WalkTree(n.Id, p);  // 容器递归
                }
            }
            WalkTree(_ctx.RootContainerTypeId, _ctx.SyncRootPath);
            if (diskPaths.Count == 0)
            {
                Log.Warning("[PREFETCH] disk_tree 无磁盘节点，跳过预拉取（直接对账）");
                ReconcileDirectoryTimes();
                return;
            }
            Log.Information("[PREFETCH] 开始预拉取 {N} 个磁盘的目录结构（后台填充，不阻塞浏览）", diskPaths.Count);

            var totalFolders = 0;
            var totalFiles = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var (diskId, diskLocalPath) in diskPaths)
            {
                if (ct.IsCancellationRequested) break;
                // BFS 从磁盘根 folderId=0 开始
                var queue = new Queue<(ulong folderId, ulong parentId, string localPath)>();
                queue.Enqueue((0, 0, diskLocalPath));
                while (queue.Count > 0 && !ct.IsCancellationRequested)
                {
                    var (folderId, parentId, localPath) = queue.Dequeue();
                    // 去重：已缓存且未过期（统一周期内）则跳过 HTTP，但从 DB 读子项继续 BFS
                    if (_cache!.IsFolderCached(diskId, folderId, CloudFiles.Callbacks.FolderCache))
                    {
                        skipped++;
                        foreach (var sub in _cache.ListCachedItems(diskId, folderId).Where(i => i.isFolder))
                            queue.Enqueue((sub.id, folderId, Path.Combine(localPath, sub.name)));
                        continue;
                    }
                    try
                    {
                        var listing = await _client!.ListFolderWithPermissionsAsync(diskId, folderId);
                        if (!listing.IsValidPage) throw new InvalidDataException("服务器没有返回请求的目录页面。");
                        var items = listing.Items.ToList();
                        _cache.SaveDirectoryPermission(CurrentUserKey(), diskId, folderId, listing.Permission);
                        _cache.SaveFolder(diskId, folderId, parentId,
                            Path.GetFileName(localPath.TrimEnd('\\')), localPath, items);
                        totalFolders++;
                        totalFiles += items.Count(i => !i.IsFolder);
                        // 子文件夹：SaveSubFolder（含 local_path，供下次对账反查）+ 入队继续递归
                        foreach (var sub in items.Where(i => i.IsFolder))
                        {
                            var subPath = Path.Combine(localPath, sub.Name);
                            _cache.SaveSubFolder(diskId, sub.Id, folderId, sub.Name, subPath);
                            queue.Enqueue((sub.Id, folderId, subPath));
                        }
                        // 清内存/CFAPI 快照后立即把新增项落成真实占位。仅清缓存不够：
                        // 已填充目录关闭按需填充后，Explorer 不一定再触发 FETCH_PLACEHOLDERS。
                        Callbacks.InvalidateFolderListing(diskId, folderId);
                        try
                        {
                            var cachedItems = _cache.ListCachedItems(diskId, folderId);
                            var permission = _cache.GetDirectoryPermission(CurrentUserKey(), diskId, folderId);
                            var created = PlaceholderCreator.CreateBatch(localPath, diskId, folderId, cachedItems,
                                readOnly: permission != DirectoryPermission.FullAccess);
                            if (created > 0)
                            {
                                ExplorerStateNotifier.NotifyDirectory(localPath);
                                Log.Information("[PREFETCH] 已为目录补建 {Count} 个新占位并通知 Explorer: {Path}",
                                    created, localPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[PREFETCH] 补建目录占位失败（浏览回调仍可自愈）: {Path}", localPath);
                        }
                        if (totalFolders % 20 == 0)
                            Log.Information("[PREFETCH] 进度：已拉取 {F} 目录 / {Fl} 文件，队列剩 {Q}",
                                totalFolders, totalFiles, queue.Count);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warning("[PREFETCH] 拉取失败 disk={Disk} folder={Folder}: {Msg}", diskId, folderId, ex.Message);
                    }
                    await Task.Delay(100, ct);  // 限流：每目录 100ms 间隔
                }
            }
            Log.Information("[PREFETCH] 完成：{F} 目录 / {Fl} 文件 / 跳过{S} / 失败{Fa}，用时{T:F1}s → 触发目录时间对账",
                totalFolders, totalFiles, skipped, failed, sw.Elapsed.TotalSeconds);
            ReconcileDirectoryTimes();  // 预拉取完成后对账（此时 files/folders 有数据）
        }
        catch (OperationCanceledException) { /* 退出时正常，不调对账 */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PREFETCH] 预拉取异常（不影响运行，浏览期 self Force 仍工作）；尝试用已有数据对账");
            try { ReconcileDirectoryTimes(); } catch { /* 对账失败不影响运行 */ }
        }
    }

    /// <summary>
    /// 启动期目录时间对账：遍历 SyncRoot 下全部目录，把修改时间强制刷新（Force）为 DB 推导值：
    /// - 顶层容器（disk_tree 按名匹配）：其下全部磁盘已入库文件的最大上传时间
    /// - 磁盘内真实目录（folders.local_path 反查身份）：该目录全部已入库后代文件的最大上传时间
    /// - DB 无记录（未浏览过）的目录：不动（保持占位出生时的低位哨兵，浏览后上浮收敛）
    /// 背景：服务器不提供目录时间（实证列表页对目录 0 个日期），目录占位出生盖"当时时刻"
    /// → 永远晚于内容文件时间 → 只增不减的上浮被永久压制。Force 归位后浏览期上浮语义正常收敛。
    /// 走 PlaceholderFixer 的 Force 队列（回调外线程执行 + CfUpdatePlaceholder 兜底）。
    /// </summary>
    private static void ReconcileDirectoryTimes()
    {
        try
        {
            // 前提条件诊断：一次性确认对账所需的关键值都对（"刷新 0 个"时定位根因用）
            var root = _ctx.SyncRootPath;
            var typeId = _ctx.RootContainerTypeId;
            var uk = CurrentUserKey();
            var diskTreeCount = _cache!.IsDiskTreeFetched(uk) ? _cache.ListChildNodes(uk, typeId).Count : -1;
            Log.Information("[DIRTIME-RECON] 前提: SyncRoot={Root} TypeId={TypeId} UserKey={Uk} 顶层子节点数={N}",
                root, typeId, uk, diskTreeCount);
            var count = 0;
            ReconcileDirTimeLevel(_ctx.SyncRootPath, _ctx.RootContainerTypeId, ref count);
            Log.Information("目录时间对账完成：刷新 {Count} 个目录的修改时间（依据 DB 推导值）", count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "目录时间对账失败（忽略，不影响运行）");
        }
    }

    /// <summary>目录时间对账的单层遍历：parentNodeId 为 disk_tree 当前父节点（磁盘层之下恒无节点，走 local_path 反查）</summary>
    private static void ReconcileDirTimeLevel(string dir, ulong parentNodeId, ref int count)
    {
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch (Exception ex)
        {
            // **诊断盲区修复**：之前是空 `catch { return; }`——Directory.GetDirectories 在
            // cldflt 已附加的 SyncRoot 上枚举可能抛 IOException/UnauthorizedAccessException，
            // 空 catch 会静默吞掉异常，导致对账 count=0 且**完全无日志**（"刷新 0 个"无迹可寻）。
            // 改为 Warning 暴露真因。
            Log.Warning(ex, "对账遍历目录失败（空 catch 修复）: {Dir}", dir);
            return;
        }

        var childNodes = _cache!.ListChildNodes(CurrentUserKey(), parentNodeId);
        foreach (var sub in subs)
        {
            try
            {
                var name = Path.GetFileName(sub.TrimEnd('\\'));
                DateTime? derived;
                var nextParent = parentNodeId;
                var node = childNodes.FirstOrDefault(c => string.Equals(c.LocalName, name, StringComparison.OrdinalIgnoreCase));
                if (node != null)
                {
                    derived = MaxUploadTimeOfTreeNode(node);
                    nextParent = node.Id;   // 容器下一层仍是 disk_tree 节点（磁盘）
                }
                else
                {
                    var ident = _cache.TryGetFolderByLocalPath(sub);
                    derived = ident != null
                        ? _cache.GetFolderTreeMaxUploadTime(ident.Value.diskId, ident.Value.folderId)
                        : null;
                }
                if (derived is { } t)
                {
                    PlaceholderFixer.EnqueueDirTime(sub, t.ToFileTimeUtc(), force: true);
                    count++;
                }
                else if (node == null)
                {
                    // 诊断：既不在 disk_tree 也反查不到 folders.local_path（未浏览过的目录）
                    Log.Information("[DIRTIME-RECON] 跳过（DB 无记录）: {Dir}", sub);
                }
                ReconcileDirTimeLevel(sub, nextParent, ref count);
            }
            catch (Exception ex)
            {
                // **诊断盲区修复**：之前是 Log.Debug（MinimumLevel=Information 时不可见），
                // foreach body 任何异常都被静默吞掉。改为 Warning 暴露真因。
                Log.Warning(ex, "目录时间对账跳过异常目录: {Dir}", sub);
            }
        }
    }

    /// <summary>disk_tree 节点的推导时间：磁盘 → 全盘已入库文件最大上传时间；容器 → 递归其下磁盘取最大</summary>
    private static DateTime? MaxUploadTimeOfTreeNode(DiskTreeNode node)
    {
        if (node.IsDisk) return _cache!.GetDiskMaxUploadTime(node.Id);
        DateTime? best = null;
        foreach (var child in _cache!.ListChildNodes(CurrentUserKey(), node.Id))
        {
            var t = MaxUploadTimeOfTreeNode(child);
            if (t is { } tv && (best == null || tv > best)) best = tv;
        }
        return best;
    }

    /// <summary>从控制台读验证码</summary>
    private static string ReadCaptchaCodeFromConsole()
    {
        Console.Error.Write("请看 captcha.png 输入验证码: ");
        return Console.ReadLine() ?? "";
    }

    /// <summary>后台 session 续期检测</summary>
    private static async Task SessionWatcherAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_ctx.SessionCheckIntervalMinutes), ct);
                var alive = await _client!.CheckSessionAsync(_ctx.RootContainerTypeId);
                if (!alive)
                {
                    Log.Warning("session 失效，请重新登录");
                    // MVP 不自动重登，让用户重启程序
                }
                else
                {
                    Log.Information("session 续期检查通过");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "session 续期检测异常");
            }
        }
    }

    /// <summary>
    /// 控制台控制事件处理器（由 OS 在独立线程上调用，非主线程）。
    /// 所有事件（Ctrl+C / Ctrl+Break / 关闭窗口 / 注销 / 关机）统一引到 RequestShutdown，
    /// 即与原来的 Ctrl+C 完全一致的优雅退出流程。
    ///
    /// **关闭/注销/关机事件**：系统只允许处理器在返回前运行有限时间（约 5 秒），
    /// 因此这里等待主线程完成清理（_exitDone，最多 4 秒）再返回，确保优雅反注册发生在
    /// 进程被强杀之前。Ctrl+C / Ctrl+Break 返回 true 即阻止默认终止，主线程继续跑清理。
    /// </summary>
    private static bool OnConsoleCtrl(CtrlType ctrlType)
    {
        RequestShutdown(ctrlType);
        if (ctrlType is CtrlType.CTRL_CLOSE_EVENT or CtrlType.CTRL_LOGOFF_EVENT or CtrlType.CTRL_SHUTDOWN_EVENT)
            _exitDone.Wait(TimeSpan.FromSeconds(4));
        // 返回 true = 事件已处理：
        //  - Ctrl+C/Break：阻止默认终止，主线程继续做清理后正常退出
        //  - 关闭/注销/关机：系统仍会在本函数返回后终止进程，但清理已经（或正在）完成
        return true;
    }

    /// <summary>统一退出入口（幂等）：取消主循环令牌，触发与 Ctrl+C 一致的优雅清理。</summary>
    private static void RequestShutdown(CtrlType from)
    {
        if (System.Threading.Interlocked.Exchange(ref _shutdownStarted, 1) == 1) return;
        Log.Information("收到退出信号 {Signal}，转入优雅退出流程（与 Ctrl+C 一致）", from);
        _cts.Cancel();
    }

    /// <summary>供托盘菜单"退出"项调用：引到与 Ctrl+C 完全一致的优雅退出链。</summary>
    internal static void RequestShutdownFromUi() => RequestShutdown(CtrlType.CTRL_C_EVENT);

    /// <summary>供托盘菜单"打开同步文件夹"读取同步根目录路径。</summary>
    internal static string SyncRootPath => _ctx.SyncRootPath;

    /// <summary>
    /// 按运行模式建立/附着控制台（双模收口）。
    ///   --quiet  : 不分配控制台（WinExe 本就没有控制台窗口，无需隐藏）。
    ///   --console: AllocConsole 显式创建调试控制台。
    ///   默认 GUI : 尝试 AttachConsole(-1) 复用父控制台（从 cmd 启动时才有），否则纯 GUI。
    /// 成功附着/分配后调用 <see cref="RewireStdStreams"/> 重连标准流。
    /// 结果写入 <see cref="_hasConsole"/>，供 ConfigureLogger 与登录读码判断使用。
    /// </summary>
    private static void SetupConsole()
    {
        if (_quiet)
        {
            _hasConsole = false;
            return;
        }
        if (_console)
        {
            _hasConsole = AllocConsole();
            if (_hasConsole) RewireStdStreams();
            return;
        }
        // 默认 GUI：尝试附着父控制台（从现有 cmd 启动时才有）
        _hasConsole = AttachConsole(ATTACH_PARENT_PROCESS);
        if (_hasConsole) RewireStdStreams();
    }

    /// <summary>重连标准流到控制台（AttachConsole/AllocConsole 后 std 句柄不会自动重定向）。</summary>
    private static void RewireStdStreams()
    {
        try
        {
            // Keep Win32's console code pages and .NET's text streams aligned. Otherwise
            // a CP936 console reads UTF-8 log bytes as GBK and Chinese output is garbled.
            _originalConsoleInputCodePage = GetConsoleCP();
            _originalConsoleOutputCodePage = GetConsoleOutputCP();
            _restoreConsoleInputCodePage = _originalConsoleInputCodePage != 0;
            _restoreConsoleOutputCodePage = _originalConsoleOutputCodePage != 0;
            AppDomain.CurrentDomain.ProcessExit += RestoreConsoleCodePages;
            _ = SetConsoleCP(Utf8CodePage);
            _ = SetConsoleOutputCP(Utf8CodePage);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = utf8;
            Console.OutputEncoding = utf8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
        }
        catch { /* 重连失败不影响主流程，仅控制台输出可能丢失 */ }
    }

    private static void RestoreConsoleCodePages(object? sender, EventArgs e)
    {
        // AttachConsole shares its code page with the parent shell, so don't leave that
        // shell changed after this process exits. AllocConsole is also safe to restore.
        if (_restoreConsoleInputCodePage) _ = SetConsoleCP(_originalConsoleInputCodePage);
        if (_restoreConsoleOutputCodePage) _ = SetConsoleOutputCP(_originalConsoleOutputCodePage);
    }

    /// <summary>
    /// 相对数据库路径迁移到当前用户 LocalAppData，避免任务计划的工作目录把 DB 指向 System32 等目录。
    /// 相对路径打开失败时依次尝试用户缓存和临时目录中的备用文件；原 SQLite 文件始终保留。
    /// </summary>
    private static SqliteCache OpenCacheWithFallback(string configuredPath, out string actualPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("配置项 DatabasePath 不能为空");

        var isRelative = !Path.IsPathRooted(configuredPath);
        var requestedPath = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : AppPaths.MigrateDataFile(configuredPath, AppContext.BaseDirectory);
        try
        {
            var cache = OpenCacheAtPath(requestedPath);
            actualPath = requestedPath;
            return cache;
        }
        catch (Exception ex) when (isRelative && IsRecoverableDatabaseOpenFailure(ex))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                    localAppData = Path.Combine(userProfile, "AppData", "Local");
            }

            var fallbackFileName = Path.GetFileNameWithoutExtension(requestedPath)
                + ".fallback" + Path.GetExtension(requestedPath);
            var fallbackPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(localAppData) && Path.IsPathFullyQualified(localAppData))
            {
                fallbackPaths.Add(Path.GetFullPath(Path.Combine(
                    localAppData, ProductIdentity.UserDataDirectoryName, fallbackFileName)));
            }

            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(roamingAppData) && Path.IsPathFullyQualified(roamingAppData))
                fallbackPaths.Add(Path.GetFullPath(Path.Combine(
                    roamingAppData, ProductIdentity.UserDataDirectoryName, fallbackFileName)));

            // 用户 Temp 与 LocalAppData 常在同一受限配置文件中；再试系统 Temp。
            // 这里存放的只是可重建索引缓存，原数据库始终保留。
            var tempPath = Path.GetTempPath();
            if (!string.IsNullOrWhiteSpace(tempPath) && Path.IsPathFullyQualified(tempPath))
                fallbackPaths.Add(Path.GetFullPath(Path.Combine(
                    tempPath, ProductIdentity.UserDataDirectoryName, fallbackFileName)));
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory) && Path.IsPathFullyQualified(windowsDirectory))
                fallbackPaths.Add(Path.GetFullPath(Path.Combine(
                    windowsDirectory, "Temp", ProductIdentity.UserDataDirectoryName, fallbackFileName)));

            var attemptedPaths = new List<string>();
            var failureDetails = new List<string> { $"{requestedPath}: {ex.Message}" };
            var failures = new List<Exception> { ex };
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requestedPath };
            Exception lastFailure = ex;
            foreach (var fallbackPath in fallbackPaths)
            {
                if (!seenPaths.Add(fallbackPath)) continue;
                attemptedPaths.Add(fallbackPath);
                Log.Warning(lastFailure,
                    "SQLite 缓存路径 {DatabasePath} 无法打开，尝试可重建缓存路径 {FallbackPath}",
                    requestedPath, fallbackPath);
                try
                {
                    var cache = OpenCacheAtPath(fallbackPath);
                    actualPath = fallbackPath;
                    return cache;
                }
                catch (Exception fallbackException) when (IsRecoverableDatabaseOpenFailure(fallbackException))
                {
                    lastFailure = fallbackException;
                    failures.Add(fallbackException);
                    failureDetails.Add($"{fallbackPath}: {fallbackException.Message}");
                }
            }

            // The listing cache can be rebuilt from the cloud. If every disk-backed location
            // is unavailable, keep startup working with a process-local SQLite database.
            try
            {
                var cache = OpenCacheAtPath(":memory:");
                actualPath = ":memory:（仅当前进程有效）";
                try
                {
                    Log.Warning("所有磁盘缓存路径都不可写，改用进程内 SQLite 缓存；本次会话结束后缓存将清空。失败详情：{Failures}",
                        string.Join("；", failureDetails));
                }
                catch { /* 日志目录也不可写时仍继续启动。 */ }
                return cache;
            }
            catch (Exception memoryException)
            {
                lastFailure = memoryException;
                failures.Add(memoryException);
                failureDetails.Add($":memory:: {memoryException.Message}");
            }

            var attempted = attemptedPaths.Count == 0 ? "没有可用的磁盘备用路径" : string.Join("；", attemptedPaths);
            throw new InvalidOperationException(
                $"SQLite 缓存无法打开。原路径：'{requestedPath}'；已尝试备用路径：{attempted}。" +
                $"错误详情：{string.Join("；", failureDetails)}；最后错误：{lastFailure.Message}",
                new AggregateException(failures));
        }
    }

    private static SqliteCache OpenCacheAtPath(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var cache = new SqliteCache(databasePath);
        try
        {
            // 启动时清空列举缓存：保证占位文件与缓存状态一致。
            // 上次退出可能清理了占位文件，但 SQLite 缓存仍在；不清空会命中缓存而跳过重建。
            cache.ResetListingCache();
            return cache;
        }
        catch
        {
            cache.Dispose();
            throw;
        }
    }

    private static bool IsRecoverableDatabaseOpenFailure(Exception exception) =>
        exception is UnauthorizedAccessException ||
        exception is SqliteException { SqliteErrorCode: 8 or 14 };

    // ============== 配置加载 ==============

    private static void LoadConfig(string[] args)
    {
        var maintenance = args.FirstOrDefault(argument => argument.StartsWith("--maintenance-config=", StringComparison.OrdinalIgnoreCase));
        if (maintenance != null)
        {
            if (!args.Any(argument => argument.Equals("--unregister", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("维护配置仅允许用于 --unregister。");
            _ctx = ConfigurationStore.Load(decryptPassword: false,
                configurationPath: maintenance["--maintenance-config=".Length..], migrate: false);
            return;
        }
        _ctx = ConfigurationStore.Load(AppContext.BaseDirectory,
            decryptPassword: !args.Any(argument => argument.Equals("--unregister", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--dehydrate", StringComparison.OrdinalIgnoreCase)));
    }

    // ============== 设置界面支持（供 Tray/SettingsWindow 调用） ==============

    /// <summary>防止并发应用设置（"确定"与"应用"都可能触发）：同时只允许一个应用流程。</summary>
    private static int _settingsBusy;

    private static async Task RunUiSelfTestAsync(string[] args)
    {
        var testRoot = Path.Combine(Path.GetTempPath(),
            $"{ProductIdentity.Identifier}-WinUiSelfTest-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var configPath = Path.Combine(testRoot, "appsettings.selftest.json");
        var captchaPath = Path.Combine(testRoot, "captcha.png");
        var reportPath = Path.Combine(testRoot, "ui-selftest-results.json");
        var fakeConfig = new SyncRootContext
        {
            SyncRootPath = Path.Combine(testRoot, "sync-root-never-created"),
            Server = "https://example.invalid",
            Username = "winui-selftest",
            ProviderName = "WinUI Self Test",
            FolderCacheMinutes = 30,
        };

        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(fakeConfig));
            _ctx = JsonSerializer.Deserialize<SyncRootContext>(await File.ReadAllTextAsync(configPath))
                ?? throw new InvalidDataException("自检临时配置无法读取。");
            var image = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAGQAAAAyCAYAAACqNX6+AAAAOklEQVR4nO3BAQ0AAADCoPdPbQ8H" +
                "FAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAvBtOAAABlBBCAAAAAElFTkSuQmCC");
            await File.WriteAllBytesAsync(captchaPath, image);
            ConfigureLogger(Path.Combine(testRoot, "logs"));

            _uiSelfTestHoldSeconds = ParseHoldSeconds(args);
            _uiSelfTestShot = args.Any(a => a.Equals("--ui-selftest-shot", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--wpf-ui-selftest-shot", StringComparison.OrdinalIgnoreCase));

            var checks = await WinUiUiHost.RunSelfTestAsync(captchaPath, testRoot,
                _uiSelfTestHoldSeconds, _uiSelfTestShot).ConfigureAwait(false);
            var passed = checks.All(check => check.Passed);
            foreach (var check in checks)
            {
                if (check.Passed) Log.Information("[SELFTEST] PASS {Name}: {Detail}", check.Name, check.Detail);
                else Log.Error("[SELFTEST] FAIL {Name}: {Detail}", check.Name, check.Detail);
            }

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId,
                Passed = passed,
                ConfigPath = configPath,
                CaptchaPath = captchaPath,
                Checks = checks,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Log.Information("[SELFTEST] 报告路径：{ReportPath}", reportPath);
            if (!passed) Environment.ExitCode = 1;
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            try
            {
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                {
                    ProcessId = Environment.ProcessId,
                    Passed = false,
                    ConfigPath = configPath,
                    CaptchaPath = captchaPath,
                    Error = error.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            Log.Fatal(error, "WinUI 自检失败");
        }
        finally
        {
            TrayHost.Shutdown();
            await Task.Run(() => TrayHost.Join(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// 当前配置的深拷贝快照（JSON 往返），供设置界面编辑。
    /// 不用 MemberwiseClone（protected 不可外部调用）；且浅拷贝会让 string[] 之类的引用共享，编辑时容易串改运行时状态。
    /// </summary>
    internal static SyncRootContext SnapshotConfig()
    {
        var snapshot = JsonSerializer.Deserialize<SyncRootContext>(JsonSerializer.Serialize(_ctx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SyncRootContext();
        snapshot.Password = _ctx.Password;
        return snapshot;
    }

    /// <summary>把 <see cref="_ctx"/> 写回当前 Windows 用户的运行配置。</summary>
    internal static void SaveConfig()
    {
        ConfigurationStore.Save(_ctx);
        Log.Information("配置已写入 {Path}", AppPaths.ConfigPath);
    }

    /// <summary>Switch accounts through the normal shutdown path so the old SyncRoot is cleared while connected.</summary>
    internal static void RequestAccountSwitch(SyncRootContext target)
    {
        if (target.Username.Equals(_ctx.Username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("所选账户已经是当前账户。");
        if (System.Threading.Interlocked.Exchange(ref _settingsBusy, 1) == 1)
            throw new InvalidOperationException("另一个设置操作正在进行。");

        var switchContext = SnapshotConfig();
        switchContext.Username = target.Username;
        switchContext.ActiveAccount = target.Username;
        switchContext.Password = target.Password;
        switchContext.Accounts = target.Accounts.Select(account => new AccountCredential
        {
            Username = account.Username,
            ProtectedPassword = account.ProtectedPassword,
        }).ToList();
        _pendingAccountSwitch = switchContext;
        Callbacks.SuppressDeleteRecovery = true;
        Log.Information("收到账户切换请求：{OldAccount} → {NewAccount}；开始优雅关闭并清理同步目录",
            _ctx.Username, target.Username);
        RequestShutdownFromUi();
    }

    /// <summary>
    /// 把 <see cref="_ctx"/> 中支持热生效的项下发到运行时的 Callbacks 静态字段。
    /// 覆盖：目录缓存 / 自动刷新周期（含 1~120 归一化）、按需填充策略、SyncRootPath、UserKey 和容器 TypeId。
    /// </summary>
    internal static void ApplyHotConfig()
    {
        _ctx.FolderCacheMinutes = NormalizeCacheMinutes(_ctx.FolderCacheMinutes,
            nameof(SyncRootContext.FolderCacheMinutes));
        Callbacks.FolderCache = TimeSpan.FromMinutes(_ctx.FolderCacheMinutes);
        Callbacks.DisablePopulationAfterPopulate = true;
        Callbacks.SyncRootPath = Path.GetFullPath(_ctx.SyncRootPath);
        Callbacks.UserKey = CurrentUserKey();
        Callbacks.ContainerTypeId = _ctx.RootContainerTypeId;
    }

    /// <summary>
    /// 应用设置（由托盘"设置"窗口调用，必须跑在线程池线程，不可在 UI 线程或 cfapi 回调线程）。
    ///
    /// 流程：
    /// 1) 用编辑结果整体替换 _ctx（除 SyncRootPath 外立即落定，路径留到切换成功后）；
    /// 2) ApplyHotConfig 下发缓存分钟数 / 按需填充策略等立即生效项；
    /// 3) 先 SaveConfig 落盘（即便后续失败，重启也是新配置）；
    /// 4) 路径 / Provider 显示名变化 → 先 <see cref="SyncRootProbe"/> 探测新位置，
    ///    不通过则**原样保留旧挂载点**直接返回；通过才走 <see cref="SwitchSyncRootAsync"/> 热切换
    ///    （它本身是"先拆旧再挂新"，失败回滚旧路径）；
    ///    仅容器 TypeId 变化 → 重建顶层占位；
    /// 5) 再次 SaveConfig。
    /// </summary>
    /// <returns>(是否全部成功, 给用户看的摘要)</returns>
    internal static async Task<(bool ok, string message)> ApplySettingsAsync(SyncRootContext edit)
    {
        if (System.Threading.Interlocked.Exchange(ref _settingsBusy, 1) == 1)
            return (false, "上一次设置尚未应用完成，请稍候再试。");

        var msgs = new List<string>();
        var applied = true;
        // 放在 try 外，供 catch 里回滚使用（避免用 Exception.Data 这种弱类型传递）
        var oldPath = Path.GetFullPath(_ctx.SyncRootPath);
        var oldProviderName = _ctx.ProviderName;
        try
        {
            var newPath = Path.GetFullPath(edit.SyncRootPath);
            var pathChanged = !string.Equals(oldPath.TrimEnd('\\'), newPath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
            var registrationChanged = pathChanged
                || !string.Equals(_ctx.ProviderName, edit.ProviderName, StringComparison.Ordinal);
            var containerChanged = _ctx.RootContainerTypeId != edit.RootContainerTypeId;

            // 1) 整体替换；路径先保持旧值，切换成功后再改（失败时可原样回滚）
            _ctx = edit;
            _ctx.SyncRootPath = oldPath;

            // 2) 立即生效项
            ApplyHotConfig();
            msgs.Add("缓存/刷新/填充策略已生效");

            // 3) 先落盘
            SaveConfig();
            UpdateService.SetAutomaticCheckEnabled(_ctx.AutomaticUpdateCheck);
            TrayHost.SetTrayUpdateHintEnabled(_ctx.TrayUpdateHintEnabled);

            // 4) 重建 SyncRoot
            if (registrationChanged)
            {
                // **先探测、后拆旧**：SwitchSyncRootAsync 是"先停 Fixer → 清占位 → 断开 → 反注册 → 才挂新"，
                // 新位置要是挂不上（FAT32/exFAT 卷、无写权限、嵌在别的 SyncRoot 里…），旧挂载点已经没了，
                // 用户会掉进"两边都没有"的窗口期，只能指望回滚。探测不通过就直接放弃：**旧根一概不动**。
                //
                // 只在**路径真的变了**时才探：路径没变（仅 Provider 显示名改动 → 也要重建注册）时，
                // 这个目录正在被使用，卷能力与可写可删都已验证过，再探一次只会多打一条
                // "已注册为本程序"的警告，属于自找噪音。
                if (pathChanged)
                {
                    var probe = SyncRootProbe.Probe(newPath, oldPath, edit.ProviderName);
                    Log.Information("[SETTINGS] 新挂载位置探测: {Path} → {Detail}", newPath, probe.Describe(true));
                    if (!probe.Ok)
                    {
                        _ctx.SyncRootPath = oldPath;
                        Callbacks.SyncRootPath = oldPath;
                        SaveConfig();
                        return (false, $"新位置不可用，已保留原挂载点（{oldPath}）：{probe.Describe()}");
                    }
                }

                _ctx.SyncRootPath = newPath;
                await SwitchSyncRootAsync(oldPath, oldProviderName).ConfigureAwait(false);
                msgs.Add(pathChanged ? $"挂载目录已切换：{oldPath} → {newPath}" : "已按新 Provider 显示名重建 SyncRoot");
            }
            else if (containerChanged)
            {
                try
                {
                    // 整棵树的本地层级随入口变化，重新列举时写入新路径。
                    _cache?.ResetListingCache();
                    _cache?.ClearFolderLocalPaths();
                    await RefreshDiskTreeAndReconcileAsync().ConfigureAwait(false);
                    msgs.Add("顶层占位已按新磁盘树入口重建");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[SETTINGS] 顶层占位重建失败");
                    msgs.Add("顶层占位重建失败（详见日志）");
                    applied = false;
                }
            }

            SaveConfig();
            return (applied, string.Join("；", msgs) + "。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SETTINGS] 应用设置失败，尝试回滚到旧挂载点");
            try { await RestoreSyncRootAsync(oldPath, oldProviderName).ConfigureAwait(false); }
            catch { /* 回滚自身失败已在其内部记录 */ }
            return (false, $"应用失败：{ex.Message}（已尝试回滚到 {oldPath}，详见日志）");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _settingsBusy, 0);
        }
    }

    /// <summary>
    /// 热切换 SyncRoot 到 <see cref="_ctx.SyncRootPath"/>（从 oldPath 迁出）。
    ///
    /// 顺序**不可调换**（对齐 Program 退出清理的硬约束）：
    ///   停 PlaceholderFixer → 仍在连接态清理旧占位 → CfDisconnectSyncRoot → 释放占位数组
    ///   → Unregister 旧路径 → CreateDirectory + Unregister 残留 + 清内容 + Register 新路径
    ///   → 重设 Callbacks.SyncRootPath → Build 回调表 + CfConnectSyncRoot
    ///   → 重置 DB 本地路径缓存（local_path/last_listed）→ 重建顶层占位 → 重启 Fixer → 重新预拉取。
    /// 任一步失败都会尝试 <see cref="RestoreSyncRootAsync"/> 回到旧路径。
    /// </summary>
    private static async Task SwitchSyncRootAsync(string oldPath, string oldProviderName)
    {
        var newPath = Path.GetFullPath(_ctx.SyncRootPath);
        Log.Information("[SETTINGS] 开始热切换 SyncRoot: {Old} → {New}", oldPath, newPath);

        // 1. 停属性监视器和后台目录时间线程，避免清理/切换过程中发起 CFAPI 操作。
        StopPinStateWatcher();
        PlaceholderFixer.Stop();

        // 2. 清理旧占位（必须在 CfDisconnectSyncRoot 之前！断开后 cldflt 会拒绝删除孤儿重解析点）
        var cleanupFailures = 0;
        if (Directory.Exists(oldPath))
        {
            try { cleanupFailures = CleanupSyncRootContents(oldPath); }
            catch (Exception ex)
            {
                cleanupFailures = 1;
                Log.Warning(ex, "[SETTINGS] 清理旧 SyncRoot 内容失败: {Path}", oldPath);
            }
        }

        // 3. 断开回调通道
        try
        {
            var hr = CfDisconnectSyncRoot(_connectionKey);
            if (hr.Failed)
                Log.Warning("[SETTINGS] CfDisconnectSyncRoot 失败（可能已断开）: 0x{Hr:X}", hr.Code);
            else
                Log.Information("[SETTINGS] 回调通道已断开");
        }
        catch (Exception ex) { Log.Warning(ex, "[SETTINGS] CfDisconnectSyncRoot 异常（忽略）"); }

        // 4. 释放按目录缓存的占位数组非托管内存
        CloudFiles.Callbacks.ReleasePlaceholderArrays();

        if (cleanupFailures != 0)
            throw new IOException("旧同步目录未能完全清理，已取消切换以保留用户文件访问。");

        // 5. 反注册旧路径
        try
        {
            SyncRootRegistrar.Unregister(oldPath, oldProviderName);
            Log.Information("[SETTINGS] 旧 SyncRoot 已反注册: {Path}", oldPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SETTINGS] 旧 SyncRoot 反注册失败，取消切换: {Path}", oldPath);
            throw;
        }

        // 6. 注册新路径（失败则回滚旧路径）
        try
        {
            Directory.CreateDirectory(newPath);
            // 防御性反注册：新路径可能残留上次注册（例如用户改回去）
            try { SyncRootRegistrar.Unregister(newPath, _ctx.ProviderName); } catch { /* 本来就没注册 */ }
            if (Directory.Exists(newPath))
            {
                try { CleanupSyncRootContents(newPath); }
                catch (Exception ex) { Log.Warning(ex, "[SETTINGS] 清理新目录旧内容失败（继续）: {Path}", newPath); }
            }
            SyncRootRegistrar.Register(newPath, _ctx.ProviderName,
                _ctx.RootContainerTypeId);
            Log.Information("[SETTINGS] 新 SyncRoot 注册成功: {Path}", newPath);
        }
        catch (Exception ex)
        {
            // 回滚由调用方 ApplySettingsAsync 统一做（避免嵌套重复注册/连接）
            Log.Error(ex, "[SETTINGS] 新 SyncRoot 注册失败: {Path}", newPath);
            _ctx.SyncRootPath = oldPath;
            Callbacks.SyncRootPath = Path.GetFullPath(oldPath);
            throw new InvalidOperationException($"新路径注册失败：{ex.Message}", ex);
        }

        // 7. 新路径生效（Register 内部也会写，这里是双保险，覆盖回滚/复用分支）
        Callbacks.SyncRootPath = newPath;

        // 8. 重新建立回调通道
        _callbackTable = CallbackTable.Build();
        var connect = CfConnectSyncRoot(newPath, _callbackTable, IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey);
        if (connect.Failed)
        {
            Log.Error("[SETTINGS] CfConnectSyncRoot 失败: 0x{Hr:X}", connect.Code);
            try { SyncRootRegistrar.Unregister(newPath, _ctx.ProviderName); } catch { /* 忽略 */ }
            _ctx.SyncRootPath = oldPath;
            Callbacks.SyncRootPath = Path.GetFullPath(oldPath);
            throw new InvalidOperationException($"CfConnectSyncRoot 失败: 0x{connect.Code:X} ({connect})");
        }
        Log.Information("[SETTINGS] 新 SyncRoot 已连接: {Path}", newPath);
        StartPinStateWatcher();

        // 9. SQLite 里按绝对路径存的 folders.local_path 全部失效 → 清空（下次列举/预拉取按新根重写）
        try
        {
            _cache?.ResetListingCache();
            _cache?.ClearFolderLocalPaths();
            Log.Information("[SETTINGS] 已重置目录列举缓存与本地路径缓存");
        }
        catch (Exception ex) { Log.Warning(ex, "[SETTINGS] 重置 DB 缓存失败（自愈：列举会自行覆盖）"); }

        // 10. 重建顶层占位（含磁盘树刷新与过期占位清理）
        try
        {
            await RefreshDiskTreeAndReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SETTINGS] 顶层占位重建失败（忽略，浏览时回调会补）");
        }

        // 11. 重启后台修正线程 + 重新预拉取（让新目录树的时间与内容一次到位）
        PlaceholderFixer.Start(msg => Log.Information("{Msg}", msg));
        _ = Task.Run(() => PreFetchAllDisksAsync(_cts.Token));
    }

    /// <summary>
    /// 回滚：把 SyncRoot 恢复到指定路径（尽力而为，失败只记录）。
    /// 热切换中途失败时调用，保证用户至少还能用旧挂载点。
    /// </summary>
    private static async Task RestoreSyncRootAsync(string? oldSnapshotPath, string oldProviderName)
    {
        if (string.IsNullOrWhiteSpace(oldSnapshotPath)) return;
        try
        {
            _ctx.SyncRootPath = oldSnapshotPath;
            _ctx.ProviderName = oldProviderName;
            Callbacks.SyncRootPath = Path.GetFullPath(oldSnapshotPath);
            try { Directory.CreateDirectory(oldSnapshotPath); } catch { /* 忽略 */ }
            try { SyncRootRegistrar.Unregister(oldSnapshotPath, oldProviderName); } catch { /* 防御性，忽略 */ }
            SyncRootRegistrar.Register(oldSnapshotPath, oldProviderName,
                _ctx.RootContainerTypeId);
            _callbackTable = CallbackTable.Build();
            var hr = CfConnectSyncRoot(oldSnapshotPath, _callbackTable, IntPtr.Zero,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey);
            if (hr.Failed)
            {
                Log.Error("[SETTINGS] 回滚失败，CfConnectSyncRoot 0x{Hr:X}；建议重启程序或运行 --unregister", hr.Code);
                return;
            }
            StartPinStateWatcher();
            PlaceholderFixer.Start(msg => Log.Information("{Msg}", msg));
            Log.Warning("[SETTINGS] 已回滚到旧 SyncRoot: {Path}", oldSnapshotPath);
            SaveConfig();
            await RefreshDiskTreeAndReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SETTINGS] 回滚到旧 SyncRoot 失败: {Path}；建议重启程序", oldSnapshotPath);
        }
    }

    // ============== 单实例 ==============

    /// <summary>
    /// 确保本进程是会话内唯一实例。
    /// 判定手段是**内核命名互斥体**（不是遍历进程名）：后者存在"我查到你之前你也刚启动"的竞态，
    /// 且同名进程（比如调试时另跑一份 exe）无法区分。
    /// 进程意外终止时句柄被系统回收、互斥对象随之销毁，不会留下永久"黑名单"。
    /// </summary>
    /// <returns>true=我是唯一的，继续启动；false=已有实例运行，调用方应立即 return</returns>
    private static bool EnsureSingleInstance(bool openFolderIfRunning = false, bool waitForPrevious = false)
    {
        try
        {
            var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                _singleInstanceMutex = mutex;   // 持到进程结束（静态引用防 GC 回收导致句柄关闭）
                return true;
            }
            if (waitForPrevious)
            {
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromSeconds(30)))
                    {
                        _singleInstanceMutex = mutex;
                        Log.Information("账户切换重启已等候旧实例完成优雅退出");
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    _singleInstanceMutex = mutex;
                    Log.Warning("旧实例异常终止；账户切换重启取得单实例锁");
                    return true;
                }
                mutex.Dispose();
                Log.Error("等待旧实例释放单实例锁超时，取消账户切换重启");
                return false;
            }
            // 别人已持有：立刻释放自己刚拿到的引用，不要 hold 住
            mutex.Dispose();

            if (openFolderIfRunning)
            {
                Log.Information("检测到已有运行中的实例，直接打开同步目录: {Path}", _ctx.SyncRootPath);
                OpenSyncFolder();
                Log.CloseAndFlush();
            }
            else
            {
                Log.Warning("检测到已有运行中的实例（{Name} 已存在），取消本次启动", SingleInstanceMutexName);
                ShowAlreadyRunningDialog();
            }
            return false;
        }
        catch (Exception ex)
        {
            // 互斥体创建失败（权限/环境异常）不应阻断业务 —— 放行，交给后续的 0x17A 守卫兜底
            Log.Warning(ex, "单实例检查失败（放行本次启动，若撞上已有实例会报 0x17A）");
            return true;
        }
    }

    private static void OpenSyncFolder()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(_ctx.SyncRootPath);
            System.Diagnostics.Process.Start(startInfo);
            Log.Information("已打开同步目录: {Path}", _ctx.SyncRootPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开同步目录失败: {Path}", _ctx.SyncRootPath);
        }
    }

    /// <summary>
    /// 弹"已有运行中的实例。"提示。
    /// 用 Win32 MessageBoxW，而不是依赖 XAML 的消息框：UI Dispatcher 此刻可能还没起来，
    /// native 弹窗零依赖且带 MB_SYSTEMMODAL/MB_TOPMOST，不会被埋在某个窗口后面。
    /// --quiet 无头模式不弹窗（只写日志），免得打断计划任务的静默执行。
    /// </summary>
    private static void ShowAlreadyRunningDialog()
    {
        if (_quiet) return;
        // 控制台模式下同时输出到控制台，便于脚本/终端排查
        if (_hasConsole)
        {
            try { Console.Error.WriteLine("已有运行中的实例。"); } catch { /* 忽略 */ }
        }
        try
        {
            MessageBoxW(IntPtr.Zero, "已有运行中的实例。", ProductIdentity.DisplayNameZh,
                MessageBoxOk | MessageBoxIconInformation | MessageBoxSystemModal
                | MessageBoxSetForeground | MessageBoxTopMost);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "弹出重复实例提示失败（忽略）");
        }
    }

    /// <summary>报告已处理的启动失败：写入日志、返回非零任务结果，并在交互会话中显示错误。</summary>
    private static void ReportStartupFailure(string message, Exception? error = null)
    {
        Environment.ExitCode = 1;
        try
        {
            if (error == null) Log.Error("启动失败：{Message}", message);
            else Log.Error(error, "启动失败：{Message}", message);
        }
        catch { /* 即使日志器不可用，仍尝试显示原因。 */ }

        if (_hasConsole)
        {
            try { Console.Error.WriteLine($"启动失败：{message}{(error == null ? "" : $" {error.Message}")}"); }
            catch { /* 控制台不可用时继续处理。 */ }
        }

        try { Log.CloseAndFlush(); } catch { /* 交互提示仍需显示，即使日志无法刷新。 */ }

        if (_quiet || Interlocked.Exchange(ref _crashDialogShown, 1) != 0) return;
        try
        {
            var detail = error == null ? message : $"{message}\r\n{error.GetType().Name}：{error.Message}";
            if (detail.Length > 1800) detail = detail[..1800] + "...";
            MessageBoxW(IntPtr.Zero, detail, $"{ProductIdentity.DisplayNameZh} - 启动失败",
                MessageBoxOk | MessageBoxIconError | MessageBoxSystemModal
                | MessageBoxSetForeground | MessageBoxTopMost);
        }
        catch { /* 启动错误处理不能再抛异常。 */ }
    }

    private static string DescribeStartupSecurityContext()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            using var process = Process.GetCurrentProcess();
            var integritySid = identity.Groups?
                .FirstOrDefault(group => group.Value.StartsWith("S-1-16-", StringComparison.Ordinal))?.Value
                ?? "未知";
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return $"启动诊断：账户={identity.Name}；SID={identity.User?.Value}；"
                + $"完整性级别={integritySid}；会话={process.SessionId}；"
                + $"管理员令牌={principal.IsInRole(WindowsBuiltInRole.Administrator)}；"
                + $"交互桌面={Environment.UserInteractive}；工作目录='{Environment.CurrentDirectory}'；"
                + $"用户目录='{userProfile}'；LocalAppData='{localAppData}'；"
                + $"LOCALAPPDATA环境变量='{Environment.GetEnvironmentVariable("LOCALAPPDATA")}'；日志目录='{_logDir}'。";
        }
        catch (Exception ex)
        {
            return $"启动诊断信息获取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 安装进程级未捕获异常兜底：AppDomain + TaskScheduler。
    /// 直接写 crash 文件（不依赖 Serilog，Serilog 未配置时也能落盘），便于"窗口不出现且无日志"时定位真因。
    /// </summary>
    private static void InstallCrashHandlers()
    {
        if (Interlocked.Exchange(ref _crashHandlersInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var tag = $"AppDomain.UnhandledException; stage={_startupStage}";
            try { Log.Fatal(ex, "未捕获的 AppDomain 异常（进程即将终止），阶段={Stage}", _startupStage); } catch { /* Serilog 未就绪时忽略 */ }
            var crashPath = WriteCrashFile(ex, tag);
            ShowCrashDialog(ex, tag, crashPath);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Log.Error(e.Exception, "未观察的 Task 异常"); } catch { /* 忽略 */ }
            WriteCrashFile(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }

    /// <summary>
    /// 处理会导致进程退出的未处理异常：先写入崩溃日志，再显示一次原生错误弹窗。
    /// 使用 Win32 MessageBox 是因为该处理器可能运行在没有可用 XAML 窗口或 Dispatcher 的线程上。
    /// </summary>
    internal static void HandleFatalException(Exception? ex, string tag)
    {
        try { Log.Fatal(ex, "未处理异常（进程即将终止）: {Tag}", tag); } catch { /* Serilog 未就绪时忽略 */ }
        var crashPath = WriteCrashFile(ex, tag);
        ShowCrashDialog(ex, tag, crashPath);
    }

    private static void ShowCrashDialog(Exception? ex, string tag, string? crashPath)
    {
        if (_quiet || Interlocked.Exchange(ref _crashDialogShown, 1) != 0) return;

        try
        {
            var detail = ex == null
                ? "无异常详细信息。"
                : $"异常类型：{ex.GetType().FullName}\r\n异常信息：{ex.Message}";
            if (detail.Length > 1800) detail = detail[..1800] + "...";

            var log = string.IsNullOrWhiteSpace(crashPath) ? "崩溃日志写入失败。" : $"崩溃日志：{crashPath}";
            var message = $"程序遇到未处理的错误，即将退出。\r\n{detail}\r\n来源：{tag}\r\n{log}";
            MessageBoxW(IntPtr.Zero, message, $"{ProductIdentity.DisplayNameZh} - 错误",
                MessageBoxOk | MessageBoxIconError | MessageBoxSystemModal);
        }
        catch
        {
            // 错误处理路径不能再次抛出，避免遮蔽原始异常。
        }
    }

    /// <summary>把异常直接写到 logs/crash_*.log（绕过 Serilog，确保即使日志器未初始化也能落盘）。</summary>
    internal static string? WriteCrashFile(Exception? ex, string tag)
    {
        var fileName = $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log";
        var candidatePaths = new List<string>();
        try { candidatePaths.Add(Path.Combine(AppPaths.ResolveDataPath("logs"), fileName)); }
        catch { /* 应用数据目录解析失败时继续尝试 Temp。 */ }
        try
        {
            candidatePaths.Add(Path.Combine(Path.GetTempPath(), ProductIdentity.UserDataDirectoryName, "logs", fileName));
        }
        catch { /* 临时目录解析失败时保留已有候选路径。 */ }
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                candidatePaths.Add(Path.Combine(profile, ProductIdentity.UserDataDirectoryName, "logs", fileName));
        }
        catch { /* 用户目录不可用时继续尝试系统临时目录。 */ }
        try
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
                candidatePaths.Add(Path.Combine(windowsDirectory, "Temp", ProductIdentity.UserDataDirectoryName, "logs", fileName));
        }
        catch { /* 系统临时目录解析失败时保留已有候选路径。 */ }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidatePaths)
        {
            if (!seenPaths.Add(path)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{DateTime.Now:o}] {tag}");
                sb.AppendLine(ex?.ToString() ?? "(null exception)");
                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch { /* 继续尝试备用目录；所有目录都失败才放弃。 */ }
        }

        return null;
    }

    /// <summary>本次运行使用的日志文件完整路径（启动时确定，退出时不改变）</summary>
    private static string _currentLogPath = "";
    /// <summary>日志目录（供托盘"打开日志目录"菜单项使用，启动时由 ConfigureLogger 赋值）</summary>
    private static string _logDir = "logs";
    /// <summary>供托盘菜单"打开日志目录"读取日志目录完整路径。</summary>
    internal static string LogDirectory => _logDir;

    private static void ConfigureLogger(string? directoryOverride = null)
    {
        // 每次运行一个独立日志文件：logs/{ProductIdentity.Identifier}_yyyyMMdd_HHmmss.log
        // 旧配置的 LogPath 可能是 "YuWangCloudSync.log"（文件名），这里取其目录部分，
        // 保证升级后不会退化成单文件追加。
        var configured = directoryOverride ?? _ctx.LogPath ?? "logs";
        if (Path.HasExtension(configured)) configured = Path.GetDirectoryName(configured) ?? "logs";
        if (string.IsNullOrWhiteSpace(configured)) configured = "logs";

        var isRelative = !Path.IsPathRooted(configured);
        var logDirectories = new List<string>();
        void AddLogDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
            var fullPath = Path.GetFullPath(path);
            if (!logDirectories.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                logDirectories.Add(fullPath);
        }

        try
        {
            AddLogDirectory(Path.IsPathRooted(configured)
                ? configured
                : AppPaths.ResolveDataPath(configured));
        }
        catch when (isRelative) { /* 后面继续尝试其它用户可写目录。 */ }

        if (isRelative)
        {
            try { AddLogDirectory(Path.Combine(Path.GetTempPath(), ProductIdentity.UserDataDirectoryName, "logs")); }
            catch { /* 继续尝试其它目录。 */ }
            try
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(roaming))
                    AddLogDirectory(Path.Combine(roaming, ProductIdentity.UserDataDirectoryName, "logs"));
            }
            catch { /* 继续尝试其它目录。 */ }
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile))
                    AddLogDirectory(Path.Combine(profile, ProductIdentity.UserDataDirectoryName, "logs"));
            }
            catch { /* 继续尝试系统临时目录。 */ }
            try
            {
                var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrWhiteSpace(windowsDirectory))
                    AddLogDirectory(Path.Combine(windowsDirectory, "Temp", ProductIdentity.UserDataDirectoryName, "logs"));
            }
            catch { /* 所有候选目录都会逐一尝试。 */ }
        }

        // 在各候选目录中直接创建本次日志文件；这同时确认 Serilog 可写入该位置。
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string? logDir = null;
        string? logPath = null;
        Exception? lastLogFailure = null;
        foreach (var candidateDir in logDirectories)
        {
            try
            {
                Directory.CreateDirectory(candidateDir);
                for (var seq = 1; seq <= 1000; seq++)
                {
                    var suffix = seq == 1 ? "" : $"_{seq}";
                    var candidatePath = Path.Combine(candidateDir, $"{ProductIdentity.Identifier}_{stamp}{suffix}.log");
                    try
                    {
                        using (new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
                        logDir = candidateDir;
                        logPath = candidatePath;
                        break;
                    }
                    catch (IOException) when (File.Exists(candidatePath))
                    {
                        // 同秒启动的进程已创建该文件，尝试下一个序号。
                    }
                }
                if (logPath != null) break;
            }
            catch (Exception ex)
            {
                lastLogFailure = ex;
            }
        }

        if (logPath == null && !isRelative)
            throw new IOException($"配置的绝对日志目录不可写：'{configured}'", lastLogFailure);

        _logDir = logDir ?? logDirectories.FirstOrDefault() ?? "logs";
        logPath ??= "";
        _currentLogPath = logPath;

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information();
        if (!string.IsNullOrWhiteSpace(logPath))
            loggerConfig = loggerConfig.WriteTo.File(logPath, rollingInterval: RollingInterval.Infinite,
                retainedFileCountLimit: null);
        // 仅当存在可用控制台（--console 或附着父控制台）时挂 Console sink；
        // --quiet 与纯 GUI（双击运行无控制台）只写文件日志。若所有相对目录都不可写，仍继续启动。
        if (_hasConsole) loggerConfig = loggerConfig.WriteTo.Console();
        var nextLogger = loggerConfig.CreateLogger();
        Log.CloseAndFlush();
        Log.Logger = nextLogger;

        // 让回调也能用日志
        Callbacks.Log = msg => Log.Information("{Msg}", msg);
        if (!string.IsNullOrWhiteSpace(logPath))
            Log.Information("日志文件: {LogPath}", logPath);
        else
            Log.Warning("所有日志目录都不可写，本次运行将不保留文件日志：{Reason}", lastLogFailure?.Message ?? "没有可用的日志目录");

        if (logDir != null) PruneOldLogs(logDir);
    }

    /// <summary>只保留最近 LogsToKeep 个日志文件（按最后写入时间排序）</summary>
    private static void PruneOldLogs(string logDir)
    {
        var keep = _ctx.LogsToKeep;
        if (keep <= 0) return;
        try
        {
            var files = Directory.EnumerateFiles(logDir, $"{ProductIdentity.Identifier}_*.log")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            if (files.Count <= keep) return;
            foreach (var f in files.Skip(keep))
            {
                try { f.Delete(); }
                catch { /* 删不掉就算了，不影响主流程 */ }
            }
        }
        catch { /* 清理失败不影响运行 */ }
    }
}
