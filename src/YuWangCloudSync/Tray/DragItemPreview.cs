using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using WinRT.Interop;

namespace YuWangCloudSync.Tray;

/// <summary>A live XAML card in its own non-activating, input-disabled window.</summary>
internal sealed class DragItemPreview : IDisposable
{
    private const int MaxWidthDip = 240, HeightDip = 36, CornerRadiusDip = 8;
    private const double Padding = 10, IconSize = 20, Spacing = 12;
    private const int WindowStyle = -16, ExtendedWindowStyle = -20;
    private const int WindowFrame = 0x00C40000; // WS_BORDER | WS_DLGFRAME | WS_THICKFRAME
    private const uint DwmCornerPreference = 33, DwmBorderColor = 34;
    private static readonly SubclassProc HitTestSubclass = HandleHitTest;
    private const uint NoActivate = 0x0010, NoSize = 0x0001, NoZOrder = 0x0004, FrameChanged = 0x0020;
    private readonly Window _window;
    private readonly Border _card;
    private readonly TextBlock? _countLabel;
    private readonly PreviewAcrylic _acrylic;
    private readonly int _gap;
    private readonly bool _touch;
    private readonly int _hideDwmBorderResult;
    private (int X, int Y)? _lastPosition;
    private bool _disposed;
    internal nint Handle { get; private set; }
    internal int Width { get; }
    internal int Height { get; }
    internal bool UsesRowIcon { get; }
    internal string? DisplayedItemCount => _countLabel?.Text;
    internal string MaterialDescription => _acrylic.Describe();

