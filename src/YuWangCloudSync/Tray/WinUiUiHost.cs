using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;
using YuWangCloudSync.Models;
using VirtualKey = Windows.System.VirtualKey;

namespace YuWangCloudSync.Tray;

internal sealed record UiSelfTestCheck(string Name, bool Passed, string Detail);

/// <summary>WinUI STA dispatcher and the application's on-demand settings/captcha windows.</summary>
internal static class WinUiUiHost
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private static readonly UIntPtr WindowSizeSubclassId = new(1);
    private static readonly Dictionary<IntPtr, (int WidthDip, int HeightDip)> MinimumWindowSizes = [];
    private static readonly SubclassProc MinimumSizeSubclass = EnforceMinimumWindowSize;

    private sealed record CaptchaRequest(string ImagePath, TaskCompletionSource<string> Completion);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaxSize;
        internal NativePoint MaxPosition;
        internal NativePoint MinTrackSize;
        internal NativePoint MaxTrackSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window, uint message, UIntPtr wParam,
        IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    private static readonly ManualResetEventSlim Ready = new(false);
    private static readonly Queue<CaptchaRequest> CaptchaQueue = new();
    private static DispatcherQueue? _dispatcher;
    private static SettingsWindow? _settings;
    private static AccountManagementWindow? _accountManager;
    private static AboutWindow? _about;
    private static CaptchaWindow? _captcha;
    private static CacheCleanupConfirmationWindow? _cacheCleanupConfirmation;
    private static int _uiThreadId;

    internal static bool IsReady => Ready.IsSet && _dispatcher is not null;
    internal static int UiThreadId => _uiThreadId;
    internal static bool IsSettingsWindowOpen => _settings is not null;

    internal static void Initialize()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinUI DispatcherQueue 未在应用 STA 线程创建。");
        _uiThreadId = Environment.CurrentManagedThreadId;
        Ready.Set();
    }

    internal static bool Post(Action action)
    {
        if (!Ready.IsSet || _dispatcher is null) return false;
        return _dispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception error) { Serilog.Log.Error(error, "WinUI UI 操作失败"); }
        });
    }

    internal static async Task<bool> PostWhenReadyAsync(Action action, int timeoutMs)
    {
        if (!await Task.Run(() => Ready.Wait(timeoutMs)).ConfigureAwait(false)) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception error) { completion.TrySetException(error); }
            })) return false;
        try { return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    internal static Task<string> PromptCaptchaAsync(string imagePath)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                CaptchaQueue.Enqueue(new CaptchaRequest(imagePath, completion));
                ShowNextCaptcha();
            }))
        {
            completion.TrySetException(new InvalidOperationException("WinUI UI 线程不可用，无法显示验证码窗口。"));
        }
        return completion.Task;
    }

    internal static void ShowSettings(SyncRootContext snapshot)
    {
        if (!Post(() => ShowSettingsOnUi(snapshot)))
            Serilog.Log.Warning("WinUI UI 线程不可用，无法打开设置窗口");
    }

    internal static void ShowAbout()
    {
        if (!Post(ShowAboutOnUi))
            Serilog.Log.Warning("WinUI UI 线程不可用，无法打开关于窗口");
    }

    internal static async Task<bool> ConfirmCacheCleanupAsync()
    {
        var queued = new TaskCompletionSource<CacheCleanupConfirmationWindow?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
            {
                try
                {
                    var window = _cacheCleanupConfirmation;
                    if (window is null)
                    {
                        window = new CacheCleanupConfirmationWindow();
                        _cacheCleanupConfirmation = window;
                        window.Closed += (_, _) =>
                        {
                            if (ReferenceEquals(_cacheCleanupConfirmation, window))
                                _cacheCleanupConfirmation = null;
                        };
                        window.Activate();
                    }
                    else
                    {
                        ActivateWindow(window);
                    }
                    queued.TrySetResult(window);
                }
                catch (Exception error)
                {
                    Serilog.Log.Error(error, "创建缓存清理确认窗口失败");
                    queued.TrySetResult(null);
                }
            }))
        {
            Serilog.Log.Warning("WinUI UI 线程不可用，无法确认缓存清理");
            return false;
        }

        var confirmation = await queued.Task.ConfigureAwait(false);
        return confirmation is not null && await confirmation.Completion.ConfigureAwait(false);
    }

    private static void ShowAboutOnUi()
    {
        if (_about is not null)
        {
            ActivateWindow(_about);
            _about.OnEnteredAbout();
            return;
        }

        var window = new AboutWindow();
        _about = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_about, window)) _about = null;
        };
        window.Activate();
        window.OnEnteredAbout();
    }

    private static SettingsWindow ShowSettingsOnUi(SyncRootContext snapshot, bool activate = true)
    {
        if (_settings is not null)
        {
            ActivateWindow(_settings);
            return _settings;
        }

        var window = new SettingsWindow(snapshot);
        _settings = window;
        window.ApplyRequested += (edit, closeOnSuccess) =>
            ApplySettingsInBackground(window, edit, closeOnSuccess);
        window.AccountSwitchRequested += Program.RequestAccountSwitch;
        window.AccountManagementRequested += ShowAccountManagementOnUi;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settings, window)) _settings = null;
        };
        if (activate) window.Activate();
        return window;
    }

    private static void ShowAccountManagementOnUi(SettingsWindow owner)
    {
        if (!ReferenceEquals(_settings, owner) || owner.IsClosed) return;
        if (_accountManager is not null)
        {
            ActivateWindow(_accountManager);
            return;
        }

        var ownerHandle = WindowNative.GetWindowHandle(owner);
        if (!IsWindowEnabled(ownerHandle)) return;
        EnableWindow(ownerHandle, false);
        if (IsWindowEnabled(ownerHandle))
            throw new InvalidOperationException("无法锁定设置窗口，账户管理窗口未打开。");

        try
        {
            var window = new AccountManagementWindow(owner.AccountSource,
                owner.GetAccountsSnapshot(), owner.SelectedAccountName, owner.UpdateAccountsFromManager);
            _accountManager = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_accountManager, window)) _accountManager = null;
                EnableWindow(ownerHandle, true);
                if (!owner.IsClosed) ActivateWindow(owner);
            };
            window.Activate();
        }
        catch
        {
            _accountManager = null;
            EnableWindow(ownerHandle, true);
            if (!owner.IsClosed) ActivateWindow(owner);
            throw;
        }
    }

    private static void ShowNextCaptcha()
    {
        if (_captcha is not null || CaptchaQueue.Count == 0) return;
        var request = CaptchaQueue.Dequeue();
        try
        {
            var window = new CaptchaWindow(request.ImagePath);
            _captcha = window;
            window.Closed += (_, _) =>
            {
                if (window.WasCanceled) request.Completion.TrySetCanceled();
                else request.Completion.TrySetResult(window.Result);
                if (ReferenceEquals(_captcha, window)) _captcha = null;
                Post(ShowNextCaptcha);
            };
            window.Activate();
        }
        catch (Exception error)
        {
            request.Completion.TrySetException(error);
            Serilog.Log.Error(error, "显示 WinUI 验证码窗口失败");
            ShowNextCaptcha();
        }
    }

    private static void ApplySettingsInBackground(SettingsWindow window, SyncRootContext edit, bool closeOnSuccess)
    {
        _ = Task.Run(async () =>
        {
            bool ok;
            string message;
            try
            {
                (ok, message) = await Program.ApplySettingsAsync(edit).ConfigureAwait(false);
                Serilog.Log.Information("[SETTINGS] 应用结果: ok={Ok} msg={Message}", ok, message);
            }
            catch (Exception error)
            {
                Serilog.Log.Error(error, "应用设置异常");
                ok = false;
                message = error.Message;
            }

            if (!Post(() => _ = window.CompleteApplyAsync(ok, message, closeOnSuccess)))
                Serilog.Log.Warning("WinUI UI 线程不可用，无法显示设置应用结果");
        });
    }

    internal static void CloseAll()
    {
        Post(CloseAllOnUiThread);
    }

    internal static void CloseAllOnUiThread()
    {
        try { _accountManager?.Close(); } catch { }
        try { _settings?.Close(); } catch { }
        try { _about?.Close(); } catch { }
        try { _captcha?.Close(); } catch { }
        try { _cacheCleanupConfirmation?.Close(); } catch { }
        _settings = null;
        _accountManager = null;
        _about = null;
        _captcha = null;
        _cacheCleanupConfirmation = null;
        while (CaptchaQueue.TryDequeue(out var request)) request.Completion.TrySetResult("");
    }

    internal static async Task<IReadOnlyList<UiSelfTestCheck>> RunSelfTestAsync(
        string captchaPath, string testRoot, int holdSeconds, bool saveShots, bool probeFolderPicker)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<UiSelfTestCheck>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(async () =>
            {
                try { completion.TrySetResult(await RunSelfTestOnUiAsync(captchaPath, testRoot,
                    holdSeconds, saveShots, probeFolderPicker)); }
                catch (Exception error)
                {
                    completion.TrySetResult([new UiSelfTestCheck("WinUI UI host", false, error.ToString())]);
                }
            }))
        {
            return [new UiSelfTestCheck("WinUI dispatcher", false, "DispatcherQueue 未就绪或已停止。")];
        }
        return await completion.Task.ConfigureAwait(false);
    }

    private static async Task<bool> CloseFolderPickerProbeAsync(string title)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var dialog = FindWindowW(null, title);
            if (dialog != IntPtr.Zero)
                return PostMessageW(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            await Task.Delay(50).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<IReadOnlyList<UiSelfTestCheck>> RunSelfTestOnUiAsync(
        string captchaPath, string testRoot, int holdSeconds, bool saveShots, bool probeFolderPicker)
    {
        var checks = new List<UiSelfTestCheck>();
        void Check(string name, bool passed, string detail) => checks.Add(new(name, passed, detail));

        Check("WinUI STA dispatcher", Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && Environment.CurrentManagedThreadId == _uiThreadId,
            $"UI thread={Environment.CurrentManagedThreadId}; expected STA thread={_uiThreadId}");

        var configPath = Path.Combine(testRoot, "appsettings.selftest.json");
        var temporaryConfigIsolated = File.Exists(configPath)
            && !Directory.Exists(Program.SnapshotConfig().SyncRootPath);
        Check("Temporary config isolation", temporaryConfigIsolated,
            $"Config={configPath}; fake SyncRoot was not created.");

        TrayHost.Start(showUi: true, showTrayUpdateHint: Program.SnapshotConfig().TrayUpdateHintEnabled);
        await Task.Run(() => TrayHost.WaitForUiReady(15_000));
        var trayDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < trayDeadline && !TrayHost.IsTrayCreated)
            await Task.Delay(100);
        Check("Native Win32 tray", TrayHost.IsTrayCreated,
            TrayHost.IsTrayCreated ? "H.NotifyIcon.Core TrayIcon/PopupMenu created." : "Tray icon did not become ready within 15 seconds.");

        var fakeConfig = Program.SnapshotConfig();

        var settings = ShowSettingsOnUi(fakeConfig, activate: false);
        settings.Activate();
        await settings.LoadedTask.WaitAsync(TimeSpan.FromSeconds(10));
        var settingsHandle = WindowNative.GetWindowHandle(settings);
        Check("Settings WinUI window", settings.Root.XamlRoot is not null && settings.Root.ActualWidth > 0
            && settings.Root.ActualHeight > 0 && IsWindowVisible(settingsHandle),
            $"XamlRoot={settings.Root.XamlRoot is not null}; size={settings.Root.ActualWidth:0}x{settings.Root.ActualHeight:0}; HWND=0x{settingsHandle:X}");
        Check("Per-monitor V2 DPI awareness", AreDpiAwarenessContextsEqual(
                GetWindowDpiAwarenessContext(settingsHandle), new IntPtr(-4)),
            "Windows must recognize the window as PerMonitorV2 rather than bitmap-scaling a DPI-unaware window.");
        var settingsDpi = GetDpiForWindow(settingsHandle);
        var settingsScale = settings.Root.XamlRoot?.RasterizationScale ?? 0;
        Check("Settings DPI rasterization", settingsDpi > 0 && Math.Abs(settingsScale - settingsDpi / 96d) < 0.01,
            $"Window DPI={settingsDpi}; XAML scale={settingsScale:0.###}.");
        Check("System-themed settings title bar", WinUiWindowChrome.IsConfigured(settings)
            && settings.Root.RequestedTheme == ElementTheme.Default,
            "Mica custom caption area and WinUI controls follow the Windows theme through ElementTheme.Default.");

        var duplicate = ShowSettingsOnUi(fakeConfig);
        Check("Settings single instance", ReferenceEquals(settings, duplicate), "Repeated request activates the existing settings window.");
        Check("Settings validation", settings.ValidateForSelfTest(),
            "Invalid TypeId is rejected by the same value validation used before applying settings.");
        if (probeFolderPicker)
        {
            const string title = "YuWangCloudSync NativeAOT folder picker probe";
            var closeDialogTask = CloseFolderPickerProbeAsync(title);
            var pickedFolder = await FolderPicker.PickAsync(settingsHandle, title, testRoot);
            var dialogShown = await closeDialogTask.ConfigureAwait(true);
            Check("Folder picker COM dialog", dialogShown && string.IsNullOrEmpty(pickedFolder),
                $"DialogShown={dialogShown}; selection is empty after the probe cancels it: {string.IsNullOrEmpty(pickedFolder)}.");
        }
        checks.AddRange(await CheckSystemControlStylesAsync(settings.Root));
        ShowWindow(settingsHandle, 6); // SW_MINIMIZE
        await Task.Delay(120);
        Check("Title bar skips minimized window", !WinUiWindowChrome.CanUpdateTitleBar(settings),
            "Native title bar and inset refreshes are gated while the window is minimized.");
        ActivateWindow(settings);
        await Task.Delay(120);
        Check("Title bar resumes after restore", WinUiWindowChrome.CanUpdateTitleBar(settings),
            "Title bar refresh resumes after the window returns to the foreground.");
        var backgroundProbe = new CaptchaWindow(captchaPath);
        backgroundProbe.Activate();
        await backgroundProbe.LoadedTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check("Title bar skips background window", !WinUiWindowChrome.CanUpdateTitleBar(settings)
            && WinUiWindowChrome.CanUpdateTitleBar(backgroundProbe),
            "Only the foreground WinUI window is eligible for title bar refresh.");
        backgroundProbe.CancelForSelfTest();
        await Task.Delay(100);
        ActivateWindow(settings);
        await Task.Delay(100);
        if (saveShots) await SaveWindowPngAsync(settings.Root, "settings", testRoot);
        if (holdSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        settings.Close();
        await Task.Delay(100);

        TrayHost.OpenSettingsForSelfTest();
        var settingsDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < settingsDeadline && !IsSettingsWindowOpen)
            await Task.Delay(50);
        Check("Tray settings callback", IsSettingsWindowOpen,
            "The Win32 PopupMenu settings item opens the WinUI settings window.");
        try { _settings?.Close(); } catch { }

        var captchaCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captcha = new CaptchaWindow(captchaPath);
        captcha.Closed += (_, _) => captchaCompletion.TrySetResult(captcha.Result);
        captcha.Activate();
        var captchaImageLoaded = await captcha.LoadedTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check("Captcha image loaded", captchaImageLoaded && captcha.HasImageSource,
            $"Image source set from temporary file: {captcha.HasImageSource}.");
        var captchaHandle = WindowNative.GetWindowHandle(captcha);
        Check("Captcha WinUI window", captcha.Root.XamlRoot is not null && captcha.Root.ActualWidth > 0
            && IsWindowVisible(captchaHandle),
            $"XamlRoot={captcha.Root.XamlRoot is not null}; size={captcha.Root.ActualWidth:0}x{captcha.Root.ActualHeight:0}; HWND=0x{captchaHandle:X}");
        var captchaDpi = GetDpiForWindow(captchaHandle);
        var captchaScale = captcha.Root.XamlRoot?.RasterizationScale ?? 0;
        Check("Captcha DPI rasterization", captchaDpi > 0 && Math.Abs(captchaScale - captchaDpi / 96d) < 0.01,
            $"Window DPI={captchaDpi}; XAML scale={captchaScale:0.###}.");
        Check("System-themed captcha title bar", WinUiWindowChrome.IsConfigured(captcha)
            && captcha.Root.RequestedTheme == ElementTheme.Default,
            "Captcha caption area uses Mica and WinUI system theme resources.");
        checks.AddRange(await CheckSystemControlStylesAsync(captcha.Root));
        if (saveShots) await SaveWindowPngAsync(captcha.Root, "captcha", testRoot);
        captcha.AcceptForSelfTest("Ab12");
        var acceptedCode = await captchaCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("Captcha submit result", acceptedCode == "Ab12", $"Returned code: '{acceptedCode}'.");

        var canceledCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledCaptcha = new CaptchaWindow(captchaPath);
        canceledCaptcha.Closed += (_, _) => canceledCompletion.TrySetResult(canceledCaptcha.Result);
        canceledCaptcha.Activate();
        await canceledCaptcha.LoadedTask.WaitAsync(TimeSpan.FromSeconds(10));
        canceledCaptcha.CancelForSelfTest();
        var canceledCode = await canceledCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("Captcha cancel result", canceledCode.Length == 0, "Cancel/close returns an empty string.");

        TrayHost.Shutdown();
        await Task.Run(() => TrayHost.Join(TimeSpan.FromSeconds(3)));
        return checks;
    }

    private static async Task<IReadOnlyList<UiSelfTestCheck>> CheckSystemControlStylesAsync(Grid root)
    {
        static IEnumerable<Control> DescendantControls(UIElement parent)
        {
            if (parent is Button or TextBox or CheckBox)
            {
                yield return (Control)parent;
                yield break;
            }
            // Traverse application content, excluding controls inside WinUI templates.
            IEnumerable<UIElement> children = parent switch
            {
                Panel panel => panel.Children,
                Border { Child: { } child } => [child],
                ScrollViewer { Content: UIElement child } => [child],
                _ => [],
            };
            foreach (var child in children)
            {
                foreach (var descendant in DescendantControls(child)) yield return descendant;
            }
        }
        static bool SameBrush(Brush? left, Brush? right) => Equals(left, right)
            || (left is SolidColorBrush a && right is SolidColorBrush b
                && a.Color == b.Color && a.Opacity == b.Opacity);

        // Explicit stock styles provide a reference independent of any app implicit style.
        var references = new Control[]
        {
            new Button { Style = (Style)Application.Current.Resources["DefaultButtonStyle"] },
            new TextBox { Style = (Style)Application.Current.Resources["DefaultTextBoxStyle"] },
            new CheckBox { Style = (Style)Application.Current.Resources["DefaultCheckBoxStyle"] },
        };
        var controls = DescendantControls(root).Where(control => control is Button or TextBox or CheckBox).ToArray();
        var referenceHost = new StackPanel { Opacity = 0, IsHitTestVisible = false };
        foreach (var reference in references) referenceHost.Children.Add(reference);
        root.Children.Add(referenceHost);
        var originalTheme = root.RequestedTheme;
        var checks = new List<UiSelfTestCheck>();
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                root.UpdateLayout();
                await Task.Delay(100);
                var mismatches = new List<string>();
                foreach (var control in controls)
                {
                    var reference = references.Single(candidate => candidate.GetType() == control.GetType());
                    if (!SameBrush(control.Background, reference.Background)
                        || !SameBrush(control.Foreground, reference.Foreground)
                        || !SameBrush(control.BorderBrush, reference.BorderBrush)
                        || control.CornerRadius != reference.CornerRadius
                        || control.BorderThickness != reference.BorderThickness
                        || control.Padding != reference.Padding
                        || control.FontSize != reference.FontSize
                        || control.FontFamily.Source != reference.FontFamily.Source
                        || control.MinHeight != reference.MinHeight
                        || !double.IsNaN(control.Height))
                        mismatches.Add(string.IsNullOrEmpty(control.Name) ? control.GetType().Name : control.Name);
                }
                checks.Add(new UiSelfTestCheck($"{root.Name} stock controls ({theme})", controls.Length > 0 && mismatches.Count == 0,
                    mismatches.Count == 0
                        ? $"{controls.Length} controls match stock brushes, border, radius, padding, typography and automatic height."
                        : "Differ from stock style: " + string.Join(", ", mismatches)));
            }
        }
        finally
        {
            root.RequestedTheme = originalTheme;
            root.Children.Remove(referenceHost);
        }
        return checks;
    }

    private static async Task SaveWindowPngAsync(FrameworkElement element, string name, string testRoot)
    {
        var renderer = new RenderTargetBitmap();
        await renderer.RenderAsync(element);
        var pixelBuffer = await renderer.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(pixelBuffer);
        var pixels = new byte[pixelBuffer.Length];
        reader.ReadBytes(pixels);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            checked((uint)renderer.PixelWidth), checked((uint)renderer.PixelHeight), 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var outputReader = new DataReader(stream.GetInputStreamAt(0));
        await outputReader.LoadAsync((uint)stream.Size);
        var bytes = new byte[checked((int)stream.Size)];
        outputReader.ReadBytes(bytes);
        var path = Path.Combine(testRoot, $"{name}.png");
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static void ActivateWindow(Window window)
    {
        try
        {
            var handle = WindowNative.GetWindowHandle(window);
            _ = ShowWindow(handle, 9); // SW_RESTORE
            window.Activate();
            _ = SetForegroundWindow(handle);
        }
        catch (Exception error) { Serilog.Log.Warning(error, "激活 WinUI 窗口失败"); }
    }

    internal static void ConfigureWindow(Window window, int widthDip, int heightDip, bool resizable,
        int minimumWidthDip = 0, int minimumHeightDip = 0)
    {
        var handle = WindowNative.GetWindowHandle(window);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
        }

        if (minimumWidthDip > 0 || minimumHeightDip > 0)
            SetMinimumWindowSize(window, handle, minimumWidthDip, minimumHeightDip);

        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinUI DispatcherQueue 未在设置窗口创建时就绪。");
        uint configuredDpi = GetDpiForSystem();
        if (configuredDpi == 0) configuredDpi = 96;
        var hasActivated = false;
        ApplyWindowBounds(window, widthDip, heightDip, configuredDpi, minimumWidthDip, minimumHeightDip);

        // A newly created HWND can report 96 DPI before Windows assigns it to a monitor.
        // Recheck after activation, then keep the requested DIP size when it crosses monitors.
        window.Activated += OnActivated;
        window.AppWindow.Changed += (_, args) =>
        {
            if (!hasActivated || (!args.DidPositionChange && !args.DidSizeChange)) return;
            dispatcher.TryEnqueue(() =>
            {
                var dpi = GetDpiForWindow(handle);
                if (dpi == 0) dpi = GetDpiForSystem();
                if (dpi == 0 || dpi == configuredDpi) return;
                configuredDpi = dpi;
                ApplyWindowBounds(window, widthDip, heightDip, configuredDpi, minimumWidthDip, minimumHeightDip);
            });
        };

        void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            window.Activated -= OnActivated;
            hasActivated = true;
            dispatcher.TryEnqueue(() =>
            {
                var dpi = GetDpiForWindow(handle);
                if (dpi == 0) dpi = GetDpiForSystem();
                if (dpi == 0 || dpi == configuredDpi) return;
                configuredDpi = dpi;
                ApplyWindowBounds(window, widthDip, heightDip, configuredDpi, minimumWidthDip, minimumHeightDip);
            });
        }
    }

    private static void SetMinimumWindowSize(Window window, IntPtr handle,
        int minimumWidthDip, int minimumHeightDip)
    {
        MinimumWindowSizes[handle] = (minimumWidthDip, minimumHeightDip);
        if (!SetWindowSubclass(handle, MinimumSizeSubclass, WindowSizeSubclassId, UIntPtr.Zero))
        {
            MinimumWindowSizes.Remove(handle);
            throw new InvalidOperationException("无法设置窗口最小尺寸约束。",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        window.Closed += (_, _) =>
        {
            _ = RemoveWindowSubclass(handle, MinimumSizeSubclass, WindowSizeSubclassId);
            MinimumWindowSizes.Remove(handle);
        };
    }

    private static IntPtr EnforceMinimumWindowSize(IntPtr window, uint message, UIntPtr wParam,
        IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero
            && MinimumWindowSizes.TryGetValue(window, out var minimum))
        {
            try
            {
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var dpi = GetDpiForWindow(window);
                if (dpi == 0) dpi = GetDpiForSystem();
                if (dpi == 0) dpi = 96;
                var scale = dpi / 96d;
                if (minimum.WidthDip > 0)
                    info.MinTrackSize.X = Math.Max(info.MinTrackSize.X,
                        (int)Math.Round(minimum.WidthDip * scale));
                if (minimum.HeightDip > 0)
                    info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y,
                        (int)Math.Round(minimum.HeightDip * scale));
                Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            }
            catch (Exception error)
            {
                Serilog.Log.Warning(error, "应用窗口最小尺寸约束失败");
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static void ApplyWindowBounds(Window window, int widthDip, int heightDip, uint dpi,
        int minimumWidthDip, int minimumHeightDip)
    {
        var scale = dpi / 96d;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)Math.Round(48 * scale);
        var availableWidth = Math.Max(1, area.Width - margin);
        var availableHeight = Math.Max(1, area.Height - margin);
        var requestedWidth = (int)Math.Round(widthDip * scale);
        var requestedHeight = (int)Math.Round(heightDip * scale);
        var minimumWidth = minimumWidthDip > 0 ? (int)Math.Round(minimumWidthDip * scale) : 1;
        var minimumHeight = minimumHeightDip > 0 ? (int)Math.Round(minimumHeightDip * scale) : 1;
        var width = Math.Max(minimumWidth, Math.Min(requestedWidth, availableWidth));
        var height = Math.Max(minimumHeight, Math.Min(requestedHeight, availableHeight));
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc subclassProc,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc subclassProc, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
