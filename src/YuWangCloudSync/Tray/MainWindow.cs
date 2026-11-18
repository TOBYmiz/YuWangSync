using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

/// <summary>On-demand WinUI cloud browser. The tray-hosted engine remains alive when this window closes.</summary>
internal sealed class MainWindow : Window
{
    private enum SortColumn { Name, Modified, Type, Size }

    private enum NavigationNodeKind { Root, Container, Disk, Folder }

    /// <summary>Selects a fixed-icon App.xaml template per navigation node kind.</summary>
    private sealed partial class NavigationTreeTemplateSelector(
        Func<TreeViewNode, NavigationNodeKind?> resolveKind) : DataTemplateSelector
    {
        private readonly Dictionary<NavigationNodeKind, DataTemplate?> _templates = new()
        {
            [NavigationNodeKind.Root] = WinUiUiHost.GetDataTemplateResource("YuWangNavigationRootTemplate"),
            [NavigationNodeKind.Container] = WinUiUiHost.GetDataTemplateResource("YuWangNavigationContainerTemplate"),
            [NavigationNodeKind.Disk] = WinUiUiHost.GetDataTemplateResource("YuWangNavigationDiskTemplate"),
            [NavigationNodeKind.Folder] = WinUiUiHost.GetDataTemplateResource("YuWangNavigationFolderTemplate"),
        };

        protected override DataTemplate SelectTemplateCore(object item)
        {
            var kind = item is TreeViewNode node ? resolveKind(node) : null;
            return _templates[kind ?? NavigationNodeKind.Folder]!;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }

    private sealed record RemoteFolder(DiskTreeNode Disk, ulong Id, ulong ParentId, string Name,
        DirectoryPermission Permission, TreeViewNode NavigationNode);

    private sealed class NavigationFolderState
    {
        public required DiskTreeNode Disk { get; init; }
        public required ulong Id { get; init; }
        public required ulong ParentId { get; init; }
        public required string Name { get; init; }
        public required TreeViewNode Node { get; init; }
        public DirectoryPermission Permission { get; set; } = DirectoryPermission.Unknown;
        public bool ChildrenLoaded { get; set; }
        public FolderListingResult? PendingListing { get; set; }
        public bool PendingListingConsumed { get; set; }
        public Task<FolderListingResult>? ListingTask { get; set; }

        public RemoteFolder ToRemoteFolder() => new(Disk, Id, ParentId, Name, Permission, Node);
    }

    private sealed class DiskTreeEntry
    {
        public required string Name { get; init; }
        public required TreeViewNode Node { get; init; }
        public DiskTreeNode? Model { get; init; }
        public bool IsContainer { get; init; }
    }

    private sealed class SortHeader
    {
        public required SortColumn Column { get; init; }
        public required FontIcon Chevron { get; init; }
    }

    private const double SidebarWidth = 264;
    private const double ListHorizontalMargin = 8;
    private const double ItemContentLeftPadding = 16;
    private const double ItemContentRightPadding = 12;
    private const double MultiSelectContentOffset = 32;
    private const string RootGlyph = "\uE753";
    private const string FolderGlyph = "\uE8B7";
    private const string FileGlyph = "\uE8A5";

    private readonly SyncRootContext _config;
    private readonly Grid _root;

    // Sidebar
    private readonly TreeView _navigationTree = new() { SelectionMode = TreeViewSelectionMode.Single };
    private readonly Button _reloadDisks = NewIconButton("\uE72C", "重新加载云端磁盘", 14);
    private readonly Button _settings = NewIconButton("\uE713", "设置");
    private readonly TextBlock _accountName = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _accountState = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Address bar
    private readonly Button _back = NewIconButton("\uE72B", "后退 (Alt+←)");
    private readonly Button _forward = NewIconButton("\uE72A", "前进 (Alt+→)");
    private readonly Button _up = NewIconButton("\uE74A", "上一级 (Alt+↑)");
    private readonly Button _refresh = NewIconButton("\uE72C", "刷新 (F5)");
    private readonly StackPanel _breadcrumbItems = new()
    {
        Name = "CloudLocationBreadcrumb",
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ScrollViewer _breadcrumbScroller = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ProgressBar _loading = new()
    {
        IsIndeterminate = true,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(4, 0, 4, 0),
    };
    private readonly AutoSuggestBox _search = new()
    {
        Name = "CloudSearchBox",
        PlaceholderText = "搜索当前目录",
        Width = 240,
        VerticalAlignment = VerticalAlignment.Center,
        QueryIcon = new SymbolIcon(Symbol.Find),
    };

    // Command bar
    private readonly CommandBar _commandBar = new()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
        HorizontalAlignment = HorizontalAlignment.Left,
        IsDynamicOverflowEnabled = true,
    };
    private readonly AppBarButton _newFolder = NewCommand("\uE8F4", "新建文件夹");
    private readonly AppBarButton _upload = NewCommand("\uE898", "上传");
    private readonly AppBarButton _open = NewCommand("\uE8E5", "打开");
    private readonly AppBarButton _download = NewCommand("\uE896", "下载");
    private readonly AppBarButton _rename = NewCommand("\uE8AC", "重命名");
    private readonly AppBarButton _move = NewCommand("\uE8DE", "移动");
    private readonly AppBarButton _delete = NewCommand("\uE74D", "删除");
    private readonly AppBarToggleButton _multiSelect = new()
    {
        Name = "MultiSelectCheckboxToggle",
        Label = "多选",
        Icon = new FontIcon { Glyph = "\uE762" },
    };

    // File list
    private readonly InfoBar _infoBar = new()
    {
        IsOpen = false,
        IsClosable = true,
        Margin = new Thickness(16, 0, 16, 8),
    };
    private readonly Grid _columnsHeader = new() { ColumnSpacing = 12 };
    private readonly List<SortHeader> _sortHeaders = [];
    private readonly ListView _items = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsMultiSelectCheckBoxEnabled = false,
        Margin = new Thickness(ListHorizontalMargin, 0, ListHorizontalMargin, 4),
    };
    private readonly StackPanel _emptyState = new()
    {
        Spacing = 6,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        Margin = new Thickness(24),
    };
    private readonly FontIcon _emptyGlyph = new() { FontSize = 44, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _emptyTitle = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _emptyDetail = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Border _dropOverlay = new()
    {
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(ListHorizontalMargin, 0, ListHorizontalMargin, 4),
    };
    private readonly TextBlock _dropText = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
    };

    // Status bar
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ProgressBar _transferProgress = new()
    {
        Width = 140,
        Minimum = 0,
        Maximum = 100,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border _permissionBadge = new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2, 10, 3),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly FontIcon _permissionGlyph = new() { FontSize = 12 };
    private readonly TextBlock _permissionText = new() { VerticalAlignment = VerticalAlignment.Center };

    // Context menus
    private readonly MenuFlyout _itemMenu = new();
    private readonly MenuFlyout _backgroundMenu = new();
    private readonly MenuFlyoutItem _menuOpen = NewMenuItem("\uE8E5", "打开");
    private readonly MenuFlyoutItem _menuDownload = NewMenuItem("\uE896", "下载…");
    private readonly MenuFlyoutItem _menuRename = NewMenuItem("\uE8AC", "重命名");
    private readonly MenuFlyoutItem _menuMove = NewMenuItem("\uE8DE", "移动到…");
    private readonly MenuFlyoutItem _menuDelete = NewMenuItem("\uE74D", "删除");
    private readonly MenuFlyoutItem _menuNewFolder = NewMenuItem("\uE8F4", "新建文件夹");
    private readonly MenuFlyoutItem _menuUpload = NewMenuItem("\uE898", "上传文件…");
    private readonly MenuFlyoutItem _menuRefresh = NewMenuItem("\uE72C", "刷新");

    private readonly Stack<RemoteFolder> _backHistory = new();
    private readonly Stack<RemoteFolder> _forwardHistory = new();
    private readonly List<RemoteFolder> _breadcrumbs = [];
    private readonly Dictionary<(ulong Disk, ulong Folder), RemoteFolder> _knownFolders = [];
    private readonly Dictionary<TreeViewNode, NavigationFolderState> _folderNodes = [];
    private readonly Dictionary<(ulong Disk, ulong Folder), NavigationFolderState> _folderNodesByKey = [];
    private readonly Dictionary<TreeViewNode, DiskTreeEntry> _diskTreeNodes = [];
    private readonly Dictionary<TreeViewNode, NavigationNodeKind> _nodeKinds = [];
    private readonly Dictionary<(ulong Disk, ulong Item), IStorageItem> _downloadedItems = [];
    private readonly Dictionary<string, Task<BitmapImage?>> _systemFileIconTasks = new(StringComparer.OrdinalIgnoreCase);
    private Task<BitmapImage?>? _systemFolderIconTask;
    private List<DiskTreeNode> _diskTree = [];
    private List<YuWangItem> _currentItems = [];
    private RemoteFolder? _currentFolder;
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private int _visibleCount;
    private bool _busyNow;
    private bool _suppressNavigationSelection;

    private YuWangClient? Client => Program.ClientForUi;

