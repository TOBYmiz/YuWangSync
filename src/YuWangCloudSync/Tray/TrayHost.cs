using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using H.NotifyIcon.Core;
using Serilog;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Updates;

namespace YuWangCloudSync.Tray;

/// <summary>
/// Win32 托盘、WinUI 按需窗口和关机监听宿主。
/// 托盘使用 H.NotifyIcon.Core 的原生 PopupMenu，回调运行在 WinUI DispatcherQueue 所在线程。
/// </summary>
internal static class TrayHost
{
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim UiReady = new(false);
    private static bool _showUi;
    private static bool _started;
    private static TrayIcon? _tray;
    private static PopupMenu? _trayContextMenu;
    private static PopupMenuItem? _aboutMenuItem;
    private static IntPtr _trayIconHandle;
    private static IntPtr _trayUpdateIconHandle;
    private static IntPtr _activeTrayIconHandle;
    private static bool _trayUpdateHintEnabled = true;
    private static bool _trayCreated;
    private static readonly ManualResetEventSlim ShutdownComplete = new(false);

    internal static bool IsTrayCreated => Volatile.Read(ref _trayCreated);

    private const int ShellWaitTimeoutMs = 90_000;
    private const int ShellPollIntervalMs = 1_000;
    private const int ShellReadyGraceMs = 500;
    private const int TrayCreateMaxAttempts = 5;
    private const int TrayCreateRetryDelayMs = 3_000;

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint LoadDefaultSize = 0x00000040;

    private const int WM_QUERYENDSESSION = 0x0011;
    private const int HWND_MESSAGE = -3;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string? windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static WindowProcDelegate? _shutdownWindowProc;
    private static IntPtr _shutdownWindow;

