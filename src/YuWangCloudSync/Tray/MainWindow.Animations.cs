using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    private const int NavigationExpandMilliseconds = 220;
    private const int NavigationExpandStaggerMilliseconds = 16;
    private const int NavigationExpandMaxStaggeredRows = 12;
    private const long NavigationExpandWaitMilliseconds = 3000;

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    // Child rows of a just-expanded node, animated as their containers are realized. Every
    // expansion starts a fresh explicit animation (from 0 to 1), so a quick collapse/re-expand
    // always replays it instead of reusing rows still finishing a theme delete transition.
    private readonly Dictionary<TreeViewNode, HashSet<TreeViewNode>> _navigationExpandAnimations = [];
    private readonly Dictionary<TreeViewNode, long> _navigationExpandStarted = [];
    private bool _navigationLayoutHooked;

    private readonly Border _infoBarCountdown = new()
    {
        Name = "InfoBarCountdown",
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        CornerRadius = new CornerRadius(4),
        Opacity = 0.14,
    };

    private Vector3KeyFrameAnimation? _infoBarCountdownAnimation;
    private const double InfoBarTransitionMilliseconds = 200;
    private readonly Grid _infoBarHost = new()
    {
        Name = "InfoBarHost", Visibility = Visibility.Collapsed, Height = 0, Opacity = 0,
    };
    private readonly TranslateTransform _infoBarTranslation = new() { Y = -6 };
    private bool _infoBarAnimating;
    private bool _infoBarAnimationShowing;
    private bool _allowInfoBarClose;
    private long _infoBarAnimationStarted;
    private double _infoBarHeightFrom, _infoBarHeightTo, _infoBarOpacityFrom, _infoBarTranslationFrom;

    private void OpenInfoBar()
    {
        var animate = !_infoBar.IsOpen || _infoBarAnimating;
        _infoBar.IsOpen = true;
        _infoBarHost.Visibility = Visibility.Visible;
        _infoBarHost.IsHitTestVisible = true;
        if (animate) AnimateInfoBar(show: true);
    }

    private void OnInfoBarClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        _infoBarCloseTimer.Stop();
        if (_allowInfoBarClose || _isClosed || !_uiSettings.AnimationsEnabled || _infoBarHost.XamlRoot is null) return;
        // Keep the control and countdown visible until the exit transition finishes.
        args.Cancel = true;
        if (!_infoBarAnimating || _infoBarAnimationShowing) AnimateInfoBar(show: false);
    }

    private void AnimateInfoBar(bool show)
    {
        StopInfoBarAnimation();
        _infoBarAnimationShowing = show;
        _infoBarHeightFrom = double.IsNaN(_infoBarHost.Height) ? _infoBarHost.ActualHeight : _infoBarHost.Height;
        _infoBarHeightTo = 0;
        if (show)
        {
            // Measure the new content, including margins, independently of the host's
            // current animated height so a replacement can reverse an in-flight close.
            var width = _infoBarHost.ActualWidth > 0 ? _infoBarHost.ActualWidth : _root.ActualWidth;
            _infoBar.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            _infoBarHeightTo = _infoBar.DesiredSize.Height;
        }
        _infoBarOpacityFrom = _infoBarHost.Opacity;
        _infoBarTranslationFrom = _infoBarTranslation.Y;
        _infoBarHost.IsHitTestVisible = show;
        if (!_uiSettings.AnimationsEnabled || _infoBarHost.XamlRoot is null)
        {
            FinishInfoBarAnimation();
            return;
        }
        _infoBarHost.Height = _infoBarHeightFrom;
        _infoBarAnimationStarted = Environment.TickCount64;
        _infoBarAnimating = true;
        CompositionTarget.Rendering += OnInfoBarRendering;
    }

    private void OnInfoBarRendering(object? sender, object args)
    {
        var progress = Math.Clamp((Environment.TickCount64 - _infoBarAnimationStarted)
            / InfoBarTransitionMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _infoBarHost.Height = _infoBarHeightFrom + (_infoBarHeightTo - _infoBarHeightFrom) * eased;
        _infoBarHost.Opacity = _infoBarOpacityFrom + ((_infoBarAnimationShowing ? 1 : 0) - _infoBarOpacityFrom) * eased;
        _infoBarTranslation.Y = _infoBarTranslationFrom + ((_infoBarAnimationShowing ? 0 : -6) - _infoBarTranslationFrom) * eased;
        if (progress >= 1) FinishInfoBarAnimation();
    }

    private void FinishInfoBarAnimation()
    {
        StopInfoBarAnimation();
        if (_infoBarAnimationShowing)
        {
            _infoBarHost.Height = double.NaN;
            _infoBarHost.Opacity = 1;
            _infoBarTranslation.Y = 0;
            return;
        }
        _allowInfoBarClose = true;
        try { _infoBar.IsOpen = false; }
        finally { _allowInfoBarClose = false; }
    }

    private void StopInfoBarAnimation()
    {
        if (!_infoBarAnimating) return;
        CompositionTarget.Rendering -= OnInfoBarRendering;
        _infoBarAnimating = false;
    }

    private void ResetClosedInfoBar()
    {
        StopInfoBarAnimation();
        _infoBarCloseTimer.Stop();
        StopInfoBarCountdown();
        _infoBarHost.Visibility = Visibility.Collapsed;
        _infoBarHost.Height = 0;
        _infoBarHost.Opacity = 0;
        _infoBarTranslation.Y = -6;
    }

    private void QueueNavigationExpandAnimation(TreeViewNode node)
    {
        if (_isClosed || _updatingNavigationTree || !_uiSettings.AnimationsEnabled) return;
        _navigationExpandAnimations[node] = [];
        _navigationExpandStarted[node] = Environment.TickCount64;
        if (_navigationLayoutHooked) return;
        _navigationLayoutHooked = true;
        _navigationTree.LayoutUpdated += OnNavigationLayoutUpdated;
    }

    private void CancelNavigationExpandAnimation(TreeViewNode node)
    {
        _navigationExpandAnimations.Remove(node);
        _navigationExpandStarted.Remove(node);
    }

    private void OnNavigationLayoutUpdated(object? sender, object args)
    {
        var now = Environment.TickCount64;
        List<TreeViewNode>? finished = null;
        foreach (var (node, animated) in _navigationExpandAnimations)
        {
            var done = _isClosed || !node.IsExpanded
                || now - _navigationExpandStarted[node] > NavigationExpandWaitMilliseconds;
            if (!done)
            {
                var realized = 0;
                foreach (var child in node.Children)
                {
                    if (Project<UIElement>(_navigationTree.ContainerFromNode(child)) is not { } container) continue;
                    realized++;
                    if (animated.Add(child)) AnimateNavigationRow(container, animated.Count - 1);
                }
                var loading = node.HasUnrealizedChildren
                    || (_folderNodes.TryGetValue(node, out var state) && !state.ChildrenLoaded);
                done = !loading && (node.Children.Count == 0 || realized > 0);
            }
            if (done) (finished ??= []).Add(node);
        }
        if (finished is not null)
            foreach (var node in finished) CancelNavigationExpandAnimation(node);
        if (_navigationExpandAnimations.Count > 0 || !_navigationLayoutHooked) return;
        _navigationLayoutHooked = false;
        _navigationTree.LayoutUpdated -= OnNavigationLayoutUpdated;
    }

    private static void AnimateNavigationRow(UIElement row, int index)
    {
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var delay = TimeSpan.FromMilliseconds(Math.Min(index, NavigationExpandMaxStaggeredRows)
            * NavigationExpandStaggerMilliseconds);
        var duration = TimeSpan.FromMilliseconds(NavigationExpandMilliseconds);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = duration;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Target = "Translation";
        translation.InsertKeyFrame(0f, new Vector3(0, -8, 0));
        translation.InsertKeyFrame(1f, Vector3.Zero, easing);
        translation.Duration = duration;
        translation.DelayTime = delay;
        translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        row.StartAnimation(opacity);
        row.StartAnimation(translation);
    }

    /// <summary>
    /// A slightly deeper green layer over a success InfoBar. Its left edge stays fixed while
    /// the right edge sweeps left until the bar closes itself.
    /// </summary>
    private void StartInfoBarCountdown()
    {
        if (_isClosed || !_uiSettings.AnimationsEnabled) return;
        _infoBarCountdown.Style ??= WinUiUiHost.GetStyleResource("YuWangSuccessCountdownStyle");
        _infoBarCountdown.Visibility = Visibility.Visible;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertKeyFrame(0f, Vector3.One);
        scale.InsertKeyFrame(1f, new Vector3(0, 1, 1), compositor.CreateLinearEasingFunction());
        scale.Duration = _infoBarCloseTimer.Interval;
        // Scale is relative to CenterPoint (0,0,0): the left edge never moves.
        _infoBarCountdown.CenterPoint = Vector3.Zero;
        if (_infoBarCountdownAnimation is { } previous) _infoBarCountdown.StopAnimation(previous);
        _infoBarCountdownAnimation = scale;
        _infoBarCountdown.StartAnimation(scale);
    }

    private void StopInfoBarCountdown()
    {
        if (_infoBarCountdown.Visibility == Visibility.Collapsed) return;
        _infoBarCountdown.Visibility = Visibility.Collapsed;
        if (_infoBarCountdownAnimation is { } running) _infoBarCountdown.StopAnimation(running);
        _infoBarCountdownAnimation = null;
        _infoBarCountdown.Scale = Vector3.One;
    }
}