    internal MainWindow(SyncRootContext config)
    {
        _config = config;
        Title = $"{ProductIdentity.DisplayNameZh} - 主界面";

        _root = new Grid { Name = "MainWindowRoot" };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        _root.Children.Add(sidebar);

        var content = BuildContentSurface();
        Grid.SetColumn(content, 1);
        _root.Children.Add(content);

        BuildContextMenus();
        WireCommands();
        WinUiWindowChrome.Attach(this, _root, Title, RootGlyph);
        WinUiUiHost.ConfigureWindow(this, 1180, 760, resizable: true,
            minimumWidthDip: 900, minimumHeightDip: 560);
        _root.Loaded += (_, _) => RunUi(() => LoadDiskTreeAsync());

        RenderBreadcrumbs();
        UpdatePermissionBadge();
        RenderItems();
    }

    #region Layout

    private Grid BuildSidebar()
    {
        var sidebar = new Grid { Padding = new Thickness(8, 0, 8, 10), RowSpacing = 4 };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Padding = new Thickness(12, 2, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "云端磁盘",
            Style = WinUiUiHost.GetStyleResource("BodyStrongTextBlockStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_reloadDisks, 1);
        header.Children.Add(_reloadDisks);
        sidebar.Children.Add(header);

        _navigationTree.Name = "CloudNavigationTreeView";
        _navigationTree.ItemTemplateSelector = new NavigationTreeTemplateSelector(
            node => _nodeKinds.TryGetValue(node, out var kind) ? kind : null);
        _navigationTree.SelectionChanged += (_, args) => RunUi(() => OnNavigationSelectionChangedAsync(args));
        _navigationTree.Expanding += (_, args) => RunUi(() => OnNavigationExpandingAsync(args.Node));
        Grid.SetRow(_navigationTree, 1);
        sidebar.Children.Add(_navigationTree);

        var footer = BuildAccountFooter();
        Grid.SetRow(footer, 2);
        sidebar.Children.Add(footer);
        return sidebar;
    }

    private Grid BuildAccountFooter()
    {
        var footer = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(10, 10, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = WinUiUiHost.GetBrushResource("DividerStrokeColorDefaultBrush"),
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = WinUiUiHost.GetBrushResource("AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE77B",
                FontSize = 14,
                Foreground = WinUiUiHost.GetBrushResource("TextOnAccentFillColorPrimaryBrush"),
            },
        };
        footer.Children.Add(avatar);

        _accountName.Text = string.IsNullOrWhiteSpace(_config.Username)
            ? "育网云账号"
            : AccountNameDisplay.ForDisplay(_config.Username);
        _accountState.Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle");
        var identity = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _accountName, _accountState },
        };
        Grid.SetColumn(identity, 1);
        footer.Children.Add(identity);

        _settings.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_settings, 2);
        footer.Children.Add(_settings);
        return footer;
    }

    private Border BuildContentSurface()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // address bar
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // command bar
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // info bar
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // column header
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status bar

        var address = BuildAddressBar();
        Grid.SetRow(address, 0);
        grid.Children.Add(address);

        var commands = BuildCommandBar();
        Grid.SetRow(commands, 1);
        grid.Children.Add(commands);

        Grid.SetRow(_infoBar, 2);
        grid.Children.Add(_infoBar);

        BuildColumnsHeader();
        Grid.SetRow(_columnsHeader, 3);
        grid.Children.Add(_columnsHeader);

        _items.Name = "CloudFileListView";
        _items.SelectionChanged += (_, _) => UpdateCommandStates();
        _items.DoubleTapped += (_, _) => RunUi(() => OpenSelectedAsync());
        _items.KeyDown += OnListKeyDown;
        _items.CanDragItems = true;
        _items.DragItemsStarting += OnDragItemsStarting;
        _items.AllowDrop = true;
        _items.DragEnter += OnDragOver;
        _items.DragOver += OnDragOver;
        _items.DragLeave += (_, _) => _dropOverlay.Visibility = Visibility.Collapsed;
        _items.Drop += OnDrop;
        _items.ContextRequested += OnListBackgroundContextRequested;
        Grid.SetRow(_items, 4);
        grid.Children.Add(_items);

        _emptyGlyph.Foreground = WinUiUiHost.GetBrushResource("TextFillColorTertiaryBrush");
        _emptyTitle.Style = WinUiUiHost.GetStyleResource("BodyStrongTextBlockStyle");
        _emptyDetail.Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle");
        _emptyState.Children.Add(_emptyGlyph);
        _emptyState.Children.Add(_emptyTitle);
        _emptyState.Children.Add(_emptyDetail);
        Grid.SetRow(_emptyState, 4);
        grid.Children.Add(_emptyState);

        _dropOverlay.BorderBrush = WinUiUiHost.GetBrushResource("AccentFillColorDefaultBrush");
        _dropOverlay.Background = WinUiUiHost.GetBrushResource("SubtleFillColorSecondaryBrush");
        _dropText.Style = WinUiUiHost.GetStyleResource("BodyStrongTextBlockStyle");
        _dropText.Foreground = WinUiUiHost.GetBrushResource("AccentTextFillColorPrimaryBrush");
        _dropOverlay.Child = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon
                {
                    Glyph = "\uE898",
                    FontSize = 36,
                    Foreground = WinUiUiHost.GetBrushResource("AccentTextFillColorPrimaryBrush"),
                },
                _dropText,
            },
        };
        Grid.SetRow(_dropOverlay, 4);
        grid.Children.Add(_dropOverlay);

        var status = BuildStatusBar();
        Grid.SetRow(status, 5);
        grid.Children.Add(status);

        return new Border
        {
            Background = WinUiUiHost.GetBrushResource("LayerFillColorDefaultBrush"),
            BorderBrush = WinUiUiHost.GetBrushResource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(8, 0, 0, 0),
            Child = grid,
        };
    }

    private Grid BuildAddressBar()
    {
        var bar = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 10, 16, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _back, _forward, _up, _refresh },
        };
        bar.Children.Add(navigation);

        _breadcrumbScroller.Content = _breadcrumbItems;
        // Keep the deepest segment visible without forcing a synchronous layout pass.
        _breadcrumbItems.SizeChanged += (_, _) =>
            _breadcrumbScroller.ChangeView(_breadcrumbScroller.ScrollableWidth, null, null, true);
        var addressContent = new Grid { Children = { _breadcrumbScroller, _loading } };
        var addressBox = new Border
        {
            Height = 34,
            Padding = new Thickness(4, 0, 4, 0),
            Background = WinUiUiHost.GetBrushResource("ControlFillColorDefaultBrush"),
            BorderBrush = WinUiUiHost.GetBrushResource("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = addressContent,
        };
        Grid.SetColumn(addressBox, 1);
        bar.Children.Add(addressBox);

        Grid.SetColumn(_search, 2);
        bar.Children.Add(_search);
        return bar;
    }

    private Grid BuildCommandBar()
    {
        _commandBar.PrimaryCommands.Add(_newFolder);
        _commandBar.PrimaryCommands.Add(_upload);
        _commandBar.PrimaryCommands.Add(new AppBarSeparator());
        _commandBar.PrimaryCommands.Add(_open);
        _commandBar.PrimaryCommands.Add(_download);
        _commandBar.PrimaryCommands.Add(new AppBarSeparator());
        _commandBar.PrimaryCommands.Add(_rename);
        _commandBar.PrimaryCommands.Add(_move);
        _commandBar.PrimaryCommands.Add(_delete);
        _commandBar.PrimaryCommands.Add(new AppBarSeparator());
        _commandBar.PrimaryCommands.Add(_multiSelect);

        var host = new Grid
        {
            Padding = new Thickness(6, 0, 12, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = WinUiUiHost.GetBrushResource("DividerStrokeColorDefaultBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        host.Children.Add(_commandBar);
        return host;
    }

    private void BuildColumnsHeader()
    {
        AddFileColumns(_columnsHeader);
        UpdateColumnsHeaderMargin();
        AddSortHeader(SortColumn.Name, "名称", 0, HorizontalAlignment.Left);
        AddSortHeader(SortColumn.Modified, "修改时间", 1, HorizontalAlignment.Left);
        AddSortHeader(SortColumn.Type, "类型", 2, HorizontalAlignment.Left);
        AddSortHeader(SortColumn.Size, "大小", 3, HorizontalAlignment.Right);
        UpdateSortHeaders();
    }

    private void AddSortHeader(SortColumn column, string text, int gridColumn, HorizontalAlignment alignment)
    {
        var chevron = new FontIcon { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = text,
                    Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                chevron,
            },
        };
        if (alignment == HorizontalAlignment.Right)
        {
            // Keep the label flush with the right-aligned values; the indicator sits on its left.
            content.Children.RemoveAt(1);
            content.Children.Insert(0, chevron);
        }

        var button = new Button
        {
            Content = content,
            Style = WinUiUiHost.GetStyleResource("SubtleButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = alignment,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(-8, 0, -8, 0),
        };
        ToolTipService.SetToolTip(button, $"按{text}排序");
        button.Click += (_, _) =>
        {
            if (_sortColumn == column) _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = column;
                // Dates and sizes are most useful newest/largest first.
                _sortAscending = column is SortColumn.Name or SortColumn.Type;
            }
            UpdateSortHeaders();
            RenderItems();
        };
        Grid.SetColumn(button, gridColumn);
        _columnsHeader.Children.Add(button);
        _sortHeaders.Add(new SortHeader { Column = column, Chevron = chevron });
    }

    private void UpdateSortHeaders()
    {
        foreach (var header in _sortHeaders)
        {
            header.Chevron.Glyph = _sortAscending ? "\uE70E" : "\uE70D";
            header.Chevron.Visibility = header.Column == _sortColumn ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateColumnsHeaderMargin()
    {
        var checkboxOffset = _items.SelectionMode == ListViewSelectionMode.Multiple ? MultiSelectContentOffset : 0;
        _columnsHeader.Margin = new Thickness(
            ListHorizontalMargin + ItemContentLeftPadding + checkboxOffset, 0,
            ListHorizontalMargin + ItemContentRightPadding, 2);
    }

    private static void AddFileColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
    }

    private Grid BuildStatusBar()
    {
        var bar = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(16, 6, 16, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = WinUiUiHost.GetBrushResource("DividerStrokeColorDefaultBrush"),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _summary.Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle");
        bar.Children.Add(_summary);

        _status.Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle");
        Grid.SetColumn(_status, 1);
        bar.Children.Add(_status);

        Grid.SetColumn(_transferProgress, 2);
        bar.Children.Add(_transferProgress);

        _permissionText.Style = WinUiUiHost.GetStyleResource("CaptionTextBlockStyle");
        _permissionBadge.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _permissionGlyph, _permissionText },
        };
        Grid.SetColumn(_permissionBadge, 3);
        bar.Children.Add(_permissionBadge);
        return bar;
    }

    private void BuildContextMenus()
    {
        _menuOpen.FontWeight = FontWeights.SemiBold;
        _itemMenu.Items.Add(_menuOpen);
        _itemMenu.Items.Add(_menuDownload);
        _itemMenu.Items.Add(new MenuFlyoutSeparator());
        _itemMenu.Items.Add(_menuRename);
        _itemMenu.Items.Add(_menuMove);
        _itemMenu.Items.Add(_menuDelete);

        _backgroundMenu.Items.Add(_menuNewFolder);
        _backgroundMenu.Items.Add(_menuUpload);
        _backgroundMenu.Items.Add(new MenuFlyoutSeparator());
        _backgroundMenu.Items.Add(_menuRefresh);

        // Shortcuts are handled by the list and address bar; the menu only displays them.
        _menuOpen.KeyboardAcceleratorTextOverride = "Enter";
        _menuDelete.KeyboardAcceleratorTextOverride = "Delete";
        _menuRename.KeyboardAcceleratorTextOverride = "F2";
        _menuRefresh.KeyboardAcceleratorTextOverride = "F5";
    }

    private static Button NewIconButton(string glyph, string tooltip, double glyphSize = 16)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = glyphSize },
            Style = WinUiUiHost.GetStyleResource("SubtleButtonStyle"),
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden,
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static AppBarButton NewCommand(string glyph, string label)
    {
        var button = new AppBarButton { Label = label, Icon = new FontIcon { Glyph = glyph } };
        ToolTipService.SetToolTip(button, label);
        return button;
    }

    private static MenuFlyoutItem NewMenuItem(string glyph, string text)
        => new() { Text = text, Icon = new FontIcon { Glyph = glyph } };

    #endregion

    private void WireCommands()
    {
        _back.Click += (_, _) => RunUi(() => GoBackAsync());
        _up.Click += (_, _) => RunUi(() => GoUpAsync());
        _forward.Click += (_, _) => RunUi(() => GoForwardAsync());
        _refresh.Click += (_, _) => RunUi(() => RefreshAsync());
        _reloadDisks.Click += (_, _) => RunUi(() => LoadDiskTreeAsync());
        _settings.Click += (_, _) => WinUiUiHost.ShowSettings(Program.SnapshotConfig());

        _back.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu });
        _forward.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Right, Modifiers = VirtualKeyModifiers.Menu });
        _up.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Up, Modifiers = VirtualKeyModifiers.Menu });
        _refresh.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.F5 });
        var focusSearch = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        focusSearch.Invoked += (_, args) =>
        {
            args.Handled = true;
            _search.Focus(FocusState.Keyboard);
        };
        _root.KeyboardAccelerators.Add(focusSearch);
        _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        _upload.Click += (_, _) => RunUi(() => ChooseFilesToUploadAsync());
        _newFolder.Click += (_, _) => RunUi(() => CreateFolderAsync());
        _open.Click += (_, _) => RunUi(() => OpenSelectedAsync());
        _download.Click += (_, _) => RunUi(() => DownloadSelectionAsync());
        _rename.Click += (_, _) => RunUi(() => RenameSelectionAsync());
        _move.Click += (_, _) => RunUi(() => MoveSelectionAsync());
        _delete.Click += (_, _) => RunUi(() => DeleteSelectionAsync());
        _multiSelect.Checked += (_, _) => SetCheckboxSelectionEnabled(true);
        _multiSelect.Unchecked += (_, _) => SetCheckboxSelectionEnabled(false);
        _search.TextChanged += (_, _) => RenderItems();

        _menuOpen.Click += (_, _) => RunUi(() => OpenSelectedAsync());
        _menuDownload.Click += (_, _) => RunUi(() => DownloadSelectionAsync());
        _menuRename.Click += (_, _) => RunUi(() => RenameSelectionAsync());
        _menuMove.Click += (_, _) => RunUi(() => MoveSelectionAsync());
        _menuDelete.Click += (_, _) => RunUi(() => DeleteSelectionAsync());
        _menuNewFolder.Click += (_, _) => RunUi(() => CreateFolderAsync());
        _menuUpload.Click += (_, _) => RunUi(() => ChooseFilesToUploadAsync());
        _menuRefresh.Click += (_, _) => RunUi(() => RefreshAsync());
    }

    private async Task RefreshAsync()
    {
        if (_currentFolder is null) await LoadDiskTreeAsync();
        else await RefreshCurrentFolderAsync();
    }

    #region Navigation tree

    private async Task LoadDiskTreeAsync()
    {
        var client = Client;
        if (client is null || !client.IsLoggedIn)
        {
            UpdateAccountState();
            ShowInfo(InfoBarSeverity.Informational, "正在连接育网云",
                "登录完成后即可浏览云端磁盘。", "重试", () => RunUi(LoadDiskTreeAsync));
            return;
        }

        _infoBar.IsOpen = false;
        await RunBusyAsync("正在加载云端磁盘…", async () =>
        {
            _diskTree = await client.FetchDiskTreeAsync(_config.RootContainerTypeId);
            _navigationTree.RootNodes.Clear();
            _folderNodes.Clear();
            _folderNodesByKey.Clear();
            _diskTreeNodes.Clear();
            _nodeKinds.Clear();
            _knownFolders.Clear();
            _backHistory.Clear();
            _forwardHistory.Clear();
            _breadcrumbs.Clear();
            _currentFolder = null;
            _currentItems = [];

            var root = CreateNavigationNode("育网云", NavigationNodeKind.Root);
            root.IsExpanded = true;
            _navigationTree.RootNodes.Add(root);
            _diskTreeNodes[root] = new DiskTreeEntry { Name = "育网云", Node = root, IsContainer = true };
            AddDiskTreeChildren(root, _config.RootContainerTypeId, []);

            var diskCount = _diskTree.Count(node => node.IsDisk);
            if (diskCount == 0)
                ShowInfo(InfoBarSeverity.Warning, "未找到云端磁盘", "请检查账号、服务器和磁盘树入口 TypeId。");
            SetStatus(diskCount == 0 ? "" : $"已加载 {diskCount} 个云端磁盘");
            RenderBreadcrumbs();
            UpdatePermissionBadge();
            RenderItems();
        });

        // Open the first disk so the window is immediately useful.
        if (_currentFolder is null && FindFirstDisk(_navigationTree.RootNodes) is { } firstDisk)
            await NavigateToFolderAsync(firstDisk, addToHistory: false);
    }

    private NavigationFolderState? FindFirstDisk(IList<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (_folderNodes.TryGetValue(node, out var state)) return state;
            if (FindFirstDisk(node.Children) is { } nested) return nested;
        }
        return null;
    }

    private void AddDiskTreeChildren(TreeViewNode parentNode, ulong parentId, HashSet<ulong> ancestors)
    {
        foreach (var model in _diskTree.Where(node => node.ParentId == parentId)
                     .OrderBy(node => node.IsDisk ? 1 : 0)
                     .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (model.IsContainer)
            {
                if (!ancestors.Add(model.Id)) continue;
                var node = CreateNavigationNode(model.Name, NavigationNodeKind.Container);
                node.IsExpanded = true;
                parentNode.Children.Add(node);
                _diskTreeNodes[node] = new DiskTreeEntry { Name = model.Name, Node = node, Model = model, IsContainer = true };
                AddDiskTreeChildren(node, model.Id, ancestors);
                ancestors.Remove(model.Id);
                continue;
            }

            if (!model.IsDisk) continue;
            var diskNode = CreateNavigationNode(model.Name, NavigationNodeKind.Disk);
            diskNode.HasUnrealizedChildren = true;
            var state = new NavigationFolderState
            {
                Disk = model,
                Id = 0,
                ParentId = 0,
                Name = model.Name,
                Node = diskNode,
            };
            parentNode.Children.Add(diskNode);
            RegisterFolderNode(state);
        }
    }

    /// <summary>
    /// TreeViewNode.Content stays a plain string, the content type the stock node-mode template
    /// path is designed for. The icon comes from a fixed App.xaml template chosen by
    /// <see cref="NavigationTreeTemplateSelector"/>; nothing UI-typed is bound into the tree.
    /// </summary>
    private TreeViewNode CreateNavigationNode(string name, NavigationNodeKind kind)
    {
        var node = new TreeViewNode { Content = name };
        _nodeKinds[node] = kind;
        return node;
    }

    private void RegisterFolderNode(NavigationFolderState state)
    {
        _folderNodes[state.Node] = state;
        _folderNodesByKey[(state.Disk.Id, state.Id)] = state;
        _diskTreeNodes[state.Node] = new DiskTreeEntry { Name = state.Name, Node = state.Node, Model = state.Disk };
    }

    private async Task OnNavigationSelectionChangedAsync(TreeViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigationSelection || _busyNow) return;
        var selectedNode = args.AddedItems.OfType<TreeViewNode>().FirstOrDefault();
        if (selectedNode is null) return;
        if (!_folderNodes.TryGetValue(selectedNode, out var state))
        {
            // Groupings are not browsable: toggle them instead and keep the current selection.
            selectedNode.IsExpanded = !selectedNode.IsExpanded;
            if (_currentFolder is { } current) SetNavigationSelection(current.NavigationNode);
            else SetNavigationSelection(null);
            return;
        }
        await NavigateToFolderAsync(state, addToHistory: true);
    }

    private async Task OnNavigationExpandingAsync(TreeViewNode node)
    {
        if (!_folderNodes.TryGetValue(node, out var state) || state.ChildrenLoaded) return;
        try
        {
            await GetListingForExpansionAsync(state);
        }
        catch (Exception error)
        {
            node.HasUnrealizedChildren = true;
            Serilog.Log.Warning(error, "展开主界面目录导航失败：{Folder}", state.Name);
            ShowInfo(InfoBarSeverity.Error, $"无法加载“{state.Name}”的子文件夹", error.Message);
        }
    }

    private async Task NavigateToFolderAsync(NavigationFolderState state, bool addToHistory)
    {
        if (_busyNow) return;
        var target = state.ToRemoteFolder();
        if (_currentFolder is { } current && current.Disk.Id == target.Disk.Id && current.Id == target.Id)
            return;

        if (addToHistory && _currentFolder is { } previous)
        {
            _backHistory.Push(previous);
            _forwardHistory.Clear();
        }

        _currentFolder = target;
        for (var parent = state.Node.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        SetNavigationSelection(state.Node);
        RebuildBreadcrumbs(state.Node);
        RememberFolder(target);
        _search.Text = "";
        _search.PlaceholderText = $"搜索“{target.Name}”";
        await LoadCurrentFolderAsync();
    }

    private void SetNavigationSelection(TreeViewNode? node)
    {
        _suppressNavigationSelection = true;
        try
        {
            _navigationTree.SelectedNodes.Clear();
            if (node is not null) _navigationTree.SelectedNodes.Add(node);
        }
        finally { _suppressNavigationSelection = false; }
    }

    private void RebuildBreadcrumbs(TreeViewNode node)
    {
        var path = new List<RemoteFolder>();
        for (TreeViewNode? current = node; current is not null; current = current.Parent)
            if (_folderNodes.TryGetValue(current, out var state)) path.Add(state.ToRemoteFolder());
        path.Reverse();
        _breadcrumbs.Clear();
        _breadcrumbs.AddRange(path);
        RenderBreadcrumbs();
    }

    /// <summary>Clickable address bar built from the navigation tree ancestry of the current folder.</summary>
    private void RenderBreadcrumbs()
    {
        _breadcrumbItems.Children.Clear();
        var segments = new List<(string Name, TreeViewNode? Node)>();
        if (_currentFolder is { } folder)
        {
            for (TreeViewNode? current = folder.NavigationNode; current is not null; current = current.Parent)
            {
                if (_folderNodes.TryGetValue(current, out var state)) segments.Add((state.Name, current));
                else if (_diskTreeNodes.TryGetValue(current, out var grouping)) segments.Add((grouping.Name, current));
            }
            segments.Reverse();
        }
        else segments.Add(("育网云", null));

        _breadcrumbItems.Children.Add(new FontIcon
        {
            Glyph = RootGlyph,
            FontSize = 14,
            Margin = new Thickness(6, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WinUiUiHost.GetBrushResource("TextFillColorSecondaryBrush"),
        });

        for (var index = 0; index < segments.Count; index++)
        {
            var (name, node) = segments[index];
            var isLast = index == segments.Count - 1;
            if (index > 0)
            {
                _breadcrumbItems.Children.Add(new FontIcon
                {
                    Glyph = "\uE76C",
                    FontSize = 9,
                    Margin = new Thickness(2, 1, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = WinUiUiHost.GetBrushResource("TextFillColorTertiaryBrush"),
                });
            }

            var label = new TextBlock
            {
                Text = name,
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isLast) label.FontWeight = FontWeights.SemiBold;
            MenuFlyout? groupingMenu = null;
            var isFolder = node is not null && _folderNodes.ContainsKey(node);
            if (!isLast && !isFolder && node is not null) groupingMenu = BuildGroupingMenu(node);
            if (isLast || (!isFolder && groupingMenu is null))
            {
                label.Margin = new Thickness(6, 0, 6, 0);
                if (!isLast) label.Foreground = WinUiUiHost.GetBrushResource("TextFillColorSecondaryBrush");
                ToolTipService.SetToolTip(label, name);
                _breadcrumbItems.Children.Add(label);
                continue;
            }

            var segment = new Button
            {
                Content = label,
                Style = WinUiUiHost.GetStyleResource("SubtleButtonStyle"),
                Padding = new Thickness(6, 3, 6, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(segment, name);
            if (groupingMenu is not null)
            {
                // Groupings are not browsable folders; offer their disks instead.
                segment.Flyout = groupingMenu;
            }
            else
            {
                var destination = node!;
                segment.Click += (_, _) => RunUi(() => NavigateToNodeAsync(destination));
            }
            _breadcrumbItems.Children.Add(segment);
        }

        var fullPath = string.Join(" / ", segments.Select(segment => segment.Name));
        ToolTipService.SetToolTip(_breadcrumbScroller, fullPath);
    }

    private MenuFlyout? BuildGroupingMenu(TreeViewNode grouping)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        AddGroupingMenuItems(menu.Items, grouping);
        return menu.Items.Count == 0 ? null : menu;
    }

    private void AddGroupingMenuItems(IList<MenuFlyoutItemBase> items, TreeViewNode parent)
    {
        foreach (var child in parent.Children)
        {
            if (_folderNodes.TryGetValue(child, out var state))
            {
                var item = new MenuFlyoutItem
                {
                    Text = state.Name,
                    Icon = new FontIcon { Glyph = state.Id == 0 ? "\uEDA2" : FolderGlyph },
                };
                var destination = child;
                item.Click += (_, _) => RunUi(() => NavigateToNodeAsync(destination));
                items.Add(item);
            }
            else if (_diskTreeNodes.TryGetValue(child, out var grouping))
            {
                var sub = new MenuFlyoutSubItem { Text = grouping.Name, Icon = new FontIcon { Glyph = "\uE8F1" } };
                AddGroupingMenuItems(sub.Items, child);
                if (sub.Items.Count > 0) items.Add(sub);
            }
        }
    }

    private async Task NavigateToNodeAsync(TreeViewNode node)
    {
        if (_folderNodes.TryGetValue(node, out var state))
            await NavigateToFolderAsync(state, addToHistory: true);
    }

    /// <summary>Runs an async UI handler and surfaces failures instead of letting async void crash or vanish.</summary>
    private async void RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "主界面事件处理失败");
            try { ShowInfo(InfoBarSeverity.Error, "操作失败", error.Message); } catch { }
        }
    }

    private string GetNavigationPath(TreeViewNode node)
    {
        var path = new Stack<string>();
        for (TreeViewNode? current = node; current is not null; current = current.Parent)
        {
            if (_folderNodes.TryGetValue(current, out var folder)) path.Push(folder.Name);
            else if (_diskTreeNodes.TryGetValue(current, out var grouping)) path.Push(grouping.Name);
        }
        return string.Join(" / ", path);
    }

    #endregion

    #region Folder listings

    private async Task LoadCurrentFolderAsync()
    {
        if (_currentFolder is not { } folder) return;
        var client = Client;
        if (client is null || !client.IsLoggedIn)
        {
            ShowInfo(InfoBarSeverity.Warning, "育网云会话不可用", "请等待登录完成后重试。",
                "重试", () => RunUi(LoadCurrentFolderAsync));
            return;
        }

        _currentItems = [];
        RenderItems();
        await RunBusyAsync($"正在加载“{folder.Name}”…", async () =>
        {
            if (!_folderNodes.TryGetValue(folder.NavigationNode, out var state))
                throw new InvalidOperationException("导航树中的目标文件夹已失效，请刷新后重试。");
            var listing = await GetListingForNavigationAsync(state);
            ApplyCurrentFolderListing(folder, state, listing);
        });
    }

    private async Task RefreshCurrentFolderAsync()
    {
        if (_currentFolder is not { } folder) return;
        var client = Client;
        if (client is null || !client.IsLoggedIn)
        {
            ShowInfo(InfoBarSeverity.Warning, "育网云会话不可用", "请等待登录完成后重试。");
            return;
        }

        await RunBusyAsync($"正在刷新“{folder.Name}”…", async () =>
            await RefreshCurrentFolderCoreAsync(client, folder));
    }

    private async Task RefreshCurrentFolderCoreAsync(YuWangClient client, RemoteFolder folder)
    {
        if (!_folderNodes.TryGetValue(folder.NavigationNode, out var state))
            throw new InvalidOperationException("导航树中的目标文件夹已失效，请刷新后重试。");
        var listing = await GetFreshFolderListingAsync(client, state);
        state.PendingListingConsumed = true;
        ApplyCurrentFolderListing(folder, state, listing);
    }

    private async Task<FolderListingResult> GetListingForNavigationAsync(NavigationFolderState state)
    {
        if (state.ListingTask is not null)
        {
            var inFlight = await AwaitFolderListingTaskAsync(state);
            state.PendingListingConsumed = true;
            return inFlight;
        }
        if (state.PendingListing is { } prefetched && !state.PendingListingConsumed)
        {
            state.PendingListingConsumed = true;
            return prefetched;
        }

        var listing = await GetFreshFolderListingAsync(Client
            ?? throw new InvalidOperationException("Cloud client 尚未初始化"), state);
        state.PendingListingConsumed = true;
        return listing;
    }

    private async Task<FolderListingResult> GetListingForExpansionAsync(NavigationFolderState state)
    {
        if (state.ChildrenLoaded)
            return state.PendingListing ?? new FolderListingResult([], state.Permission, true);
        if (state.ListingTask is not null) return await AwaitFolderListingTaskAsync(state);
        if (state.PendingListing is { } prefetched) return prefetched;

        var client = Client ?? throw new InvalidOperationException("Cloud client 尚未初始化");
        return await GetFreshFolderListingAsync(client, state);
    }

    private async Task<FolderListingResult> GetFreshFolderListingAsync(YuWangClient client, NavigationFolderState state)
    {
        if (state.ListingTask is null)
            state.ListingTask = FetchAndStoreFolderListingAsync(client, state);
        return await AwaitFolderListingTaskAsync(state);
    }

    private async Task<FolderListingResult> AwaitFolderListingTaskAsync(NavigationFolderState state)
    {
        var task = state.ListingTask
            ?? throw new InvalidOperationException("目录清单请求状态丢失。");
        try { return await task; }
        finally
        {
            if (ReferenceEquals(state.ListingTask, task)) state.ListingTask = null;
        }
    }

    private async Task<FolderListingResult> FetchAndStoreFolderListingAsync(YuWangClient client,
        NavigationFolderState state)
    {
        var listing = await client.ListFolderWithPermissionsAsync(state.Disk.Id, state.Id);
        if (!listing.IsValidPage)
            throw new IOException("服务器未返回完整的目标目录列表。");

        Callbacks.StoreMainWindowDirectoryListing(
            new FileIdentity(state.Disk.Id, state.ParentId, state.Id, 0),
            state.ParentId, state.Name, listing.Items, listing.Permission);
        state.Permission = Program.GetCachedDirectoryPermission(state.Disk.Id, state.Id);
        state.PendingListing = listing;
        state.PendingListingConsumed = false;
        PopulateFolderChildren(state, listing.Items);
        RememberFolder(state.ToRemoteFolder());
        return listing;
    }

    private void PopulateFolderChildren(NavigationFolderState parent,
        IReadOnlyList<YuWangItem> items)
    {
        foreach (var oldChild in parent.Node.Children.ToArray())
            RemoveFolderNode(oldChild);
        parent.Node.Children.Clear();

        foreach (var item in items.Where(item => item.IsFolder)
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = CreateNavigationNode(item.Name, NavigationNodeKind.Folder);
            node.HasUnrealizedChildren = true;
            var state = new NavigationFolderState
            {
                Disk = parent.Disk,
                Id = item.Id,
                ParentId = parent.Id,
                Name = item.Name,
                Node = node,
            };
            parent.Node.Children.Add(node);
            RegisterFolderNode(state);
        }

        parent.ChildrenLoaded = true;
        parent.Node.HasUnrealizedChildren = false;
    }

    private void RemoveFolderNode(TreeViewNode node)
    {
        foreach (var child in node.Children.ToArray()) RemoveFolderNode(child);
        _nodeKinds.Remove(node);
        _diskTreeNodes.Remove(node);
        if (!_folderNodes.Remove(node, out var state)) return;
        _folderNodesByKey.Remove((state.Disk.Id, state.Id));
        _knownFolders.Remove((state.Disk.Id, state.Id));
    }

    private void ApplyCurrentFolderListing(RemoteFolder folder, NavigationFolderState state,
        FolderListingResult listing)
    {
        _currentItems = listing.Items.ToList();
        state.Permission = Program.GetCachedDirectoryPermission(folder.Disk.Id, folder.Id);
        var loadedFolder = state.ToRemoteFolder();
        _currentFolder = loadedFolder;
        RebuildBreadcrumbs(state.Node);
        RememberFolder(loadedFolder);
        UpdatePermissionBadge();
        RenderItems();
        SetStatus("");
    }

    #endregion

    #region File list

    private void SetCheckboxSelectionEnabled(bool enabled)
    {
        _items.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        _items.IsMultiSelectCheckBoxEnabled = enabled;
        if (!enabled && _items.SelectedItems.Count > 1)
        {
            var first = _items.SelectedItems.OfType<ListViewItem>().FirstOrDefault();
            _items.SelectedItems.Clear();
            if (first is not null) first.IsSelected = true;
        }
        UpdateColumnsHeaderMargin();
        UpdateCommandStates();
    }

    private IEnumerable<YuWangItem> SortItems(IEnumerable<YuWangItem> items)
    {
        // Folders always stay above files, like File Explorer.
        var grouped = items.OrderBy(item => item.IsFolder ? 0 : 1);
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var sorted = (_sortColumn, _sortAscending) switch
        {
            (SortColumn.Modified, true) => grouped.ThenBy(item => item.UploadTime ?? DateTime.MinValue),
            (SortColumn.Modified, false) => grouped.ThenByDescending(item => item.UploadTime ?? DateTime.MinValue),
            (SortColumn.Type, true) => grouped.ThenBy(GetTypeLabel, comparer),
            (SortColumn.Type, false) => grouped.ThenByDescending(GetTypeLabel, comparer),
            (SortColumn.Size, true) => grouped.ThenBy(item => item.Size ?? -1),
            (SortColumn.Size, false) => grouped.ThenByDescending(item => item.Size ?? -1),
            (_, false) => grouped.ThenByDescending(item => item.Name, comparer),
            _ => grouped.ThenBy(item => item.Name, comparer),
        };
        return _sortColumn == SortColumn.Name ? sorted : sorted.ThenBy(item => item.Name, comparer);
    }

    private void RenderItems()
    {
        _items.Items.Clear();
        var filter = _search.Text?.Trim() ?? "";
        var visible = SortItems(_currentItems
                .Where(item => filter.Length == 0 || item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        foreach (var item in visible)
            _items.Items.Add(CreateItemRow(item));

        _visibleCount = visible.Count;
        UpdateEmptyState(filter);
        UpdateCommandStates();
    }

    private ListViewItem CreateItemRow(YuWangItem item)
    {
        var secondary = WinUiUiHost.GetBrushResource("TextFillColorSecondaryBrush");
        var row = new Grid { ColumnSpacing = 12 };
        AddFileColumns(row);

        var iconHost = new Border
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = item.IsFolder ? FolderGlyph : FileGlyph, FontSize = 18 },
        };
        _ = LoadSystemIconIntoAsync(iconHost,
            item.IsFolder ? GetSystemFolderIconAsync() : GetSystemFileIconAsync(item.Name), 20);
        var namePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                iconHost,
                new TextBlock
                {
                    Text = item.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        row.Children.Add(namePanel);

        AddRowCell(row, 1, item.UploadTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "", secondary, TextAlignment.Left);
        AddRowCell(row, 2, GetTypeLabel(item), secondary, TextAlignment.Left);
        AddRowCell(row, 3, item.IsFolder ? "" : FormatSize(item.Size), secondary, TextAlignment.Right);

        var listItem = new ListViewItem
        {
            Content = row,
            Tag = item,
            MinHeight = 36,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(listItem, item.Name);
        listItem.ContextRequested += (_, args) => OnItemContextRequested(listItem, args);
        return listItem;
    }

    private static void AddRowCell(Grid row, int column, string text, Brush? foreground, TextAlignment alignment)
    {
        var cell = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
    }

    private static string GetTypeLabel(YuWangItem item)
    {
        if (item.IsFolder) return "文件夹";
        var extension = Path.GetExtension(item.Name).TrimStart('.');
        return extension.Length is > 0 and <= 12 ? $"{extension.ToUpperInvariant()} 文件" : "文件";
    }

    private void UpdateEmptyState(string filter)
    {
        var hasFolder = _currentFolder is not null;
        var loading = _busyNow && _currentItems.Count == 0;
        if (_visibleCount > 0 || loading)
        {
            _emptyState.Visibility = Visibility.Collapsed;
            _columnsHeader.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _emptyState.Visibility = Visibility.Visible;
        _columnsHeader.Visibility = hasFolder && _currentItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!hasFolder)
        {
            _emptyGlyph.Glyph = RootGlyph;
            _emptyTitle.Text = "选择一个云端磁盘";
            _emptyDetail.Text = "从左侧列表中选择磁盘或文件夹即可开始浏览。";
        }
        else if (filter.Length > 0)
        {
            _emptyGlyph.Glyph = "\uE721";
            _emptyTitle.Text = $"没有与“{filter}”匹配的项目";
            _emptyDetail.Text = "仅搜索当前文件夹中的名称。";
        }
        else
        {
            _emptyGlyph.Glyph = FolderGlyph;
            _emptyTitle.Text = "此文件夹为空";
            _emptyDetail.Text = CanWrite ? "将文件或文件夹拖放到此处，或使用“上传”添加内容。" : "";
        }
    }

    private async Task LoadSystemIconIntoAsync(Border iconHost, Task<BitmapImage?> iconTask, double size)
    {
        var icon = await iconTask;
        if (icon is null) return;
        iconHost.Child = new Image
        {
            Source = icon,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
        };
    }

    private Task<BitmapImage?> GetSystemFileIconAsync(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 20
            || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
            extension = ".file";
        if (!_systemFileIconTasks.TryGetValue(extension, out var task))
            _systemFileIconTasks[extension] = task = CreateSystemFileIconAsync(extension);
        return task;
    }

    private Task<BitmapImage?> GetSystemFolderIconAsync()
        => _systemFolderIconTask ??= CreateSystemFolderIconAsync();

    private static string SystemIconProbeDirectory()
    {
        var probeDirectory = Path.Combine(Path.GetTempPath(), ProductIdentity.Identifier, "system-file-icons");
        Directory.CreateDirectory(probeDirectory);
        return probeDirectory;
    }

    private static async Task<BitmapImage?> CreateSystemFileIconAsync(string extension)
    {
        string? probePath = null;
        try
        {
            probePath = Path.Combine(SystemIconProbeDirectory(), "file" + extension);
            if (!File.Exists(probePath)) await File.WriteAllBytesAsync(probePath, []);
            var probeFile = await StorageFile.GetFileFromPathAsync(probePath);
            using var thumbnail = await probeFile.GetThumbnailAsync(ThumbnailMode.ListView, 32);
            return await ToIconImageAsync(thumbnail);
        }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "读取系统文件类型图标失败：{Extension}", extension);
            return null;
        }
        finally
        {
            if (probePath is not null)
            {
                try { File.Delete(probePath); }
                catch (Exception error) { Serilog.Log.Debug(error, "清理系统图标临时文件失败：{Path}", probePath); }
            }
        }
    }

    private static async Task<BitmapImage?> CreateSystemFolderIconAsync()
    {
        try
        {
            var probePath = Path.Combine(SystemIconProbeDirectory(), "folder");
            Directory.CreateDirectory(probePath);
            var probeFolder = await StorageFolder.GetFolderFromPathAsync(probePath);
            using var thumbnail = await probeFolder.GetThumbnailAsync(ThumbnailMode.ListView, 32);
            return await ToIconImageAsync(thumbnail);
        }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "读取系统文件夹图标失败");
            return null;
        }
    }

    private static async Task<BitmapImage?> ToIconImageAsync(StorageItemThumbnail? thumbnail)
    {
        if (thumbnail is null || thumbnail.Type != ThumbnailType.Icon) return null;
        var image = new BitmapImage();
        await image.SetSourceAsync(thumbnail);
        return image;
    }

    private static string FormatSize(long? size)
    {
        if (size is null) return "";
        var value = (double)size.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private bool CanWrite => _currentFolder is not null && !_busyNow
        && _currentFolder.Permission == DirectoryPermission.FullAccess;

    private void UpdateCommandStates()
    {
        var selected = SelectedEntries();
        var canWrite = CanWrite;
        var idle = !_busyNow;
        _navigationTree.IsEnabled = idle;
        _reloadDisks.IsEnabled = idle;
        _multiSelect.IsEnabled = idle && _currentFolder is not null;
        _back.IsEnabled = idle && _backHistory.Count > 0;
        _up.IsEnabled = idle && _breadcrumbs.Count > 1;
        _forward.IsEnabled = idle && _forwardHistory.Count > 0;
        _refresh.IsEnabled = idle;
        _upload.IsEnabled = canWrite;
        _newFolder.IsEnabled = canWrite;
        _open.IsEnabled = idle && selected.Count == 1;
        _download.IsEnabled = idle && selected.Count > 0;
        _rename.IsEnabled = canWrite && selected.Count == 1;
        _move.IsEnabled = canWrite && selected.Count > 0;
        _delete.IsEnabled = canWrite && selected.Count > 0;

        _menuOpen.IsEnabled = _open.IsEnabled;
        _menuDownload.IsEnabled = _download.IsEnabled;
        _menuRename.IsEnabled = _rename.IsEnabled;
        _menuMove.IsEnabled = _move.IsEnabled;
        _menuDelete.IsEnabled = _delete.IsEnabled;
        _menuNewFolder.IsEnabled = canWrite;
        _menuUpload.IsEnabled = canWrite;
        _menuRefresh.IsEnabled = idle;

        UpdateSummary(selected);
        UpdateAccountState();
    }

    private void UpdateSummary(List<YuWangItem> selected)
    {
        if (_currentFolder is null)
        {
            _summary.Text = "";
            return;
        }
        var text = _visibleCount == _currentItems.Count
            ? $"{_currentItems.Count} 个项目"
            : $"显示 {_visibleCount} / {_currentItems.Count} 个项目";
        if (selected.Count > 0)
        {
            text += $"  ·  已选择 {selected.Count} 项";
            var bytes = selected.Where(item => !item.IsFolder).Sum(item => item.Size ?? 0);
            if (bytes > 0) text += $"（{FormatSize(bytes)}）";
        }
        _summary.Text = text;
    }

    private void UpdateAccountState()
    {
        var client = Client;
        _accountState.Text = client is { IsLoggedIn: true } ? "已连接" : "正在连接…";
    }

    private void UpdatePermissionBadge()
    {
        if (_currentFolder is not { } folder)
        {
            _permissionBadge.Visibility = Visibility.Collapsed;
            return;
        }

        _permissionBadge.Visibility = Visibility.Visible;
        var (glyph, text, background, foreground, tooltip) = folder.Permission switch
        {
            DirectoryPermission.FullAccess => ("\uE70F", "可写",
                "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush",
                "可以在此文件夹中上传、新建、重命名、移动和删除。"),
            DirectoryPermission.ReadOnly => ("\uE72E", "只读",
                "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush",
                "此文件夹为只读，仅可浏览和下载。"),
            _ => ("\uE9CE", "权限未确认",
                "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush",
                "未能确认此文件夹的权限，写入操作已停用。"),
        };
        _permissionGlyph.Glyph = glyph;
        _permissionGlyph.Foreground = WinUiUiHost.GetBrushResource(foreground);
        _permissionText.Text = text;
        _permissionBadge.Background = WinUiUiHost.GetBrushResource(background);
        ToolTipService.SetToolTip(_permissionBadge, tooltip);
    }

    private List<YuWangItem> SelectedEntries() => _items.SelectedItems
        .OfType<ListViewItem>()
        .Select(item => item.Tag)
        .OfType<YuWangItem>()
        .ToList();

    private void OnItemContextRequested(ListViewItem container, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        if (!container.IsSelected)
        {
            _items.SelectedItems.Clear();
            container.IsSelected = true;
        }
        UpdateCommandStates();
        ShowContextMenu(_itemMenu, container, args);
    }

    private void OnListBackgroundContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || _currentFolder is null) return;
        args.Handled = true;
        UpdateCommandStates();
        ShowContextMenu(_backgroundMenu, _items, args);
    }

    private static void ShowContextMenu(MenuFlyout menu, UIElement target, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(target, out var point))
            menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(target as FrameworkElement ?? throw new InvalidOperationException("菜单目标无效。"));
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Enter:
                args.Handled = true;
                RunUi(OpenSelectedAsync);
                break;
            case VirtualKey.Delete:
                args.Handled = true;
                RunUi(DeleteSelectionAsync);
                break;
            case VirtualKey.F2:
                args.Handled = true;
                RunUi(RenameSelectionAsync);
                break;
            case VirtualKey.Back:
                args.Handled = true;
                RunUi(GoBackAsync);
                break;
        }
    }

    private async Task OpenSelectedAsync()
    {
        var selected = SelectedEntries();
        var parent = _currentFolder;
        if (selected.Count != 1 || parent is null) return;
        var item = selected[0];
        if (item.IsFolder)
        {
            if (_folderNodesByKey.TryGetValue((parent.Disk.Id, item.Id), out var state))
                await NavigateToFolderAsync(state, addToHistory: true);
            else
                ShowInfo(InfoBarSeverity.Warning, $"无法打开“{item.Name}”", "该文件夹尚未加入导航树，请刷新当前目录后重试。");
            return;
        }

        await RunBusyAsync($"正在打开“{item.Name}”…", async () =>
        {
            var openRoot = Path.Combine(Path.GetTempPath(), ProductIdentity.Identifier, "opened-files");
            Directory.CreateDirectory(openRoot);
            var path = await DownloadFileAsync(parent.Disk.Id, item, openRoot);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"已下载并打开“{item.Name}”");
        });
    }

    #endregion

    #region History

    private async Task GoBackAsync()
    {
        if (_busyNow || _backHistory.Count == 0 || _currentFolder is null) return;
        var target = _backHistory.Pop();
        _forwardHistory.Push(_currentFolder);
        if (_folderNodes.TryGetValue(target.NavigationNode, out var state))
            await NavigateToFolderAsync(state, addToHistory: false);
    }

    private async Task GoForwardAsync()
    {
        if (_busyNow || _forwardHistory.Count == 0 || _currentFolder is null) return;
        var target = _forwardHistory.Pop();
        _backHistory.Push(_currentFolder);
        if (_folderNodes.TryGetValue(target.NavigationNode, out var state))
            await NavigateToFolderAsync(state, addToHistory: false);
    }

    private async Task GoUpAsync()
    {
        if (_busyNow || _breadcrumbs.Count <= 1 || _currentFolder is null) return;
        var target = _breadcrumbs[^2];
        _backHistory.Push(_currentFolder);
        _forwardHistory.Clear();
        if (_folderNodes.TryGetValue(target.NavigationNode, out var state))
            await NavigateToFolderAsync(state, addToHistory: false);
    }

    #endregion

    #region Upload & drag-drop

    private async Task ChooseFilesToUploadAsync()
    {
        if (_currentFolder is null) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            await UploadStorageItemsAsync(files.Cast<IStorageItem>().ToArray());
        }
        catch (Exception error) { ShowInfo(InfoBarSeverity.Error, "选择文件失败", error.Message); }
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        var canDrop = CanWrite && args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = canDrop ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (!canDrop)
        {
            _dropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var caption = $"上传到“{_currentFolder!.Name}”";
        args.DragUIOverride.Caption = caption;
        _dropText.Text = $"释放以{caption}";
        _dropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        var selected = SelectedEntries();
        var folder = _currentFolder;
        if (folder is null || selected.Count == 0)
        {
            args.Cancel = true;
            return;
        }

        var storageItems = new List<IStorageItem>();
        try
        {
            foreach (var item in selected)
            {
                if (!_downloadedItems.TryGetValue((folder.Disk.Id, item.Id), out var storageItem)
                    || (!File.Exists(storageItem.Path) && !Directory.Exists(storageItem.Path)))
                {
                    args.Cancel = true;
                    ShowInfo(InfoBarSeverity.Informational, "请先下载",
                        $"先下载“{item.Name}”，再将其拖到资源管理器。");
                    return;
                }
                storageItems.Add(storageItem);
            }

            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetStorageItems(storageItems, readOnly: false);
            SetStatus("正在向资源管理器传递已下载项目");
        }
        catch (Exception error)
        {
            args.Cancel = true;
            ShowInfo(InfoBarSeverity.Error, "无法开始拖放", error.Message);
        }
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        _dropOverlay.Visibility = Visibility.Collapsed;
        if (!CanWrite || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var storageItems = await args.DataView.GetStorageItemsAsync();
            await UploadStorageItemsAsync(storageItems);
        }
        catch (Exception error) { ShowInfo(InfoBarSeverity.Error, "拖放上传失败", error.Message); }
    }

    private async Task UploadStorageItemsAsync(IReadOnlyList<IStorageItem> items)
    {
        var folder = _currentFolder;
        var client = Client;
        if (folder is null || folder.Permission != DirectoryPermission.FullAccess
            || client is null || !client.IsLoggedIn)
        {
            ShowInfo(InfoBarSeverity.Warning, "无法上传", "云端服务尚未就绪。");
            return;
        }
        await RunBusyAsync($"正在上传 {items.Count} 项…", async () =>
        {
            var errors = new List<string>();
            foreach (var item in items)
            {
                try { await UploadStorageItemAsync(client, folder.Disk.Id, folder.Id, item); }
                catch (Exception error) { errors.Add($"{item.Name}：{error.Message}"); }
            }
            await RefreshCurrentFolderCoreAsync(client, folder);
            if (errors.Count == 0) SetStatus($"已上传 {items.Count} 项");
            else ShowInfo(InfoBarSeverity.Warning, $"上传完成，有 {errors.Count} 项失败",
                string.Join("；", errors.Take(3)));
        });
    }

    private async Task UploadStorageItemAsync(YuWangClient client, ulong diskId, ulong folderId, IStorageItem storageItem)
    {
        Program.ApplicationCancellationToken.ThrowIfCancellationRequested();
        if (storageItem is StorageFolder localFolder)
        {
            var existing = await client.ListFolderAsync(diskId, folderId);
            var sameName = existing.FirstOrDefault(item => item.IsFolder
                && item.Name.Equals(localFolder.Name, StringComparison.OrdinalIgnoreCase));
            var remoteFolder = sameName ?? await client.CreateRemoteFolderAsync(diskId, folderId, localFolder.Name,
                Program.ApplicationCancellationToken);
            foreach (var child in await localFolder.GetItemsAsync())
                await UploadStorageItemAsync(client, diskId, remoteFolder.Id, child);
            return;
        }

        if (storageItem is not StorageFile localFile)
            throw new IOException("不支持此类型的拖放项目。");
        await using var stream = new FileStream(localFile.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, Program.ApplicationCancellationToken);
        stream.Position = 0;
        SetTransferProgress($"正在上传“{localFile.Name}”…", 0);
        await client.UploadNewFileAsync(diskId, folderId, localFile.Name, stream,
            File.GetLastWriteTimeUtc(localFile.Path), hash, Program.ApplicationCancellationToken,
            progress: progress =>
            {
                var percent = progress.TotalBytes <= 0 ? 0 : (int)(progress.SentBytes * 100 / progress.TotalBytes);
                percent = Math.Clamp(percent, 0, 100);
                SetTransferProgress($"正在上传“{localFile.Name}”… {percent}%", percent);
            });
    }

    #endregion

    #region Write operations

    private async Task CreateFolderAsync()
    {
        var parent = _currentFolder;
        var client = Client;
        if (parent is null || parent.Permission != DirectoryPermission.FullAccess
            || client is null || !client.IsLoggedIn) return;
        var nameBox = new TextBox { PlaceholderText = "文件夹名称", MinWidth = 320 };
        var result = await ShowInputDialogAsync("新建文件夹", nameBox, "创建");
        var name = nameBox.Text.Trim();
        if (result != ContentDialogResult.Primary || name.Length == 0) return;
        await RunBusyAsync("正在创建文件夹…", async () =>
        {
            await client.CreateRemoteFolderAsync(parent.Disk.Id, parent.Id, name,
                Program.ApplicationCancellationToken);
            await RefreshCurrentFolderCoreAsync(client, parent);
            SetStatus($"已创建文件夹“{name}”");
        });
    }

    private async Task RenameSelectionAsync()
    {
        var selected = SelectedEntries();
        var parent = _currentFolder;
        var client = Client;
        if (selected.Count != 1 || parent is null || parent.Permission != DirectoryPermission.FullAccess
            || client is null || !client.IsLoggedIn || _busyNow) return;
        var item = selected[0];
        var nameBox = new TextBox { Text = item.Name, MinWidth = 320 };
        nameBox.Loaded += (_, _) =>
        {
            // Pre-select the stem so typing replaces the name but keeps the extension.
            var stemLength = item.IsFolder ? item.Name.Length : Path.GetFileNameWithoutExtension(item.Name).Length;
            nameBox.Select(0, stemLength > 0 ? stemLength : item.Name.Length);
        };
        if (await ShowInputDialogAsync("重命名", nameBox, "保存") != ContentDialogResult.Primary) return;
        var newName = nameBox.Text.Trim();
        if (newName.Length == 0 || newName == item.Name) return;
        await RunBusyAsync("正在重命名…", async () =>
        {
            if (!await client.RenameRemoteItemAsync(parent.Disk.Id, parent.Id, item.Id, item.IsFolder, null, newName))
                throw new IOException("服务器未确认重命名。");
            await RefreshCurrentFolderCoreAsync(client, parent);
            SetStatus($"已重命名为“{newName}”");
        });
    }

    private async Task MoveSelectionAsync()
    {
        var selected = SelectedEntries();
        var source = _currentFolder;
        var client = Client;
        if (selected.Count == 0 || source is null || source.Permission != DirectoryPermission.FullAccess
            || client is null || !client.IsLoggedIn) return;
        var choices = _knownFolders.Values
            .Where(folder => folder.Disk.Id == source.Disk.Id
                && folder.Id != source.Id
                && folder.Permission == DirectoryPermission.FullAccess
                && !selected.Any(item => item.IsFolder && item.Id == folder.Id))
            .Select(folder => (Folder: folder, Path: GetNavigationPath(folder.NavigationNode)))
            .OrderBy(choice => choice.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "没有可用的目标文件夹",
                "只能移动到当前磁盘中已浏览过、且可写的文件夹。请先在左侧展开目标文件夹。");
            return;
        }

        var targetPicker = new ComboBox { MinWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (folder, path) in choices)
            targetPicker.Items.Add(new ComboBoxItem { Content = path, Tag = folder });
        targetPicker.SelectedIndex = 0;
        var dialogContent = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = selected.Count == 1 ? $"将“{selected[0].Name}”移动到：" : $"将选中的 {selected.Count} 项移动到：",
                    TextWrapping = TextWrapping.Wrap,
                },
                targetPicker,
                new TextBlock
                {
                    Text = "列表中仅包含已浏览过的可写文件夹。",
                    Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle"),
                },
            },
        };
        if (await ShowInputDialogAsync("移动", dialogContent, "移动") != ContentDialogResult.Primary) return;
        if (targetPicker.SelectedItem is not ComboBoxItem { Tag: RemoteFolder target } || target.Id == source.Id) return;

        await RunBusyAsync("正在移动…", async () =>
        {
            foreach (var item in selected)
            {
                if (!await client.RenameRemoteItemAsync(source.Disk.Id, source.Id, item.Id, item.IsFolder,
                        target.Id, item.Name))
                    throw new IOException($"服务器未确认移动“{item.Name}”。");
            }
            await RefreshCurrentFolderCoreAsync(client, source);
            SetStatus($"已移动 {selected.Count} 项到“{target.Name}”");
        });
    }

    private async Task DeleteSelectionAsync()
    {
        var selected = SelectedEntries();
        var parent = _currentFolder;
        var client = Client;
        if (selected.Count == 0 || parent is null || parent.Permission != DirectoryPermission.FullAccess
            || client is null || !client.IsLoggedIn || _busyNow) return;
        var prompt = selected.Count == 1 ? $"删除“{selected[0].Name}”？" : $"删除这 {selected.Count} 项？";
        if (await ShowConfirmDialogAsync(prompt, "删除会作用于云端，此操作无法撤销。", "删除") != ContentDialogResult.Primary) return;
        await RunBusyAsync($"正在删除 {selected.Count} 项…", async () =>
        {
            var failures = new List<string>();
            foreach (var item in selected)
            {
                if (!await client.DeleteRemoteItemAsync(parent.Disk.Id, parent.Id, item.Id, item.IsFolder))
                    failures.Add(item.Name);
            }
            await RefreshCurrentFolderCoreAsync(client, parent);
            if (failures.Count == 0) SetStatus($"已删除 {selected.Count} 项");
            else ShowInfo(InfoBarSeverity.Warning, "部分项目未能删除", "服务器未确认：" + string.Join("、", failures));
        });
    }

    #endregion

    #region Download

    private async Task DownloadSelectionAsync()
    {
        var selected = SelectedEntries();
        var parent = _currentFolder;
        if (selected.Count == 0 || parent is null || _busyNow) return;
        var owner = WindowNative.GetWindowHandle(this);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloadsFolder = Path.Combine(userProfile, "Downloads");
        if (!Directory.Exists(downloadsFolder)) downloadsFolder = userProfile;
        var destination = await FolderPicker.PickAsync(owner, "选择下载位置", downloadsFolder);
        if (string.IsNullOrWhiteSpace(destination)) return;
        await RunBusyAsync($"正在下载 {selected.Count} 项…", async () =>
        {
            foreach (var item in selected)
                await DownloadEntryAsync(parent.Disk.Id, item, destination);
            SetStatus($"已下载 {selected.Count} 项到“{destination}”");
            ShowInfo(InfoBarSeverity.Success, "下载完成", $"已下载 {selected.Count} 项到“{destination}”。",
                "打开文件夹", () => Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }));
        });
    }

    private async Task DownloadEntryAsync(ulong diskId, YuWangItem item, string destination)
    {
        Program.ApplicationCancellationToken.ThrowIfCancellationRequested();
        if (!item.IsFolder)
        {
            await DownloadFileAsync(diskId, item, destination);
            return;
        }
        var folderPath = GetAvailablePath(destination, SafeFileName(item.Name), directory: true);
        Directory.CreateDirectory(folderPath);
        try { _downloadedItems[(diskId, item.Id)] = await StorageFolder.GetFolderFromPathAsync(folderPath); }
        catch (Exception error) { Serilog.Log.Debug(error, "无法为已下载文件夹准备 Explorer 拖放对象：{Path}", folderPath); }
        var children = await Client!.ListFolderAsync(diskId, item.Id);
        foreach (var child in children)
            await DownloadEntryAsync(diskId, child, folderPath);
    }

    private async Task<string> DownloadFileAsync(ulong diskId, YuWangItem item, string destination)
    {
        var client = Client ?? throw new InvalidOperationException("育网云客户端尚未就绪。");
        var probe = await client.ProbeRealSizeAsync(item.Id, item.Name, item.Size)
            ?? throw new IOException($"无法获取“{item.Name}”的下载地址或文件大小。");
        var folder = Path.GetFullPath(destination);
        Directory.CreateDirectory(folder);
        var finalPath = GetAvailablePath(folder, SafeFileName(item.Name), directory: false);
        var temporaryPath = finalPath + ".yw-download-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long offset = 0;
            SetTransferProgress($"正在下载“{item.Name}”…", 0);
            while (offset < probe.Size)
            {
                Program.ApplicationCancellationToken.ThrowIfCancellationRequested();
                var requested = Math.Min(4 * 1024 * 1024, probe.Size - offset);
                var range = await client.DownloadRangeDirectAsync(probe.DirectUrl, offset, requested);
                if (range is null || range.Value.Data.Length == 0)
                    throw new EndOfStreamException($"“{item.Name}”下载提前结束。");
                await output.WriteAsync(range.Value.Data, Program.ApplicationCancellationToken);
                offset += range.Value.Data.Length;
                SetTransferProgress($"正在下载“{item.Name}”… {FormatSize(offset)} / {FormatSize(probe.Size)}",
                    probe.Size <= 0 ? 100 : offset * 100d / probe.Size);
            }
            await output.FlushAsync(Program.ApplicationCancellationToken);
            output.Close();
            File.Move(temporaryPath, finalPath);
            try { _downloadedItems[(diskId, item.Id)] = await StorageFile.GetFileFromPathAsync(finalPath); }
            catch (Exception error) { Serilog.Log.Debug(error, "无法为已下载文件准备 Explorer 拖放对象：{Path}", finalPath); }
            if (item.UploadTime is { } modified) File.SetLastWriteTimeUtc(finalPath, modified.ToUniversalTime());
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static string GetAvailablePath(string parent, string name, bool directory)
    {
        var path = Path.Combine(parent, name);
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = directory ? "" : Path.GetExtension(name);
        for (var index = 2; ; index++)
        {
            path = Path.Combine(parent, $"{stem} ({index}){extension}");
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(clean) ? "未命名" : clean;
    }

    #endregion

    #region Busy state, status and dialogs

    private void RememberFolder(RemoteFolder folder)
    {
        _knownFolders[(folder.Disk.Id, folder.Id)] = folder;
    }

    private async Task RunBusyAsync(string initialStatus, Func<Task> action)
    {
        if (_busyNow) return;
        _busyNow = true;
        if (_infoBar.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning) _infoBar.IsOpen = false;
        _loading.Visibility = Visibility.Visible;
        SetStatus(initialStatus);
        UpdateCommandStates();
        UpdateEmptyState(_search.Text?.Trim() ?? "");
        try { await action(); }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "主界面操作失败");
            SetStatus("");
            ShowInfo(InfoBarSeverity.Error, "操作失败", error.Message);
        }
        finally
        {
            _loading.Visibility = Visibility.Collapsed;
            _transferProgress.Visibility = Visibility.Collapsed;
            _busyNow = false;
            UpdateCommandStates();
            UpdateEmptyState(_search.Text?.Trim() ?? "");
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void SetTransferProgress(string text, double percent)
    {
        _status.Text = text;
        _transferProgress.Value = Math.Clamp(percent, 0, 100);
        _transferProgress.Visibility = Visibility.Visible;
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message,
        string? actionText = null, Action? action = null)
    {
        _infoBar.Severity = severity;
        _infoBar.Title = title;
        _infoBar.Message = message;
        if (actionText is not null && action is not null)
        {
            var button = new Button { Content = actionText };
            button.Click += (_, _) =>
            {
                _infoBar.IsOpen = false;
                try { action(); }
                catch (Exception error) { Serilog.Log.Warning(error, "主界面通知操作失败"); }
            };
            _infoBar.ActionButton = button;
        }
        else _infoBar.ActionButton = null;
        _infoBar.IsOpen = true;
    }

    private async Task<ContentDialogResult> ShowInputDialogAsync(string title, UIElement content, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        dialog.Style = WinUiUiHost.GetStyleResource("DefaultContentDialogStyle");
        return await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowConfirmDialogAsync(string title, string detail, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = detail,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        dialog.Style = WinUiUiHost.GetStyleResource("DefaultContentDialogStyle");
        return await dialog.ShowAsync();
    }

    #endregion
}