    public static void Start(bool showUi, bool showTrayUpdateHint = true)
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            _showUi = showUi;
            Volatile.Write(ref _trayUpdateHintEnabled, showTrayUpdateHint);
            if (_showUi) UpdateService.StateChanged += OnUpdateStateChanged;
            if (!WinUiUiHost.IsReady)
                Log.Warning("WinUI DispatcherQueue 尚未就绪，继续等待 UI 初始化");
            _ = InitializeOnUiThreadWhenReadyAsync();
        }
    }

    private static async Task InitializeOnUiThreadWhenReadyAsync()
    {
        try
        {
            var posted = await WinUiUiHost.PostWhenReadyAsync(() =>
            {
                try
                {
                    if (_showUi) BuildTrayIcon();
                    CreateShutdownMonitor();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "托盘/WinUI 初始化失败（引擎继续运行）");
                }
                finally
                {
                    UiReady.Set();
                }
            }, ShellWaitTimeoutMs).ConfigureAwait(false);

            if (!posted)
            {
                Log.Error("等待 WinUI 就绪失败，托盘/WinUI 宿主未初始化（引擎继续运行）");
                UiReady.Set();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "等待 WinUI 就绪时异常，托盘/WinUI 宿主未初始化（引擎继续运行）");
            UiReady.Set();
        }
    }

    public static void WaitForUiReady(int timeoutMs = 5000) => UiReady.Wait(timeoutMs);

    public static void Shutdown()
    {
        if (!WinUiUiHost.Post(() =>
        {
            try
            {
                if (_tray != null) _tray.MessageWindow.MouseEventReceived -= OnTrayMouseEvent;
                _tray?.Dispose();
            }
            catch { }
            _tray = null;
            _trayContextMenu = null;
            Volatile.Write(ref _trayCreated, false);
            if (_trayIconHandle != IntPtr.Zero)
            {
                try { DestroyIcon(_trayIconHandle); } catch { }
                _trayIconHandle = IntPtr.Zero;
            }
            if (_trayUpdateIconHandle != IntPtr.Zero)
            {
                try { DestroyIcon(_trayUpdateIconHandle); } catch { }
                _trayUpdateIconHandle = IntPtr.Zero;
            }
            _activeTrayIconHandle = IntPtr.Zero;
            if (_shutdownWindow != IntPtr.Zero)
            {
                try { DestroyWindow(_shutdownWindow); } catch { }
                _shutdownWindow = IntPtr.Zero;
            }
            WinUiUiHost.CloseAllOnUiThread();
            ShutdownComplete.Set();
        }))
        {
            ShutdownComplete.Set();
        }
    }

    public static void Join(TimeSpan timeout) => ShutdownComplete.Wait(timeout);

    public static void Notify(string title, string message)
    {
        if (!WinUiUiHost.Post(() =>
            {
                try { ShowNotification(title, message); }
                catch (Exception ex) { Log.Warning(ex, "托盘气泡显示失败（忽略）"); }
            }))
        {
            Log.Debug("托盘未就绪，忽略通知：{Title}", title);
        }
    }

    public static Task<string> PromptCaptchaAsync(string imagePath)
        => WinUiUiHost.PromptCaptchaAsync(imagePath);

    public static void SetTrayUpdateHintEnabled(bool enabled)
    {
        Volatile.Write(ref _trayUpdateHintEnabled, enabled);
        if (!WinUiUiHost.Post(ApplyCurrentTrayIcon))
            Log.Debug("WinUI UI 线程不可用，稍后会按更新提示设置刷新托盘图标");
    }

    private static void BuildTrayIcon()
    {
        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem("打开同步文件夹", (_, _) => OpenSyncFolder()));
        menu.Items.Add(new PopupMenuItem("打开日志目录", (_, _) => OpenLogFolder()));
        menu.Items.Add(new PopupMenuItem("清理已缓存文件", (_, _) => _ = ConfirmAndDehydrateCacheAsync()));
        menu.Items.Add(new PopupMenuItem("设置", (_, _) => OnSettingsMenuSelected()));
        _aboutMenuItem = new PopupMenuItem(
            UpdateService.CurrentState.IsAvailable ? "关于 - 可更新" : "关于",
            (_, _) => OnAboutMenuSelected());
        menu.Items.Add(_aboutMenuItem);
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem("退出", (_, _) => RequestExit()));

        _tray = new TrayIcon(ProductIdentity.DisplayNameZh)
        {
            ToolTip = ProductIdentity.DisplayNameZh,
        };
        _tray.MessageWindow.MouseEventReceived += OnTrayMouseEvent;
        _trayContextMenu = menu;

        var iconPath = ResolveTrayIconPath("yw_tray.ico");
        _trayIconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | LoadDefaultSize);
        if (_trayIconHandle == IntPtr.Zero)
            Log.Warning("加载托盘图标失败：{Path}（错误 {Error}）", iconPath, Marshal.GetLastWin32Error());

        var updateIconPath = ResolveTrayIconPath("yw_tray_update.ico");
        _trayUpdateIconHandle = LoadImage(IntPtr.Zero, updateIconPath, ImageIcon, 0, 0,
            LoadFromFile | LoadDefaultSize);
        if (_trayUpdateIconHandle == IntPtr.Zero)
            Log.Warning("加载更新提示托盘图标失败：{Path}（错误 {Error}）", updateIconPath, Marshal.GetLastWin32Error());

        ApplyTrayIcon(UpdateService.CurrentState);

        CreateTrayIconWhenShellReadyAsync();
    }

    private static string ResolveTrayIconPath(string fileName)
    {
        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        return File.Exists(assetPath)
            ? assetPath
            : Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static void ApplyCurrentTrayIcon() => ApplyTrayIcon(UpdateService.CurrentState);

    private static void ApplyTrayIcon(UpdateUiState state)
    {
        if (_tray is null) return;

        var iconHandle = Volatile.Read(ref _trayUpdateHintEnabled) && state.IsAvailable
            ? _trayUpdateIconHandle
            : _trayIconHandle;
        if (iconHandle == IntPtr.Zero) iconHandle = _trayIconHandle;
        if (iconHandle == IntPtr.Zero || iconHandle == _activeTrayIconHandle) return;

        try
        {
            if (_tray.IsCreated)
                _tray.UpdateIcon(iconHandle);
            else
                _tray.Icon = iconHandle;
            _activeTrayIconHandle = iconHandle;
        }
        catch (Exception error)
        {
            Log.Warning(error, "更新托盘图标失败");
        }
    }

    private static void CreateTrayIconWhenShellReadyAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                while (!IsShellReady())
                {
                    if (stopwatch.ElapsedMilliseconds >= ShellWaitTimeoutMs)
                    {
                        Log.Warning("等待 Windows Shell 就绪超时（{Sec}s），放弃创建托盘图标", ShellWaitTimeoutMs / 1000);
                        return;
                    }
                    await Task.Delay(ShellPollIntervalMs).ConfigureAwait(false);
                }

                await Task.Delay(ShellReadyGraceMs).ConfigureAwait(false);
                for (var attempt = 1; attempt <= TrayCreateMaxAttempts; attempt++)
                {
                    if (await TryCreateOnUiThreadAsync().ConfigureAwait(false)) return;
                    if (attempt < TrayCreateMaxAttempts)
                        await Task.Delay(TrayCreateRetryDelayMs).ConfigureAwait(false);
                }
                Log.Error("托盘图标创建失败（已重试 {Count} 次）", TrayCreateMaxAttempts);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "托盘图标创建流程异常（引擎继续运行）");
            }
        });
    }

    private static Task<bool> TryCreateOnUiThreadAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!WinUiUiHost.Post(() =>
            {
                try
                {
                    if (_tray == null) { completion.TrySetResult(false); return; }
                    _tray.Create();
                    Volatile.Write(ref _trayCreated, true);
                    Log.Information("托盘图标已创建（原生 Win32 PopupMenu 模式）");
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "托盘图标创建失败");
                    completion.TrySetResult(false);
                }
            }))
        {
            completion.TrySetResult(false);
        }
        return completion.Task;
    }

    private static bool IsShellReady()
    {
        try
        {
            if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero) return false;
            var sessionId = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                using (process)
                {
                    try { if (process.SessionId == sessionId) return true; }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }

    private static void CreateShutdownMonitor()
    {
        try
        {
            _shutdownWindowProc = ShutdownWindowProc;
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(_shutdownWindowProc),
                ClassName = ProductIdentity.ShutdownWindowClassName,
                Instance = GetModuleHandle(null),
            };
            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                Log.Warning("关机监听窗口注册失败（错误 {Error}）", Marshal.GetLastWin32Error());
                return;
            }
            _shutdownWindow = CreateWindowEx(0, windowClass.ClassName, null, 0, 0, 0, 0, 0,
                new IntPtr(HWND_MESSAGE), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_shutdownWindow == IntPtr.Zero)
                Log.Warning("关机监听窗口创建失败（错误 {Error}）", Marshal.GetLastWin32Error());
            else
                Log.Information("已创建关机监听窗口");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关机监听窗口建立异常");
        }
    }

    private static IntPtr ShutdownWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_QUERYENDSESSION)
        {
            Program.RequestShutdownFromUi();
            return new IntPtr(1);
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void ShowNotification(string title, string message)
    {
        _tray?.ShowNotification(title, message, NotificationIcon.Info, null,
            largeIcon: false, sound: true, respectQuietTime: false, realtime: false, timeout: null);
    }

    private static void OnTrayMouseEvent(object? sender, MessageWindow.MouseEventReceivedEventArgs args)
    {
        if (args.MouseEvent != MouseEvent.IconRightMouseUp || _trayContextMenu == null || _tray == null)
            return;

        try
        {
            if (!GetCursorPos(out var point)) return;
            SetForegroundWindow(_tray.WindowHandle);
            _trayContextMenu.Show(_tray.WindowHandle, point.X, point.Y);
            _tray.SetFocus();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "托盘菜单显示失败（忽略）");
        }
    }

    private static void OpenSyncFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Program.SyncRootPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "打开同步文件夹失败"); }
    }

    private static void OpenLogFolder()
    {
        try
        {
            var directory = Program.LogDirectory;
            if (!Directory.Exists(directory)) directory = AppContext.BaseDirectory;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "打开日志目录失败"); }
    }

    private static void DehydrateCache()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var result = PlaceholderUpdater.DehydrateTree(Program.SyncRootPath,
                    message => Log.Information("{Message}", message));
                var megabytes = result.FreedBytes / 1024.0 / 1024.0;
                Notify("缓存清理完成", $"还原 {result.Dehydrated} 个文件，跳过 {result.Skipped} 个，释放 {megabytes:F1} MB");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "本地缓存脱水失败");
                Notify("缓存清理失败", ex.Message);
            }
        });
    }

    private static async Task ConfirmAndDehydrateCacheAsync()
    {
        try
        {
            if (await WinUiUiHost.ConfirmCacheCleanupAsync())
                DehydrateCache();
        }
        catch (Exception ex) { Log.Error(ex, "缓存清理确认对话框失败"); }
    }

    private static void OpenSettings()
    {
        try { WinUiUiHost.ShowSettings(Program.SnapshotConfig()); }
        catch (Exception ex)
        {
            Log.Error(ex, "打开设置窗口失败");
            Notify("设置打开失败", ex.Message);
        }
    }

    private static void OnSettingsMenuSelected() => OpenSettings();

    private static void OnAboutMenuSelected() => WinUiUiHost.ShowAbout();

    private static void OnUpdateStateChanged(UpdateUiState state)
    {
        if (!WinUiUiHost.Post(() =>
            {
                if (_aboutMenuItem is not null)
                    _aboutMenuItem.Text = state.IsAvailable ? "关于 - 可更新" : "关于";
                ApplyTrayIcon(state);
            }))
            Log.Debug("托盘 UI 尚不可用，稍后会按当前更新状态建立菜单项");
    }

    internal static void OpenSettingsForSelfTest() => OnSettingsMenuSelected();

    private static void RequestExit() => Program.RequestShutdownFromUi();
}
