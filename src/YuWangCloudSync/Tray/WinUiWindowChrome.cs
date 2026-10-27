using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
    }

    private static readonly Dictionary<Window, State> States = new();

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
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
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
        content.RequestedTheme = ElementTheme.Default;
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
            ThemeRoot = content,
            LeftInset = insetColumns[0],
            RightInset = insetColumns[2],
            Handle = handle,
            Dispatcher = dispatcher,
        };
        States.Add(window, state);

        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                EnqueueRefresh(state, updateInsets: true);
        };
        content.ActualThemeChanged += (_, _) => EnqueueRefresh(state, updateInsets: false);
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
        // Windows theme; native caption theme is synchronized only after activation.
        EnqueueRefresh(state, updateInsets: true);
        return layout;
    }

    internal static bool IsConfigured(Window window) => States.ContainsKey(window)
        && window.ExtendsContentIntoTitleBar && window.SystemBackdrop is MicaBackdrop;

    internal static bool CanUpdateTitleBar(Window window) => States.TryGetValue(window, out var state)
        && !state.Closed && IsWindowVisible(state.Handle) && !IsIconic(state.Handle)
        && GetForegroundWindow() == state.Handle;

    private static void EnqueueRefresh(State state, bool updateInsets)
    {
        if (state.Closed) return;
        state.Dispatcher.TryEnqueue(() =>
        {
            // AppWindow inset reads and DWM/titlebar writes can fail during minimize or
            // while this tool window is behind another application. Recheck after dispatch.
            if (!CanUpdateTitleBar(state.Window)) return;

            ApplyNativeTheme(state);
            if (updateInsets) UpdateInsets(state);
        });
    }

    private static void ApplyNativeTheme(State state)
    {
        var dark = state.ThemeRoot.ActualTheme == ElementTheme.Dark;
        var useDarkMode = dark ? 1 : 0;
        const uint DwmwaUseImmersiveDarkMode = 20;
        _ = DwmSetWindowAttribute(state.Handle, DwmwaUseImmersiveDarkMode,
            ref useDarkMode, sizeof(int));

        // Let Windows choose caption colors for dark mode, accent colors, high contrast,
        // inactive windows and the close button's distinct hover/pressed states.
        // AppWindowTitleBar color properties stay at their default (null) values.
    }

    private static void UpdateInsets(State state)
    {
        var scale = Math.Max(1, GetDpiForWindow(state.Handle)) / 96d;
        var titleBar = state.Window.AppWindow.TitleBar;
        state.LeftInset.Width = new GridLength(titleBar.LeftInset / scale);
        state.RightInset.Width = new GridLength(titleBar.RightInset / scale);
    }

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
