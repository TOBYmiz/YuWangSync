using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    private const double MinimumNavigationTreeHeight = 120;
    private const double MinimumFavoritesHeight = 80;
    private const double CollapsedFavoritesHeight = 36;
    private const double FavoritesGripHeight = 7;

    private readonly Grid _sidebarPanes = new() { Name = "SidebarPanes" };
    private readonly RowDefinition _favoritesRow = new();
    private readonly StackPanel _favoriteRows = new() { Spacing = 2 };
    private readonly Grid _favoritesBody = new();
    private readonly FontIcon _favoritesChevron = new() { FontSize = 10 };
    private readonly Button _favoritesToggle = new()
    {
        Name = "ToggleFavorites",
        Height = CollapsedFavoritesHeight,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(SidebarInset, 0, 4, 0),
    };
    private readonly Button _favoriteCurrent = NewIconButton("\uE109", "收藏当前文件夹", 14);
    private readonly ResizeGrip _favoritesGrip = new(InputSystemCursorShape.SizeNorthSouth)
    {
        Name = "FavoritesResizeGrip",
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly HashSet<(ulong Disk, ulong Folder)> _unavailableFavorites = [];
    private double _preferredFavoritesHeight;
    private readonly RotateTransform _favoritesChevronRotation = new();
    private bool _favoritesAnimating;
    private long _favoritesAnimationStarted;
    private double _favoritesAnimationFrom;
    private double _favoritesAnimationTo;
    private double _favoritesGripFrom;
    private double _favoritesRotationFrom;
    private double _favoritesOpacityFrom;

    private Grid BuildSidebarPanes()
    {
        _preferredFavoritesHeight = _savedState.FavoritesHeight;
        _favoritesChevron.Glyph = "\uE76C";
        _favoritesChevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _favoritesChevron.RenderTransform = _favoritesChevronRotation;
        _favoritesBody.SizeChanged += (_, _) => _favoritesBody.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _favoritesBody.ActualWidth, _favoritesBody.ActualHeight),
        };
        _sidebarPanes.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star), MinHeight = MinimumNavigationTreeHeight,
        });
        _sidebarPanes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FavoritesGripHeight) });
        _sidebarPanes.RowDefinitions.Add(_favoritesRow);
        _sidebarPanes.Children.Add(_navigationTree);

        _favoritesGrip.Children.Add(new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = WinUiUiHost.GetBrushResource("DividerStrokeColorDefaultBrush"),
        });
        AutomationProperties.SetName(_favoritesGrip, "调整收藏区域高度");
        var dragging = false;
        var startY = 0d;
        var startHeight = 0d;
        _favoritesGrip.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(_root);
            if (!_savedState.FavoritesExpanded || !point.Properties.IsLeftButtonPressed
                || !_favoritesGrip.CapturePointer(args.Pointer)) return;
            dragging = true;
            StopFavoritesAnimation();
            startY = point.Position.Y;
            startHeight = _favoritesRow.ActualHeight;
            args.Handled = true;
        };
        _favoritesGrip.PointerMoved += (_, args) =>
        {
            if (!dragging) return;
            _preferredFavoritesHeight = ClampFavoritesHeight(
                startHeight - (args.GetCurrentPoint(_root).Position.Y - startY));
            ConstrainFavoritesHeight();
            args.Handled = true;
        };
        void FinishResize()
        {
            if (!dragging) return;
            dragging = false;
            _savedState.FavoritesHeight = _preferredFavoritesHeight;
            SaveBrowserState();
        }
        _favoritesGrip.PointerReleased += (_, args) =>
        {
            FinishResize();
            _favoritesGrip.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        };
        _favoritesGrip.PointerCaptureLost += (_, _) => FinishResize();
        _favoritesGrip.PointerCanceled += (_, _) => FinishResize();
        Grid.SetRow(_favoritesGrip, 1);
        _sidebarPanes.Children.Add(_favoritesGrip);

        var pane = new Grid { Name = "SidebarFavorites" };
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CollapsedFavoritesHeight) });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _favoritesToggle.Style = WinUiUiHost.GetStyleResource("SubtleButtonStyle");
        _favoritesToggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                _favoritesChevron,
                new FontIcon { Glyph = "\uE734", FontSize = 14 },
                new TextBlock { Text = "收藏", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _favoritesToggle.Click += (_, _) =>
        {
            _savedState.FavoritesExpanded = !_savedState.FavoritesExpanded;
            ConstrainFavoritesHeight(animate: true);
            SaveBrowserState();
        };
        header.Children.Add(_favoritesToggle);
        _favoriteCurrent.Name = "FavoriteCurrentFolder";
        _favoriteCurrent.Click += (_, _) =>
        {
            if (!_busyNow && _currentNode is { } node && _folderNodes.TryGetValue(node, out var folder))
                ToggleFavorite(folder);
        };
        Grid.SetColumn(_favoriteCurrent, 1);
        header.Children.Add(_favoriteCurrent);
        pane.Children.Add(header);

        _favoritesBody.Children.Add(new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _favoriteRows,
        });
        Grid.SetRow(_favoritesBody, 1);
        pane.Children.Add(_favoritesBody);
        Grid.SetRow(pane, 2);
        _sidebarPanes.Children.Add(pane);
        _sidebarPanes.SizeChanged += (_, _) => ConstrainFavoritesHeight();
        RenderFavorites();
        ConstrainFavoritesHeight();
        return _sidebarPanes;
    }

    private double ClampFavoritesHeight(double height) => Math.Clamp(height, MinimumFavoritesHeight,
        Math.Max(MinimumFavoritesHeight, _sidebarPanes.ActualHeight - MinimumNavigationTreeHeight - FavoritesGripHeight));

    private void ConstrainFavoritesHeight(bool animate = false)
    {
        var expanded = _savedState.FavoritesExpanded;
        var target = expanded ? ClampFavoritesHeight(_preferredFavoritesHeight) : CollapsedFavoritesHeight;
        _favoritesGrip.IsHitTestVisible = expanded;
        AutomationProperties.SetName(_favoritesToggle, expanded ? "收起收藏" : "展开收藏");
        if (!animate && _favoritesAnimating) return;
        StopFavoritesAnimation();
        if (animate && _sidebarPanes.ActualHeight > 0 && _uiSettings.AnimationsEnabled)
        {
            _favoritesAnimationFrom = _favoritesRow.ActualHeight;
            _favoritesAnimationTo = target;
            _favoritesGripFrom = _sidebarPanes.RowDefinitions[1].ActualHeight;
            _favoritesRotationFrom = _favoritesChevronRotation.Angle;
            _favoritesOpacityFrom = _favoritesBody.Opacity;
            _favoritesBody.Visibility = Visibility.Visible;
            // Freeze both panes at their larger extent and let the grid rows clip them. Each frame then
            // only re-arranges two rows instead of re-measuring the tree list and the favorites list.
            var gripTo = expanded ? FavoritesGripHeight : 1;
            var treeHeight = Math.Max(_navigationTree.ActualHeight,
                _sidebarPanes.ActualHeight - target - gripTo);
            _navigationTree.Height = Math.Max(MinimumNavigationTreeHeight, treeHeight);
            _navigationTree.VerticalAlignment = VerticalAlignment.Top;
            _favoritesBody.Height = Math.Max(0,
                Math.Max(_favoritesAnimationFrom, _favoritesAnimationTo) - CollapsedFavoritesHeight);
            _favoritesBody.VerticalAlignment = VerticalAlignment.Top;
            _favoritesAnimationStarted = Stopwatch.GetTimestamp();
            _favoritesAnimating = true;
            CompositionTarget.Rendering += OnFavoritesAnimationFrame;
            return;
        }
        ApplyFavoritesAnimation(target, expanded ? FavoritesGripHeight : 1, expanded ? 90 : 0, expanded ? 1 : 0);
        _favoritesBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopFavoritesAnimation()
    {
        if (!_favoritesAnimating) return;
        _favoritesAnimating = false;
        CompositionTarget.Rendering -= OnFavoritesAnimationFrame;
        _navigationTree.ClearValue(FrameworkElement.HeightProperty);
        _navigationTree.ClearValue(FrameworkElement.VerticalAlignmentProperty);
        _favoritesBody.ClearValue(FrameworkElement.HeightProperty);
        _favoritesBody.ClearValue(FrameworkElement.VerticalAlignmentProperty);
    }

    // CompositionTarget.Rendering runs once per composed frame, unlike a 16 ms DispatcherTimer
    // that drifts against vsync and visibly skips frames.
    private void OnFavoritesAnimationFrame(object? sender, object args)
    {
        if (_isClosed) { StopFavoritesAnimation(); return; }
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_favoritesAnimationStarted).TotalMilliseconds / 200, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var expanded = _savedState.FavoritesExpanded;
        ApplyFavoritesAnimation(_favoritesAnimationFrom + (_favoritesAnimationTo - _favoritesAnimationFrom) * eased,
            _favoritesGripFrom + ((expanded ? FavoritesGripHeight : 1) - _favoritesGripFrom) * eased,
            _favoritesRotationFrom + ((expanded ? 90 : 0) - _favoritesRotationFrom) * eased,
            _favoritesOpacityFrom + ((expanded ? 1 : 0) - _favoritesOpacityFrom) * eased);
        if (progress < 1) return;
        StopFavoritesAnimation();
        ConstrainFavoritesHeight();
    }

    private void ApplyFavoritesAnimation(double height, double gripHeight, double angle, double opacity)
    {
        if (_favoritesRow.Height.Value != height) _favoritesRow.Height = new GridLength(height);
        if (_sidebarPanes.RowDefinitions[1].Height.Value != gripHeight)
            _sidebarPanes.RowDefinitions[1].Height = new GridLength(gripHeight);
        _favoritesChevronRotation.Angle = angle;
        _favoritesBody.Opacity = opacity;
    }

    private bool IsFavorite(NavigationFolderState folder) => _savedState.Favorites
        .Any(favorite => favorite.DiskId == folder.Disk.Id && favorite.FolderId == folder.Id);

    private NavigationFolderState? GetFavoriteTarget(ItemActionTarget? target)
        => target is { Items.Count: 1 } && target.Items[0] is { IsFolder: true } item
            && _folderNodesByKey.TryGetValue((target.Parent.Disk.Id, item.Id), out var folder) ? folder : null;

    private void ToggleFavorite(NavigationFolderState folder)
    {
        if (_isClosed || _busyNow) return;
        var removed = _savedState.Favorites.RemoveAll(favorite =>
            favorite.DiskId == folder.Disk.Id && favorite.FolderId == folder.Id);
        if (removed == 0)
        {
            _savedState.Favorites.Add(new FavoriteFolderState
            {
                DiskId = folder.Disk.Id,
                FolderId = folder.Id,
                Name = folder.Name,
                DisplayPath = GetNavigationPath(folder.Node),
                NodePath = GetNodePath(folder.Node),
            });
            _savedState.FavoritesExpanded = true;
        }
        _unavailableFavorites.Remove((folder.Disk.Id, folder.Id));
        RenderFavorites();
        ConstrainFavoritesHeight(animate: true);
        SaveBrowserState();
        UpdateItemMenuStates();
    }

    private void RemoveFavorite(FavoriteFolderState favorite)
    {
        if (_isClosed) return;
        _savedState.Favorites.RemoveAll(entry => entry.DiskId == favorite.DiskId && entry.FolderId == favorite.FolderId);
        _unavailableFavorites.Remove((favorite.DiskId, favorite.FolderId));
        RenderFavorites();
        SaveBrowserState();
    }

    private void RenderFavorites()
    {
        if (_isClosed) return;
        var changed = false;
        _favoriteRows.Children.Clear();
        if (_savedState.Favorites.Count == 0)
        {
            _favoriteRows.Children.Add(new TextBlock
            {
                Text = "点 + 收藏当前文件夹，也可右键添加。",
                Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 4, 12, 8),
                Foreground = WinUiUiHost.GetBrushResource("TextFillColorSecondaryBrush"),
            });
        }
        foreach (var favorite in _savedState.Favorites)
        {
            var key = (favorite.DiskId, favorite.FolderId);
            if (_folderNodesByKey.TryGetValue(key, out var folder))
            {
                _unavailableFavorites.Remove(key);
                var path = GetNodePath(folder.Node);
                var displayPath = GetNavigationPath(folder.Node);
                if (favorite.Name != folder.Name || !favorite.NodePath.SequenceEqual(path)
                    || favorite.DisplayPath != displayPath)
                {
                    favorite.Name = folder.Name;
                    favorite.NodePath = path;
                    favorite.DisplayPath = displayPath;
                    changed = true;
                }
            }
            var unavailable = _unavailableFavorites.Contains(key)
                || (_navigationTree.RootNodes.Count > 0 && !_folderNodesByKey.ContainsKey((favorite.DiskId, 0)));
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new FontIcon { Glyph = favorite.FolderId == 0 ? "\uEDA2" : FolderGlyph, FontSize = 14 });
            var name = new TextBlock
            {
                Text = favorite.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            content.Children.Add(name);
            var button = new Button
            {
                Content = content,
                Tag = favorite,
                Style = WinUiUiHost.GetStyleResource("SubtleButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(SidebarInset, 6, SidebarInset, 6),
                Foreground = WinUiUiHost.GetBrushResource(unavailable
                    ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"),
            };
            AutomationProperties.SetName(button, favorite.Name + (unavailable ? "，目录暂不可用" : "，收藏文件夹"));
            ToolTipService.SetToolTip(button, unavailable
                ? "暂不可用，点击重试或右键移除。" : favorite.DisplayPath);
            button.Click += (_, _) => RunUi(() => OpenFavoriteAsync(favorite));
            var menu = new MenuFlyout();
            var remove = NewMenuItem("\uE77A", "移除收藏");
            remove.Click += (_, _) => RemoveFavorite(favorite);
            menu.Items.Add(remove);
            button.ContextFlyout = menu;
            _favoriteRows.Children.Add(button);
        }
        UpdateFavoriteCommands();
        if (changed) SaveBrowserState();
    }

    private void UpdateFavoriteCommands()
    {
        _favoriteCurrent.IsEnabled = !_busyNow && _currentFolder is not null;
        var currentIsFavorite = _currentNode is { } node && _folderNodes.TryGetValue(node, out var folder)
            && IsFavorite(folder);
        if (_favoriteCurrent.Content is FontIcon icon) icon.Glyph = currentIsFavorite ? "\uE735" : "\uE109";
        var label = currentIsFavorite ? "取消收藏当前文件夹" : "收藏当前文件夹";
        ToolTipService.SetToolTip(_favoriteCurrent, label);
        AutomationProperties.SetName(_favoriteCurrent, label);
        foreach (var button in _favoriteRows.Children.OfType<Button>())
        {
            button.IsEnabled = !_busyNow;
            var selected = button.Tag is FavoriteFolderState favorite && _currentFolder is { } current
                && favorite.DiskId == current.Disk.Id && favorite.FolderId == current.Id;
            button.Background = selected
                ? WinUiUiHost.GetBrushResource("SubtleFillColorSecondaryBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void ShowFolderFavoriteMenu(NavigationFolderState folder, FrameworkElement anchor,
        ContextRequestedEventArgs args)
    {
        var menu = new MenuFlyout();
        var open = NewMenuItem("\uE8E5", "打开");
        open.Click += (_, _) => RunUi(() => NavigateToFolderAsync(folder, addToHistory: true));
        menu.Items.Add(open);
        var toggle = NewMenuItem("\uE734", IsFavorite(folder) ? "取消收藏" : "添加到收藏");
        toggle.Click += (_, _) => ToggleFavorite(folder);
        menu.Items.Add(toggle);
        ShowContextMenu(menu, anchor, args);
    }

    private async Task OpenFavoriteAsync(FavoriteFolderState favorite)
    {
        if (_busyNow || _isClosed) return;
        NavigationFolderState? target = null;
        await RunBusyAsync($"正在打开收藏“{favorite.Name}”…", async () =>
        {
            target = await ResolveFavoriteAsync(favorite);
            if (target is not null) return;
            _unavailableFavorites.Add((favorite.DiskId, favorite.FolderId));
            RenderFavorites();
            ShowInfo(InfoBarSeverity.Warning, "收藏目录暂不可用",
                "目录可能已移动、删除或无权访问。可刷新后重试，或右键移除收藏。");
        }, "无法打开收藏");
        if (target is null || _isClosed) return;
        await NavigateToFolderAsync(target, addToHistory: true);
    }

    private async Task<NavigationFolderState?> ResolveFavoriteAsync(FavoriteFolderState favorite)
    {
        if (_folderNodesByKey.TryGetValue((favorite.DiskId, favorite.FolderId), out var known)) return known;
        if (!_folderNodesByKey.TryGetValue((favorite.DiskId, 0), out var current)) return null;
        // Start at the disk so regrouping disks does not invalidate saved folder paths.
        var path = favorite.NodePath.ToArray();
        var diskIndex = Array.IndexOf(path, $"disk:{favorite.DiskId}");
        if (diskIndex < 0) return null;
        for (var index = diskIndex + 1; index < path.Length; index++)
        {
            await GetListingForExpansionAsync(current);
            if (_isClosed) return null;
            var child = current.Node.Children.FirstOrDefault(node => NodeKey(node) == path[index]);
            if (child is null || !_folderNodes.TryGetValue(child, out current)) return null;
        }
        // Never open the closest surviving ancestor in place of a missing favorite.
        return current.Disk.Id == favorite.DiskId && current.Id == favorite.FolderId ? current : null;
    }

    private void RelocateFavoritePaths(RemoteFolder source, YuWangItem item, RemoteFolder destination)
    {
        if (!item.IsFolder) return;
        string[] oldPrefix = [.. GetNodePath(source.NavigationNode), $"folder:{item.Id}"];
        string[] newPrefix = [.. GetNodePath(destination.NavigationNode), $"folder:{item.Id}"];
        var oldDisplayPrefix = GetNavigationPath(source.NavigationNode) + " / " + item.Name;
        var newDisplayPrefix = GetNavigationPath(destination.NavigationNode) + " / " + item.Name;
        var changed = false;
        foreach (var favorite in _savedState.Favorites.Where(favorite => favorite.DiskId == source.Disk.Id))
        {
            if (!favorite.NodePath.Take(oldPrefix.Length).SequenceEqual(oldPrefix)) continue;
            favorite.NodePath = [.. newPrefix, .. favorite.NodePath.Skip(oldPrefix.Length)];
            if (favorite.DisplayPath.StartsWith(oldDisplayPrefix, StringComparison.Ordinal))
                favorite.DisplayPath = newDisplayPrefix + favorite.DisplayPath[oldDisplayPrefix.Length..];
            _unavailableFavorites.Remove((favorite.DiskId, favorite.FolderId));
            changed = true;
        }
        if (changed) SaveBrowserState();
    }
}
