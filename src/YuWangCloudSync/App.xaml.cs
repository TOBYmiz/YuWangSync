using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using WinRT.Interop;
using YuWangCloudSync.Core;
using YuWangCloudSync.Tray;

namespace YuWangCloudSync;

public partial class App : Application
{
    internal static string[] LaunchArguments { get; set; } = [];
    private Task? _runner;
    private Window? _dispatcherLifetimeWindow;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    public App()
    {
        Program.SetStartupStage("注册 WinUI 未处理异常处理器");
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            Program.HandleFatalException(args.Exception, "WinUI UI 线程未处理异常");
            try { Serilog.Log.CloseAndFlush(); } catch { }
            Environment.Exit(1);
        };
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, args) =>
        {
            try { Serilog.Log.Error("WinUI XAML 资源引用失败：{Message}", args.Message); } catch { }
        };
        Program.SetStartupStage("加载 WinUI App.xaml");
        InitializeComponent();
        Program.SetStartupStage("WinUI App.xaml 加载完成");
    }

    private static void ValidateFluentResources(ResourceDictionary resources)
    {
        if (!resources.MergedDictionaries.Any(dictionary => dictionary is XamlControlsResources))
            throw new InvalidOperationException("App.xaml 未加载 WinUI XamlControlsResources 默认控件资源字典。");

        string[] requiredStyles =
        [
            "DefaultButtonStyle",
            "DefaultTextBoxStyle",
            "DefaultCheckBoxStyle",
            "SubtitleTextBlockStyle",
            "YuWangCaptionTextBlockStyle",
            "YuWangCardStyle",
            "YuWangImageFrameStyle",
        ];
        var missing = requiredStyles.Where(key => !resources.ContainsKey(key)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException(
                "App.xaml 未加载完整的 WinUI 样式资源: " + string.Join(", ", missing));

        Serilog.Log.Information("WinAppSDK XamlControlsResources 默认主题与控件样式已加载：{StyleCount} 个必需样式",
            requiredStyles.Length);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Program.SetStartupStage("WinUI OnLaunched");
        Program.SetStartupStage("校验 Fluent 资源字典");
        try
        {
            // Application.Resources is available after WinUI completes app construction and
            // dispatches OnLaunched; querying this.Resources from App's constructor can throw E_UNEXPECTED.
            ValidateFluentResources(Application.Current.Resources);
        }
        catch (Exception error)
        {
            Program.HandleFatalException(error, "校验 Fluent 资源字典");
            try { Serilog.Log.CloseAndFlush(); } catch { }
            Environment.Exit(1);
            return;
        }
        Program.SetStartupStage("Fluent 资源字典校验完成");
        WinUiUiHost.Initialize();
        // WinUI exits when its last Window closes. Keep an unactivated hidden Window alive so the
        // DispatcherQueue continues serving tray callbacks and on-demand settings/captcha windows.
        _dispatcherLifetimeWindow = new Window { Title = ProductIdentity.DisplayNameZh };
        _ = ShowWindow(WindowNative.GetWindowHandle(_dispatcherLifetimeWindow), 0); // SW_HIDE
        // WinUI owns the process STA thread. The synchronization engine and its CFAPI work stay on MTA.
        _runner = Task.Run(() => Program.RunApplicationAsync(LaunchArguments));
        _ = ExitWhenFinishedAsync(_runner);
    }

    private async Task ExitWhenFinishedAsync(Task runner)
    {
        try { await runner.ConfigureAwait(false); }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            Program.HandleFatalException(error, "主程序后台流程意外终止");
        }

        WinUiUiHost.Post(Exit);
    }
}
