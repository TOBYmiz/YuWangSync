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
        if (_isClosed) return;
        _infoBarCountdown.Background = WinUiUiHost.GetBrushResource("SystemFillColorSuccessBrush");
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