    internal DragItemPreview(string name, string glyph, double scale, bool touch, ImageSource? source,
        int itemCount = 1, ElementTheme requestedTheme = ElementTheme.Default)
    {
        _touch = touch;
        UsesRowIcon = source is not null;
        _gap = (int)Math.Round((touch ? 16 : 10) * scale);
        var card = _card = CreateCard(name, glyph, source, itemCount);
        var content = (StackPanel)card.Child;
        _countLabel = content.Children.Count > 2 ? (TextBlock)content.Children[2] : null;
        card.RequestedTheme = requestedTheme;
        card.Measure(new Size(MaxWidthDip, HeightDip));
        Width = Math.Max(1, (int)Math.Ceiling(card.DesiredSize.Width * scale));
        Height = (int)Math.Ceiling(HeightDip * scale);
        _acrylic = new PreviewAcrylic(card);
        _window = new Window();
        try
        {
            _window.Content = card;
            _window.SystemBackdrop = _acrylic;
            var appWindow = _window.AppWindow;
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            Handle = WindowNative.GetWindowHandle(_window);
            var exStyle = GetWindowLongW(Handle, ExtendedWindowStyle);
            SetWindowLongW(Handle, ExtendedWindowStyle, (exStyle & ~0x00040000 /* APPWINDOW */)
                | 0x080000A0 /* NOACTIVATE | TOOLWINDOW | TRANSPARENT */);
            SetWindowLongW(Handle, WindowStyle, GetWindowLongW(Handle, WindowStyle) & ~WindowFrame);
            _hideDwmBorderResult = DwmSetWindowAttribute(Handle, DwmBorderColor, in NoDwmBorder, sizeof(uint));
            _ = DwmSetWindowAttribute(Handle, DwmCornerPreference, in UseOwnCorners, sizeof(uint));
            if (!SetWindowSubclass(Handle, HitTestSubclass, 1, 0))
                throw new Win32Exception("无法设置拖放预览的鼠标穿透。");
            var cursor = NativeFileDrag.CursorPosition;
            var position = Locate(cursor);
            if (!SetWindowPos(Handle, -1, position.X, position.Y, Width, Height, NoActivate | FrameChanged))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var diameter = (int)Math.Round(2 * CornerRadiusDip * scale);
            var region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, diameter, diameter);
            if (region == 0) throw new Win32Exception("无法创建拖放预览圆角区域。");
            if (SetWindowRgn(Handle, region, false) == 0)
            {
                DeleteObject(region);
                throw new Win32Exception("无法应用拖放预览圆角区域。");
            }
            _lastPosition = cursor;
            appWindow.Show(activateWindow: false);
            // Show initializes the WinUI HWND and can re-enable it. Do not let the
            // card interact with input; the OLE hotspot stays offset from its region.
            EnableWindow(Handle, false);
        }
        catch { Dispose(); throw; }
    }

    private static readonly uint NoDwmBorder = 0xFFFFFFFE, UseOwnCorners = 1;

    private static nint HandleHitTest(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0084) return -1; // WM_NCHITTEST / HTTRANSPARENT
        if (message == 0x0021) return 3;  // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x024B) return 3;  // WM_POINTERACTIVATE / PA_NOACTIVATE
        if (message == 0x0082) RemoveWindowSubclass(hwnd, HitTestSubclass, id);
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static Border CreateCard(string name, string glyph, ImageSource? source, int itemCount = 1)
    {
        FrameworkElement icon = source is null
            ? new FontIcon { Glyph = glyph, FontSize = 18 }
            : new Image { Source = source, Stretch = Stretch.Uniform };
        icon.Width = icon.Height = IconSize;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var nameWidth = MaxWidthDip - 2 * Padding - IconSize - Spacing;
        var count = itemCount > 1 ? new TextBlock
        {
            Text = $"等 {itemCount} 项",
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        } : null;
        if (count is not null)
        {
            // Reserve the count before measuring the name, so only the name ellipsizes.
            count.Measure(new Size(double.PositiveInfinity, HeightDip));
            nameWidth = Math.Max(0, nameWidth - count.DesiredSize.Width - Spacing);
        }
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = name,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = nameWidth,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        if (count is not null) content.Children.Add(count);
        return new Border
        {
            Height = HeightDip,
            MaxWidth = MaxWidthDip,
            Padding = new Thickness(Padding, 0, Padding, 0),
            CornerRadius = new CornerRadius(CornerRadiusDip),
            UseLayoutRounding = true,
            IsHitTestVisible = false,
            Child = content,
        };
    }

    internal void Move()
    {
        var cursor = NativeFileDrag.CursorPosition;
        if (_lastPosition == cursor) return;
        var point = Locate(cursor);
        if (!SetWindowPos(Handle, 0, point.X, point.Y, 0, 0, NoSize | NoZOrder | NoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _lastPosition = cursor;
    }

    private PointI Locate((int X, int Y) cursor)
    {
        var above = cursor.Y - _gap - Height;
        var below = cursor.Y + _gap;
        var point = new PointI { X = cursor.X + _gap, Y = _touch ? above : below };
        var monitor = MonitorFromPoint(new PointI { X = cursor.X, Y = cursor.Y }, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return point;
        var bounds = info.Monitor;
        if (point.X + Width > bounds.Right) point.X = cursor.X - _gap - Width;
        if (point.Y < bounds.Top || point.Y + Height > bounds.Bottom) point.Y = _touch ? below : above;
        point.X = Math.Clamp(point.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        point.Y = Math.Clamp(point.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
        return point;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Handle = 0;
        try { _window.SystemBackdrop = null; }
        finally { _window.Close(); }
    }

    internal static (bool Passed, string Detail) CheckLayout()
    {
        const string glyph = "\uE8A5";
        var source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        var compact = CreateCard("文档", glyph, source);
        var longCard = CreateCard(string.Concat(Enumerable.Repeat("一个非常长的文件名", 12)), glyph, source);
        compact.Measure(new Size(240, 36));
        longCard.Measure(new Size(240, 36));
        var icon = (Image)((StackPanel)compact.Child).Children[0];
        return (ReferenceEquals(icon.Source, source) && compact.DesiredSize.Width < longCard.DesiredSize.Width
            && longCard.DesiredSize.Width <= MaxWidthDip && longCard.DesiredSize.Height == HeightDip
            && longCard.CornerRadius.TopLeft == CornerRadiusDip,
            $"Short={compact.DesiredSize.Width:F0} DIP; long={longCard.DesiredSize.Width:F0}×{longCard.DesiredSize.Height:F0} DIP; source={icon.Source.GetType().Name}.");
    }

    internal static async Task<IReadOnlyList<UiSelfTestCheck>> CheckVisualsAsync(double scale)
    {
        const string longName = "这是一个很长的文件名，用于确认截断时仍显示选中项数量";
        using var preview = new DragItemPreview(longName, "\uE8A5", scale, touch: false, source: null, itemCount: 123);
        var text = (TextBlock)((StackPanel)preview._card.Child).Children[1];
        var checks = (await WinUiUiHost.CheckLiveThemeAsync(preview._card,
            ("Existing drag preview uses a themed tint or fallback", () => preview._acrylic.RenderedTint),
            ("Existing drag preview filename follows theme", () => WinUiUiHost.BrushColor(text.Foreground)),
            ("Existing drag preview item count follows theme", () => WinUiUiHost.BrushColor(preview._countLabel?.Foreground)))).ToList();
        preview._card.UpdateLayout();
        var count = preview._countLabel!;
        var countSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(count);
        var countRight = preview._card.Padding.Left + countSlot.Right;
        var width = preview._card.ActualWidth;
        checks.Add(new("Multi-file preview keeps the item count visible after filename trimming",
            width > 0 && width <= MaxWidthDip && text.Text == longName && text.TextTrimming == TextTrimming.CharacterEllipsis
            && count.Text == "等 123 项" && count.ActualWidth > 0 && countSlot.X > 0 && countRight <= width,
            $"Width={width:F0} DIP; name max={text.MaxWidth:F0} DIP; count='{count.Text}'; count right={countRight:F0} DIP."));
        var frame = GetWindowLongW(preview.Handle, WindowStyle) & WindowFrame;
        var dwmBorderHidden = preview._hideDwmBorderResult >= 0;
        checks.Add(new("Drag preview has no native frame or DWM border",
            frame == 0 && (Environment.OSVersion.Version.Build < 22000 || dwmBorderHidden),
            $"Frame style=0x{frame:X}; DWM no-border HRESULT=0x{preview._hideDwmBorderResult:X8}."));
        return checks;
    }

    private sealed partial class PreviewAcrylic(Border card) : SystemBackdrop
    {
        private const float TintOpacity = .20f; // 80% tint transparency, without fading the icon or text.
        private DesktopAcrylicController? _controller;
        private SystemBackdropConfiguration? _configuration;

        private Color EffectiveColor { get; set; }
        internal Color? RenderedTint => _controller?.TintColor ?? WinUiUiHost.BrushColor(card.Background);

        internal string Describe() => _controller is null ? $"solid fallback; color={EffectiveColor}"
            : $"{_controller.State}; tint={EffectiveColor}; tint opacity={TintOpacity:F2}";

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            card.ActualThemeChanged += OnThemeChanged;
            WinUiWindowChrome.SystemColorsChanged += ApplyTheme;
            try
            {
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.EnsureSystemDispatcherQueue();
                if (DesktopAcrylicController.IsSupported())
                {
                    _configuration = new SystemBackdropConfiguration { IsInputActive = true };
                    _controller = new DesktopAcrylicController { TintOpacity = TintOpacity };
                    ApplyTheme();
                    _controller.SetSystemBackdropConfiguration(_configuration);
                    if (!_controller.AddSystemBackdropTarget(target)) ClearController();
                }
            }
            catch (Exception error)
            {
                ClearController();
                Serilog.Log.Debug(error, "拖放预览无法使用桌面亚克力");
            }
            ApplyTheme();
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            // This window owns its configuration. In particular, do not forward a
            // queued WinUI notification for a target that has already disconnected.
        }

        private void OnThemeChanged(FrameworkElement _, object? args) => ApplyTheme();

        private void ApplyTheme()
        {
            var dark = card.ActualTheme == ElementTheme.Dark;
            var highContrast = WinUiWindowChrome.IsHighContrast();
            var tint = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 249, 249, 249);
            var windowColor = highContrast ? GetSysColor(5 /* COLOR_WINDOW */) : 0;
            EffectiveColor = highContrast
                ? Color.FromArgb(255, (byte)windowColor, (byte)(windowColor >> 8), (byte)(windowColor >> 16))
                : tint;
            if (_configuration is { } configuration && _controller is { } controller)
            {
                configuration.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
                configuration.IsHighContrast = highContrast;
                if (highContrast) configuration.HighContrastBackgroundColor = EffectiveColor;
                // Set the actual RGB tint, not brush/window opacity. Leave acrylic's
                // luminosity blending in place so text remains legible over the desktop.
                controller.TintColor = tint;
                controller.FallbackColor = EffectiveColor;
                card.Background = null;
            }
            else card.Background = new SolidColorBrush(EffectiveColor);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            card.ActualThemeChanged -= OnThemeChanged;
            ClearController();
            WinUiWindowChrome.SystemColorsChanged -= ApplyTheme;
            base.OnTargetDisconnected(target);
        }

        private void ClearController()
        {
            _controller?.Dispose();
            _controller = null;
            _configuration = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal uint Size; internal RectI Monitor, Work; internal uint Flags; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, in uint value, uint length);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetWindowLongW(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(PointI point, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
