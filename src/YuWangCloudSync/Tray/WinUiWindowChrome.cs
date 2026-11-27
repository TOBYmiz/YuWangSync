using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Color = Windows.UI.Color;

namespace YuWangCloudSync.Tray;

/// <summary>Shared system-themed custom caption area for the on-demand WinUI windows.</summary>
internal static class WinUiWindowChrome
{
    private sealed class State
    {
        internal required Window Window { get; init; }
        internal required FrameworkElement ThemeRoot { get; init; }
        internal required ColumnDefinition LeftInset { get; init; }
        internal required ColumnDefinition RightInset { get; init; }
        internal required IntPtr Handle { get; init; }
        internal required DispatcherQueue Dispatcher { get; init; }
        internal bool Closed;
        internal (bool Dark, bool HighContrast)? CaptionPalette;
    }

    private static readonly Dictionary<Window, State> States = new();
    private static UISettings? _uiSettings;
    internal static event Action? SystemColorsChanged;

    private static void ObserveSystemColors(DispatcherQueue dispatcher)
    {
        if (_uiSettings is not null) return;
        _uiSettings = new UISettings();
        _uiSettings.ColorValuesChanged += (_, _) => dispatcher.TryEnqueue(() =>
        {
            foreach (var state in States.Values) EnqueueRefresh(state, updateInsets: false);
            SystemColorsChanged?.Invoke();
        });
    }

    internal static bool IsHighContrast()
    {
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        return SystemParametersInfoW(0x0042 /* SPI_GETHIGHCONTRAST */, contrast.Size, ref contrast, 0)
            && (contrast.Flags & 1 /* HCF_HIGHCONTRASTON */) != 0;
    }

    internal static Grid Attach(Window window, FrameworkElement content, string title, string glyph = "\uE753")
    {
        var titleBar = new Grid
        {
            Name = "AppTitleBar",
            Height = 48,
            // Transparent content exposes the Mica system backdrop beneath this custom caption area.
            Background = new SolidColorBrush(Colors.Transparent),
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        var insetColumns = titleBar.ColumnDefinitions;
        var titleContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        titleContent.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        titleContent.Children.Add(new TextBlock
        {
            Text = title,
            Style = WinUiUiHost.GetStyleResource("CaptionTextBlockStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(titleContent, 1);
        titleBar.Children.Add(titleContent);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(titleBar);
        Grid.SetRow(content, 1);
        layout.Children.Add(content);
        window.Content = layout;
        window.ExtendsContentIntoTitleBar = true;
        window.SystemBackdrop = new MicaBackdrop();
        window.SetTitleBar(titleBar);

        if (AppWindowTitleBar.IsCustomizationSupported())
            window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinUI DispatcherQueue 未在标题栏接管时创建。");
        var state = new State
        {
            Window = window,
            ThemeRoot = layout,
            LeftInset = insetColumns[0],
            RightInset = insetColumns[2],
            Handle = handle,
            Dispatcher = dispatcher,
        };
        States.Add(window, state);
        ObserveSystemColors(dispatcher);

        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                EnqueueRefresh(state, updateInsets: true);
        };
        layout.ActualThemeChanged += (_, _) => EnqueueRefresh(state, updateInsets: false);
        window.AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
                EnqueueRefresh(state, updateInsets: true);
        };
        window.Closed += (_, _) =>
        {
            state.Closed = true;
            States.Remove(window);
        };

        // The WinUI root keeps ElementTheme.Default. WinUI resources and Mica follow the
        // Windows theme; caption button colors also need to track the XAML backdrop.
        EnqueueRefresh(state, updateInsets: true);
        return layout;
    }

    internal static bool IsConfigured(Window window) => States.ContainsKey(window)
        && window.ExtendsContentIntoTitleBar && window.SystemBackdrop is MicaBackdrop;

    internal static bool CanUpdateTitleBarInsets(Window window) => States.TryGetValue(window, out var state)
        && !state.Closed && IsWindowVisible(state.Handle) && !IsIconic(state.Handle)
        && GetForegroundWindow() == state.Handle;

    private static void EnqueueRefresh(State state, bool updateInsets)
    {
        if (state.Closed) return;
        state.Dispatcher.TryEnqueue(() =>
        {
            // Never touch the caption or its insets from a minimize transition. Inset
            // reads also remain foreground-only; inactive visible windows still need colors.
            if (state.Closed || !IsWindowVisible(state.Handle) || IsIconic(state.Handle)) return;

            try
            {
                ApplyNativeTheme(state);
                if (updateInsets && CanUpdateTitleBarInsets(state.Window)) UpdateInsets(state);
            }
            catch (COMException error)
            {
                // AppWindow can lose its native caption between the HWND check and the call.
                Serilog.Log.Debug(error, "无法同步窗口标题栏主题");
            }
        });
    }

    private static void ApplyNativeTheme(State state)
    {
        var dark = state.ThemeRoot.ActualTheme == ElementTheme.Dark;
        var highContrast = IsHighContrast();
        if (state.CaptionPalette == (dark, highContrast)) return;
        var useDarkMode = dark && !highContrast ? 1 : 0;
        const uint DwmwaUseImmersiveDarkMode = 20;
        _ = DwmSetWindowAttribute(state.Handle, DwmwaUseImmersiveDarkMode,
            ref useDarkMode, sizeof(int));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var bar = state.Window.AppWindow.TitleBar;
            // DWM immersive mode alone does not set the extended AppWindow button
            // foreground. Match the Mica surface, but hand control back in high contrast.
            Color? foreground = highContrast ? null : dark ? Colors.White : Color.FromArgb(255, 26, 26, 26);
            Color? inactive = highContrast ? null : dark ? Color.FromArgb(255, 181, 181, 181) : Color.FromArgb(255, 92, 92, 92);
            bar.ButtonBackgroundColor = highContrast ? null : Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = highContrast ? null : Colors.Transparent;
            bar.ButtonForegroundColor = foreground;
            bar.ButtonInactiveForegroundColor = inactive;
            bar.ButtonHoverForegroundColor = foreground;
            bar.ButtonPressedForegroundColor = foreground;
            bar.ButtonHoverBackgroundColor = highContrast ? null : dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = highContrast ? null : dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
        }
        state.CaptionPalette = (dark, highContrast);
    }

    private static void UpdateInsets(State state)
    {
        var scale = Math.Max(1, GetDpiForWindow(state.Handle)) / 96d;
        var titleBar = state.Window.AppWindow.TitleBar;
        state.LeftInset.Width = new GridLength(titleBar.LeftInset / scale);
        state.RightInset.Width = new GridLength(titleBar.RightInset / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        internal uint Size;
        internal uint Flags;
        internal nint Scheme;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SystemParametersInfoW(uint action, uint size, ref HighContrast value, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute,
        ref int value, int valueSize);
}
