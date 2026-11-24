using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
// CsWinRT also requires containing types to be partial before it can generate the
// nested NavigationTreeTemplateSelector's WinRT interfaces for NativeAOT.
internal sealed partial class MainWindow : Window
{
    private enum SortColumn { Name, Modified, Type, Size }

    private enum NavigationNodeKind { Root, Container, Disk, Folder }

    /// <summary>Selects a fixed-icon App.xaml template per navigation node kind.</summary>
    private sealed partial class NavigationTreeTemplateSelector(
        Func<TreeViewNode, NavigationNodeKind?> resolveKind) : DataTemplateSelector
    {
        private readonly HashSet<NavigationNodeKind> _tracedKinds = [];
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
            var resolvedKind = kind ?? NavigationNodeKind.Folder;
            if (_tracedKinds.Add(resolvedKind))
                TraceUiStage($"首次选择导航模板：{resolvedKind}; ItemType={item?.GetType().FullName ?? "null"}");
            return _templates[resolvedKind]!;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }

    private sealed record RemoteFolder(DiskTreeNode Disk, ulong Id, ulong ParentId, string Name,
        DirectoryPermission Permission, TreeViewNode NavigationNode);

    // A menu keeps its own target so a navigation-tree click never acts on the list selection.
    private sealed record ItemActionTarget(RemoteFolder Parent, IReadOnlyList<YuWangItem> Items);

    private sealed class NavigationFolderState
    {
        public required DiskTreeNode Disk { get; init; }
        public required ulong Id { get; init; }
        public required ulong ParentId { get; init; }
        public required string Name { get; set; }
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
    private const double MinimumSidebarWidth = 200;
    private const double MaximumSidebarWidth = 480;
    private const double MinimumContentWidth = 560;
    private const double SidebarGripWidth = 8;
    private const double ListHorizontalMargin = 8;
    private const double ItemContentLeftPadding = 16;
    private const double ItemContentRightPadding = 12;
    private const double MultiSelectContentOffset = 32;
    private const string RootGlyph = "\uE753";
    private const string FolderGlyph = "\uE8B7";
    private const string FileGlyph = "\uE8A5";
    private const double StatusBarContentHeight = 22;

    private readonly SyncRootContext _config;
    private readonly Grid _root;
    private readonly ColumnDefinition _sidebarColumn;
    private readonly MainWindowState _savedState;
    private double _preferredSidebarWidth;
    private bool _isClosed;
    private bool _resizingSidebar;
    private double _resizeStartX;
    private double _resizeStartWidth;

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
    private readonly Grid _fileSurface = new();
    private readonly ScrollViewer _fileScroller = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    // A zero name width means fill the available space until explicitly resized.
    private readonly double[] _fileColumnWidths = [0, 62, 140, 110, 90];
    private static readonly double[] MinimumFileColumnWidths = [160, 46, 100, 90, 70];
    private static readonly double[] MaximumFileColumnWidths = [1600, 200, 480, 640, 400];
    private readonly List<Grid> _fileRows = [];
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
        // Right-aligned next to the progress bar so it never runs into the item summary.
        HorizontalAlignment = HorizontalAlignment.Right,
        TextAlignment = TextAlignment.Right,
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
    private readonly Button _cancelDragPreparation = new()
    {
        Content = new FontIcon { Glyph = "\uE711", FontSize = 10, FontFamily = new FontFamily("Segoe MDL2 Assets") },
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center,
        Width = 24,
        Height = 22,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        // Overhang into the bar padding instead of growing the status row.
        Margin = new Thickness(0, -2, 0, -2),
    };
    private readonly DispatcherTimer _statusClearTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _infoBarCloseTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _statusIsProgress;
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
    private readonly MenuFlyoutItem _menuDownload = NewMenuItem("\uE896", "下载");
    private readonly MenuFlyoutItem _menuSaveAs = NewMenuItem("\uE792", "另存为…");
    private readonly MenuFlyoutItem _menuFavorite = NewMenuItem("\uE734", "添加到收藏");
    private readonly MenuFlyoutItem _menuRename = NewMenuItem("\uE8AC", "重命名");
    private readonly MenuFlyoutItem _menuMove = NewMenuItem("\uE8DE", "移动到…");
    private readonly MenuFlyoutItem _menuDelete = NewMenuItem("\uE74D", "删除");
    private readonly MenuFlyoutItem _menuNewFolder = NewMenuItem("\uE8F4", "新建文件夹");
    private readonly MenuFlyoutItem _menuUpload = NewMenuItem("\uE898", "上传文件…");
    private readonly MenuFlyoutItem _menuRefresh = NewMenuItem("\uE72C", "刷新");
    private ItemActionTarget? _itemMenuTarget;
    private const long NavigationMenuDebounceMilliseconds = 400;
    private long _navigationMenuShownAt = long.MinValue / 2;

    private readonly Stack<TreeViewNode> _backHistory = new();
    private readonly Stack<TreeViewNode> _forwardHistory = new();
    private readonly Dictionary<(ulong Disk, ulong Folder), RemoteFolder> _knownFolders = [];
    private readonly Dictionary<TreeViewNode, NavigationFolderState> _folderNodes = [];
    private readonly Dictionary<(ulong Disk, ulong Folder), NavigationFolderState> _folderNodesByKey = [];
    private readonly Dictionary<TreeViewNode, DiskTreeEntry> _diskTreeNodes = [];
    private readonly Dictionary<TreeViewNode, NavigationNodeKind> _nodeKinds = [];
    private readonly Dictionary<(ulong Disk, ulong Item), string> _downloadedPaths = [];
    private ListViewItem? _dragSourceRow;
    private const string BrowserDragSource = "YuWangCloudSync.CloudBrowser";
    private readonly Dictionary<string, Task<BitmapImage?>> _systemFileIconTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _systemFileTypes = new(StringComparer.OrdinalIgnoreCase);
    private string _systemFolderType = "文件夹";
    private Task<BitmapImage?>? _systemFolderIconTask;
    private List<DiskTreeNode> _diskTree = [];
    private List<YuWangItem> _currentItems = [];
    private RemoteFolder? _currentFolder;
    private TreeViewNode? _currentNode;
    private TreeViewNode? _pendingSelection;
    private TreeViewNode? _pendingCollapsedNode;
    private bool _navigationChangeQueued;
    private bool _updatingNavigationTree;
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private int _visibleCount;
    private bool _busyNow;
    private bool _suppressNavigationSelection;

    // Kept as plain managed data so the unhandled-exception path need not access XAML objects.
    private static string _lastDiagnosticStage = "尚未打开主界面";
    internal static string LastDiagnosticStage => Volatile.Read(ref _lastDiagnosticStage);

    private static void TraceUiStage(string stage)
    {
        Volatile.Write(ref _lastDiagnosticStage, stage);
        try
        {
            var threadId = Environment.CurrentManagedThreadId;
            Serilog.Log.Information("[MAIN-UI] {Stage}; ThreadId={ThreadId}; IsUiThread={IsUiThread}",
                stage, threadId, threadId == WinUiUiHost.UiThreadId);
        }
        catch { }
    }

    private YuWangClient? Client => Program.ClientForUi;

    internal MainWindow(SyncRootContext config, bool loadDiskTree = true)
    {
        TraceUiStage("开始构造主界面");
        _config = config;
        _savedState = MainWindowStateStore.Load(config);
        var widths = _savedState.ColumnWidths;
        if (widths.Length == 4) widths = [widths[0], 0, widths[1], widths[2], widths[3]]; // Pre-status-column layout.
        if (widths.Length == _fileColumnWidths.Length)
            for (var index = 0; index < widths.Length; index++)
                if (double.IsFinite(widths[index]) && widths[index] > 0)
                    _fileColumnWidths[index] = Math.Clamp(widths[index],
                        MinimumFileColumnWidths[index], MaximumFileColumnWidths[index]);
        _preferredSidebarWidth = double.IsFinite(_savedState.SidebarWidth)
            ? Math.Clamp(_savedState.SidebarWidth, MinimumSidebarWidth, MaximumSidebarWidth) : SidebarWidth;
        Title = $"{ProductIdentity.DisplayNameZh} - 主界面";

        _root = new Grid { Name = "MainWindowRoot" };
        _sidebarColumn = new ColumnDefinition
        {
            Width = new GridLength(_preferredSidebarWidth),
            MinWidth = MinimumSidebarWidth,
            MaxWidth = MaximumSidebarWidth,
        };
        _root.ColumnDefinitions.Add(_sidebarColumn);
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarGripWidth) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        _root.Children.Add(sidebar);
        var resizeGrip = BuildSidebarResizeGrip();
        Grid.SetColumn(resizeGrip, 1);
        _root.Children.Add(resizeGrip);

        var content = BuildContentSurface();
        Grid.SetColumn(content, 2);
        _root.Children.Add(content);

        BuildContextMenus();
        WireCommands();
        WinUiWindowChrome.Attach(this, _root, Title, RootGlyph);
        WinUiUiHost.ConfigureWindow(this, 1180, 760, resizable: true,
            minimumWidthDip: 900, minimumHeightDip: 560);
        if (loadDiskTree) _root.Loaded += (_, _) => RunUi(() => LoadDiskTreeAsync());
        _root.SizeChanged += (_, _) => ConstrainSidebarWidth();
        Closed += (_, _) =>
        {
            _isClosed = true;
            StopFavoritesAnimation();
            StopInfoBarAnimation();
            StopInfoBarCountdown();
            _statusClearTimer.Stop();
            _infoBarCloseTimer.Stop();
            _dragIntentTimer.Stop();
            _fileSortTimer.Stop();
            _fileStatusTimer.Stop();
            _syncDownloadCancellation?.Cancel();
            _dragCancellation?.Cancel();
            CancelDragWarmup();
            try { SaveBrowserState(); }
            catch (Exception error) { Serilog.Log.Warning(error, "关闭主界面时保存浏览状态失败"); }
        };

        RenderBreadcrumbs();
        UpdatePermissionBadge();
        RenderItems();
        TraceUiStage("主界面构造完成，等待 Loaded");
    }

    #region Layout

    private Grid BuildSidebarResizeGrip()
    {
        var grip = new ResizeGrip(InputSystemCursorShape.SizeWestEast)
        {
            Name = "NavigationResizeGrip",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children =
            {
                new Border
                {
                    Width = 1,
                    Background = WinUiUiHost.GetBrushResource("DividerStrokeColorDefaultBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
        ToolTipService.SetToolTip(grip, "拖动调整导航栏宽度");
        grip.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(_root);
            if (!point.Properties.IsLeftButtonPressed || !grip.CapturePointer(args.Pointer)) return;
            _resizingSidebar = true;
            _resizeStartX = point.Position.X;
            _resizeStartWidth = _sidebarColumn.ActualWidth;
            args.Handled = true;
        };
        grip.PointerMoved += (_, args) =>
        {
            if (!_resizingSidebar) return;
            _preferredSidebarWidth = Math.Clamp(
                _resizeStartWidth + args.GetCurrentPoint(_root).Position.X - _resizeStartX,
                MinimumSidebarWidth, _sidebarColumn.MaxWidth);
            ConstrainSidebarWidth();
            args.Handled = true;
        };
        grip.PointerReleased += (_, args) =>
        {
            _resizingSidebar = false;
            grip.ReleasePointerCapture(args.Pointer);
        };
        grip.PointerCaptureLost += (_, _) => _resizingSidebar = false;
        grip.PointerCanceled += (_, _) => _resizingSidebar = false;
        return grip;
    }

    private void ConstrainSidebarWidth()
    {
        if (_root.ActualWidth <= 0) return;
        _sidebarColumn.MaxWidth = Math.Clamp(_root.ActualWidth - MinimumContentWidth - SidebarGripWidth,
            MinimumSidebarWidth, MaximumSidebarWidth);
        _sidebarColumn.Width = new GridLength(Math.Clamp(_preferredSidebarWidth,
            MinimumSidebarWidth, _sidebarColumn.MaxWidth));
    }

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
        // Child rows get an explicit per-expansion animation (MainWindow.Animations.cs). A theme
        // add/delete transition kept collapsed rows "deleting" and skipped a quick re-expand.
        _navigationTree.ItemContainerTransitions = new TransitionCollection
        {
            new RepositionThemeTransition(),
        };
        _navigationTree.SelectionChanged += (_, _) => QueueNavigationSelection();
        _navigationTree.Expanding += (_, args) =>
        {
            QueueNavigationExpandAnimation(args.Node);
            RunUi(() => OnNavigationExpandingAsync(args.Node));
        };
        _navigationTree.Collapsed += (_, args) =>
        {
            CancelNavigationExpandAnimation(args.Node);
            OnNavigationCollapsed(args.Node);
        };
        _navigationTree.ContextRequested += OnNavigationContextRequested;
        // TreeViewItem can consume the routed right-tap before ContextRequested reaches TreeView.
        _navigationTree.AddHandler(UIElement.RightTappedEvent,
            new RightTappedEventHandler(OnNavigationRightTapped), handledEventsToo: true);
        var panes = BuildSidebarPanes();
        Grid.SetRow(panes, 1);
        sidebar.Children.Add(panes);

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

        Grid.SetRow(_infoBarHost, 2);
        grid.Children.Add(_infoBarHost);
        _infoBarHost.RenderTransform = _infoBarTranslation;
        _infoBarHost.SizeChanged += (_, _) => _infoBarHost.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, _infoBarHost.ActualWidth, _infoBarHost.ActualHeight),
        };
        _infoBarHost.Children.Add(_infoBar);
        _infoBarCountdown.Margin = _infoBar.Margin;
        _infoBarHost.Children.Add(_infoBarCountdown);
        _infoBar.Closing += OnInfoBarClosing;
        _infoBar.Closed += (_, _) => ResetClosedInfoBar();

        BuildColumnsHeader();
        _fileSurface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _fileSurface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _fileSurface.Children.Add(_columnsHeader);

        _items.Name = "CloudFileListView";
        _fileSortTimer.Tick += (_, _) => ApplyPendingFileSort();
        _fileStatusTimer.Tick += (_, _) => RefreshFileAvailability();
        _fileStatusTimer.Start();
        _items.SelectionChanged += (_, _) =>
        {
            if (_replacingFileRows) return;
            UpdateCommandStates();
            QueueDragPreparation();
        };
        _items.DoubleTapped += (_, _) => RunUi(() => OpenSelectedAsync());
        _items.KeyDown += OnListKeyDown;
        _items.CanDragItems = false;
        _dragIntentTimer.Tick += (_, _) => CheckDragIntent();
        _dragProgressTimer.Tick += (_, _) => UpdateDragPreparationProgress();
        _items.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((_, args) =>
            {
                if (_dragSourceRow is not null) _dragPointer = args.GetCurrentPoint(_items);
                CheckDragIntent();
            }), handledEventsToo: true);
        _items.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnFileListPointerPressed), handledEventsToo: true);
        _items.AllowDrop = true;
        _items.DragEnter += OnDragOver;
        _items.DragOver += OnDragOver;
        _items.DragLeave += (_, _) => _dropOverlay.Visibility = Visibility.Collapsed;
        _items.Drop += OnDrop;
        _items.ContextRequested += OnListBackgroundContextRequested;
        Grid.SetRow(_items, 1);
        _fileSurface.Children.Add(_items);
        _fileScroller.Content = _fileSurface;
        _fileScroller.SizeChanged += (_, _) => UpdateFileColumnWidths();
        Grid.SetRow(_fileScroller, 3);
        Grid.SetRowSpan(_fileScroller, 2);
        grid.Children.Add(_fileScroller);

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
        var statusHeader = new TextBlock
        {
            Text = "状态", VerticalAlignment = VerticalAlignment.Center,
            Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle"),
        };
        Grid.SetColumn(statusHeader, 1);
        _columnsHeader.Children.Add(statusHeader);
        AddFileColumnGrip("状态", 1);
        AddSortHeader(SortColumn.Modified, "修改时间", 2, HorizontalAlignment.Left);
        AddSortHeader(SortColumn.Type, "类型", 3, HorizontalAlignment.Left);
        AddSortHeader(SortColumn.Size, "大小", 4, HorizontalAlignment.Right);
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"按{text}排序");
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
            QueueFileSort();
        };
        Grid.SetColumn(button, gridColumn);
        _columnsHeader.Children.Add(button);
        AddFileColumnGrip(text, gridColumn);
        _sortHeaders.Add(new SortHeader { Column = column, Chevron = chevron });
    }

    private void AddFileColumnGrip(string text, int gridColumn)
    {
        var grip = new ResizeGrip(InputSystemCursorShape.SizeWestEast)
        {
            Name = $"FileColumnResizeGrip{gridColumn}",
            Width = 8,
            Margin = new Thickness(0, 0, -6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children =
            {
                new Border
                {
                    Width = 1, Margin = new Thickness(0, 6, 0, 6),
                    Background = WinUiUiHost.GetBrushResource("DividerStrokeColorDefaultBrush"),
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grip, $"调整“{text}”列宽");
        var dragging = false;
        var startX = 0d;
        var startWidth = 0d;
        grip.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(_root);
            if (!point.Properties.IsLeftButtonPressed || !grip.CapturePointer(args.Pointer)) return;
            dragging = true;
            startX = point.Position.X;
            startWidth = _columnsHeader.ColumnDefinitions[gridColumn].Width.Value;
            args.Handled = true;
        };
        grip.PointerMoved += (_, args) =>
        {
            if (!dragging) return;
            _fileColumnWidths[gridColumn] = Math.Clamp(startWidth + args.GetCurrentPoint(_root).Position.X - startX,
                MinimumFileColumnWidths[gridColumn], MaximumFileColumnWidths[gridColumn]);
            UpdateFileColumnWidths();
            args.Handled = true;
        };
        grip.PointerReleased += (_, args) => { dragging = false; grip.ReleasePointerCapture(args.Pointer); };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        grip.PointerCanceled += (_, _) => dragging = false;
        Grid.SetColumn(grip, gridColumn);
        _columnsHeader.Children.Add(grip);
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
        UpdateFileColumnWidths();
    }

    private void AddFileColumns(Grid grid)
    {
        for (var index = 0; index < _fileColumnWidths.Length; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EffectiveFileColumnWidth(index)) });
        if (!ReferenceEquals(grid, _columnsHeader)) _fileRows.Add(grid);
    }

    private double FileColumnChromeWidth => ListHorizontalMargin * 2 + ItemContentLeftPadding
        + ItemContentRightPadding + _columnsHeader.ColumnSpacing * (_fileColumnWidths.Length - 1) + 20
        + (_items.SelectionMode == ListViewSelectionMode.Multiple ? MultiSelectContentOffset : 0);

    private double EffectiveFileColumnWidth(int index) => index == 0 && _fileColumnWidths[0] == 0
        ? Math.Max(MinimumFileColumnWidths[0], _fileScroller.ActualWidth - FileColumnChromeWidth
            - _fileColumnWidths.Skip(1).Sum())
        : _fileColumnWidths[index];

    private void UpdateFileColumnWidths()
    {
        if (_isClosed || _columnsHeader.ColumnDefinitions.Count != _fileColumnWidths.Length) return;
        var widths = Enumerable.Range(0, _fileColumnWidths.Length).Select(EffectiveFileColumnWidth).ToArray();
        _fileSurface.Width = Math.Max(_fileScroller.ActualWidth, widths.Sum() + FileColumnChromeWidth);
        for (var index = 0; index < widths.Length; index++)
        {
            var width = new GridLength(widths[index]);
            _columnsHeader.ColumnDefinitions[index].Width = width;
            foreach (var row in _fileRows) row.ColumnDefinitions[index].Width = width;
        }
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
        // Every item fits the permission badge height, so progress, long names or the cancel
        // button never make the status bar taller than its idle height.
        bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(StatusBarContentHeight) });
        // summary | status | progress | cancel | permission
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer.Stop();
            if (!_isClosed && !_statusIsProgress && _transferProgress.Visibility != Visibility.Visible) _status.Text = "";
        };
        _infoBarCloseTimer.Tick += (_, _) =>
        {
            _infoBarCloseTimer.Stop();
            if (!_isClosed && _infoBar.Severity == InfoBarSeverity.Success) _infoBar.IsOpen = false;
        };

        _summary.Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle");
        bar.Children.Add(_summary);

        _status.Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle");
        _status.TextWrapping = TextWrapping.NoWrap; // CaptionTextBlockStyle wraps by default.
        _status.MaxLines = 1;
        Grid.SetColumn(_status, 1);
        bar.Children.Add(_status);

        _cancelDragPreparation.Style = WinUiUiHost.GetStyleResource("SubtleButtonStyle");
        _cancelDragPreparation.Click += (_, _) => CancelActiveDragPreparation();
        ToolTipService.SetToolTip(_cancelDragPreparation, "取消准备文件");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_cancelDragPreparation, "取消准备文件");
        Grid.SetColumn(_cancelDragPreparation, 3);
        bar.Children.Add(_cancelDragPreparation);

        Grid.SetColumn(_transferProgress, 2);
        bar.Children.Add(_transferProgress);

        _permissionText.Style = WinUiUiHost.GetStyleResource("CaptionTextBlockStyle");
        _permissionBadge.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _permissionGlyph, _permissionText },
        };
        Grid.SetColumn(_permissionBadge, 4);
        bar.Children.Add(_permissionBadge);
        return bar;
    }

    private void BuildContextMenus()
    {
        _menuOpen.FontWeight = FontWeights.SemiBold;
        _itemMenu.Items.Add(_menuOpen);
        _itemMenu.Items.Add(_menuDownload);
        _itemMenu.Items.Add(_menuSaveAs);
        _itemMenu.Items.Add(_menuFavorite);
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

        _menuOpen.Click += (_, _) => RunItemMenuCommand(OpenItemsAsync);
        _menuDownload.Click += (_, _) => RunItemMenuCommand(HydrateItemsAsync);
        _menuSaveAs.Click += (_, _) => RunItemMenuCommand(SaveItemsAsAsync);
        _menuFavorite.Click += (_, _) => RunItemMenuCommand(target =>
        {
            if (GetFavoriteTarget(target) is { } folder) ToggleFavorite(folder);
            return Task.CompletedTask;
        });
        _menuRename.Click += (_, _) => RunItemMenuCommand(RenameItemsAsync);
        _menuMove.Click += (_, _) => RunItemMenuCommand(MoveItemsAsync);
        _menuDelete.Click += (_, _) => RunItemMenuCommand(DeleteItemsAsync);
        _menuNewFolder.Click += (_, _) => RunUi(() => CreateFolderAsync());
        _menuUpload.Click += (_, _) => RunUi(() => ChooseFilesToUploadAsync());
        _menuRefresh.Click += (_, _) => RunUi(() => RefreshAsync());
    }

    private async Task RefreshAsync()
    {
        if (_currentFolder is null) await LoadDiskTreeAsync(GetCurrentNodePath());
        else await RefreshCurrentFolderAsync();
    }

    #region Navigation tree

    private Task LoadDiskTreeAsync() => LoadDiskTreeAsync(GetCurrentNodePath() is { Length: > 0 } path
        ? path : _savedState.NodePath);

    private async Task LoadDiskTreeAsync(string[]? location)
    {
        if (_busyNow || _isClosed) return;
        TraceUiStage("开始加载磁盘树，检查登录状态");
        var client = Client;
        if (client is null || !client.IsLoggedIn)
        {
            TraceUiStage("会话不可用，跳过磁盘树请求");
            UpdateAccountState();
            ShowInfo(InfoBarSeverity.Informational, "正在连接育网云",
                "登录完成后即可浏览云端磁盘。", "重试", () => RunUi(LoadDiskTreeAsync));
            return;
        }

        _infoBar.IsOpen = false;
        await RunBusyAsync("正在加载云端磁盘…", async () =>
        {
            TraceUiStage("等待磁盘树响应");
            _diskTree = await client.FetchDiskTreeAsync(_config.RootContainerTypeId);
            if (_isClosed) return;
            TraceUiStage($"磁盘树响应完成：Nodes={_diskTree.Count}; Disks={_diskTree.Count(node => node.IsDisk)}");
            TraceUiStage("开始清空导航树与目录状态");
            _navigationTree.RootNodes.Clear();
            _folderNodes.Clear();
            _folderNodesByKey.Clear();
            _diskTreeNodes.Clear();
            _nodeKinds.Clear();
            _knownFolders.Clear();
            _backHistory.Clear();
            _forwardHistory.Clear();
            _currentNode = null;
            _pendingSelection = null;
            _pendingCollapsedNode = null;
            _currentFolder = null;
            _currentItems = [];

            TraceUiStage("开始创建导航树节点");
            var root = CreateNavigationNode("育网云", NavigationNodeKind.Root);
            _navigationTree.RootNodes.Add(root);
            _diskTreeNodes[root] = new DiskTreeEntry { Name = "育网云", Node = root, IsContainer = true };
            AddDiskTreeChildren(root, _config.RootContainerTypeId, []);
            TraceUiStage($"导航树节点创建完成：Nodes={_nodeKinds.Count}; Folders={_folderNodes.Count}");

            var diskCount = _diskTree.Count(node => node.IsDisk);
            if (diskCount == 0)
                ShowInfo(InfoBarSeverity.Warning, "未找到云端磁盘", "请检查账号、服务器和 TypeId 设置。");
            SetStatus(diskCount == 0 ? "" : $"已加载 {diskCount} 个云端磁盘");
            RenderBreadcrumbs();
            UpdatePermissionBadge();
            RenderItems();
            TraceUiStage("磁盘树界面更新完成");
        }, "无法加载云端磁盘");

        if (_isClosed) return;
        // Restore by stable identifiers, loading only the ancestors needed for this location.
        if (await TryRestoreLocationAsync(location ?? _savedState.NodePath)) return;
        if (_currentNode is null && FindFirstDisk(_navigationTree.RootNodes) is { } firstDisk)
        {
            TraceUiStage($"开始自动导航首个磁盘：Disk={firstDisk.Disk.Id}");
            await NavigateToFolderAsync(firstDisk, addToHistory: false);
        }
        else if (_currentNode is null && _navigationTree.RootNodes.FirstOrDefault() is { } root)
            await NavigateToNodeAsync(root, addToHistory: false);
        TraceUiStage("磁盘树加载流程结束");
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
        // Navigation/restoration opens the target's ancestors; unrelated branches stay collapsed.
        var node = new TreeViewNode { Content = name, IsExpanded = false };
        _nodeKinds[node] = kind;
        return node;
    }

    private void RegisterFolderNode(NavigationFolderState state)
    {
        _folderNodes[state.Node] = state;
        _folderNodesByKey[(state.Disk.Id, state.Id)] = state;
        _diskTreeNodes[state.Node] = new DiskTreeEntry { Name = state.Name, Node = state.Node, Model = state.Disk };
    }

    private void QueueNavigationSelection()
    {
        if (_suppressNavigationSelection || _busyNow || _isClosed || _updatingNavigationTree) return;
        _pendingSelection = _navigationTree.SelectedNodes.FirstOrDefault();
        QueueNavigationChange();
    }

    private void OnNavigationCollapsed(TreeViewNode node)
    {
        if (_isClosed || _updatingNavigationTree || !IsDescendantOf(_currentNode, node)) return;
        _pendingCollapsedNode = node;
        QueueNavigationChange();
    }

    private static bool IsDescendantOf(TreeViewNode? node, TreeViewNode ancestor)
    {
        for (var parent = node?.Parent; parent is not null; parent = parent.Parent)
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    // TreeView updates its flattened selection during expansion/collapse. Navigate after that
    // callback returns; never change the same selection collection from SelectionChanged.
    private void QueueNavigationChange()
    {
        if (_navigationChangeQueued) return;
        _navigationChangeQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _navigationChangeQueued = false;
            if (_isClosed) return;
            if (_busyNow) return; // RunBusyAsync drains a pending collapse when it finishes.
            var target = _pendingCollapsedNode ?? _pendingSelection;
            _pendingCollapsedNode = null;
            _pendingSelection = null;
            if (target is not null && _nodeKinds.ContainsKey(target))
                RunUi(() => NavigateToNodeAsync(target));
        });
    }

    private async Task OnNavigationExpandingAsync(TreeViewNode node)
    {
        if (_isClosed || _updatingNavigationTree) return;
        if (!_folderNodes.TryGetValue(node, out var state) || state.ChildrenLoaded) return;
        try
        {
            await GetListingForExpansionAsync(state);
        }
        catch (Exception error)
        {
            if (_isClosed) return;
            if (!_folderNodes.ContainsKey(node)) return;
            node.HasUnrealizedChildren = true;
            Serilog.Log.Warning(error, "展开主界面目录导航失败：{Folder}", state.Name);
            ShowInfo(InfoBarSeverity.Error, $"无法加载“{state.Name}”的子文件夹", error.Message);
        }
    }

    private async Task NavigateToFolderAsync(NavigationFolderState state, bool addToHistory)
    {
        if (_busyNow || _isClosed) return;
        var target = state.ToRemoteFolder();
        if (_currentFolder is { } current && current.Disk.Id == target.Disk.Id && current.Id == target.Id)
            return;

        if (addToHistory && _currentNode is { } previous)
        {
            _backHistory.Push(previous);
            _forwardHistory.Clear();
        }

        TraceUiStage($"开始展开与选中导航节点：Disk={state.Disk.Id}; Folder={state.Id}");
        _currentFolder = target;
        _currentNode = state.Node;
        for (var parent = state.Node.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        SetNavigationSelection(state.Node);
        RebuildBreadcrumbs(state.Node);
        RememberFolder(target);
        RememberCurrentLocation();
        TraceUiStage("导航节点选中完成，开始更新搜索框");
        _search.Text = "";
        _search.PlaceholderText = $"搜索“{target.Name}”";
        TraceUiStage("搜索框更新完成，开始加载目录");
        await LoadCurrentFolderAsync();
    }

    private void SetNavigationSelection(TreeViewNode? node)
    {
        if (node is null && _navigationTree.SelectedNodes.Count == 0) return;
        if (node is not null && _navigationTree.SelectedNodes.Count == 1
            && ReferenceEquals(_navigationTree.SelectedNodes[0], node)) return;
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
        RenderBreadcrumbs();
    }

    /// <summary>Clickable address bar built from the navigation tree ancestry of the current folder.</summary>
    private void RenderBreadcrumbs()
    {
        _breadcrumbItems.Children.Clear();
        var segments = new List<(string Name, TreeViewNode? Node)>();
        if (_currentNode is { } selected)
        {
            for (TreeViewNode? current = selected; current is not null; current = current.Parent)
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
            if (isLast || node is null)
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
            var destination = node;
            segment.Click += (_, _) => RunUi(() => NavigateToNodeAsync(destination));
            _breadcrumbItems.Children.Add(segment);
        }

        var fullPath = string.Join(" / ", segments.Select(segment => segment.Name));
        ToolTipService.SetToolTip(_breadcrumbScroller, fullPath);
    }

    private async Task NavigateToNodeAsync(TreeViewNode node, bool addToHistory = true)
    {
        if (_busyNow || _isClosed || ReferenceEquals(_currentNode, node)) return;
        if (_folderNodes.TryGetValue(node, out var state))
        {
            await NavigateToFolderAsync(state, addToHistory);
            return;
        }
        if (!_diskTreeNodes.ContainsKey(node)) return;
        if (addToHistory && _currentNode is { } previous)
        {
            _backHistory.Push(previous);
            _forwardHistory.Clear();
        }
        _currentNode = node;
        _currentFolder = null;
        _currentItems = [];
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        SetNavigationSelection(node);
        _search.Text = "";
        _search.PlaceholderText = $"搜索“{node.Content}”";
        _infoBar.IsOpen = false;
        SetStatus("");
        RenderBreadcrumbs();
        UpdatePermissionBadge();
        RenderItems();
        RememberCurrentLocation();
    }

    /// <summary>Runs an async UI handler and surfaces failures instead of letting async void crash or vanish.</summary>
    private async void RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            if (_isClosed) return;
            Serilog.Log.Error(error, "主界面事件处理失败；最近阶段={Stage}; HRESULT=0x{HResult:X8}",
                LastDiagnosticStage, error.HResult);
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

    private string? NodeKey(TreeViewNode node)
    {
        if (_folderNodes.TryGetValue(node, out var folder))
            return folder.Id == 0 ? $"disk:{folder.Disk.Id}" : $"folder:{folder.Id}";
        if (_nodeKinds.TryGetValue(node, out var kind) && kind == NavigationNodeKind.Root)
            return "root";
        return _diskTreeNodes.TryGetValue(node, out var entry) && entry.Model is { IsContainer: true } model
            ? $"container:{model.Id}" : null;
    }

    private string[] GetCurrentNodePath() => GetNodePath(_currentNode);

    private string[] GetNodePath(TreeViewNode? start)
    {
        var path = new Stack<string>();
        for (var node = start; node is not null; node = node.Parent)
        {
            // TreeView attaches visible root nodes to its own hidden root. Only record
            // registered browser nodes; the hidden parent is not another cloud root.
            if (NodeKey(node) is not { } key) break;
            path.Push(key);
        }
        return path.TryPeek(out var root) && root == "root" ? path.ToArray() : [];
    }

    private void RememberCurrentLocation()
    {
        if (_isClosed) return;
        RenderFavorites();
        var path = GetCurrentNodePath();
        if (path.Length == 0) return;
        _savedState.NodePath = path;
        // Persist while the node is still attached. App exit may end the dispatcher
        // without closing each window, and Closed can run after TreeView teardown.
        SaveBrowserState();
        TraceUiStage($"已记录主界面浏览位置：{string.Join("/", path)}");
    }

    private void SaveBrowserState()
    {
        _savedState.SidebarWidth = _preferredSidebarWidth;
        _savedState.ColumnWidths = _fileColumnWidths.Select(width => double.IsFinite(width) ? width : 0).ToArray();
        MainWindowStateStore.Save(_config, _savedState);
    }

    private async Task<bool> TryRestoreLocationAsync(string[]? path)
    {
        path = MainWindowStateStore.NormalizeNodePath(path);
        if (path.Length == 0) return false;
        TraceUiStage($"开始还原主界面浏览位置：{string.Join("/", path)}");
        TreeViewNode? restored = null;
        IList<TreeViewNode> children = _navigationTree.RootNodes;
        await RunBusyAsync("正在还原上次浏览位置…", async () =>
        {
            try
            {
                for (var index = 0; index < path.Length; index++)
                {
                    if (_isClosed) return;
                    var next = children.FirstOrDefault(node => NodeKey(node) == path[index]);
                    if (next is null) break;
                    restored = next;
                    // The final location fetch is performed by navigation, so it is not fetched twice.
                    if (index == path.Length - 1) break;
                    if (_folderNodes.TryGetValue(next, out var folder)) await GetListingForExpansionAsync(folder);
                    next.IsExpanded = true;
                    children = next.Children;
                }
            }
            catch (Exception error)
            {
                Serilog.Log.Warning(error, "还原主界面位置时无法加载子目录，将停留在最近可访问的上级");
            }
        }, "无法还原浏览位置");
        if (restored is null || _isClosed) return _isClosed;
        await NavigateToNodeAsync(restored, addToHistory: false);
        TraceUiStage($"主界面浏览位置还原结束：Matched={NodeKey(restored)}; RequestedDepth={path.Length}");
        return true;
    }

    #endregion

    #region Folder listings

    private async Task LoadCurrentFolderAsync()
    {
        if (_currentFolder is not { } folder) return;
        var client = Client;
        if (client is null || !client.IsLoggedIn)
        {
            ShowInfo(InfoBarSeverity.Warning, "尚未登录", "请等待登录完成后重试。",
                "重试", () => RunUi(LoadCurrentFolderAsync));
            return;
        }

        _currentItems = [];
        RenderItems();
        await RunBusyAsync($"正在加载“{folder.Name}”…", async () =>
        {
            if (!_folderNodes.TryGetValue(folder.NavigationNode, out var state))
                throw new InvalidOperationException("文件夹已失效，请刷新后重试。");
            var listing = await GetListingForNavigationAsync(state);
            ApplyCurrentFolderListing(folder, state, listing);
        }, "无法加载文件夹");
    }

    private async Task RefreshCurrentFolderAsync()
    {
        if (_currentFolder is not { } folder) return;
        var client = Client;
        if (client is null || !client.IsLoggedIn)
        {
            ShowInfo(InfoBarSeverity.Warning, "尚未登录", "请等待登录完成后重试。");
            return;
        }

        await RunBusyAsync($"正在刷新“{folder.Name}”…", async () =>
            await RefreshCurrentFolderCoreAsync(client, folder), "刷新失败");
    }

    private async Task RefreshCurrentFolderCoreAsync(YuWangClient client, RemoteFolder folder)
    {
        if (!_folderNodes.TryGetValue(folder.NavigationNode, out var state))
            throw new InvalidOperationException("文件夹已失效，请刷新后重试。");
        var listing = await GetFreshFolderListingAsync(client, state);
        state.PendingListingConsumed = true;
        ApplyCurrentFolderListing(folder, state, listing);
    }

    private async Task RefreshItemParentAsync(YuWangClient client, RemoteFolder parent)
    {
        if (!_folderNodes.TryGetValue(parent.NavigationNode, out var state)) return;
        var listing = await GetFreshFolderListingAsync(client, state);
        if (ReferenceEquals(_currentNode, state.Node))
        {
            state.PendingListingConsumed = true;
            ApplyCurrentFolderListing(state.ToRemoteFolder(), state, listing);
        }
        else if (_currentNode is { } current && _folderNodes.TryGetValue(current, out var currentState))
        {
            // A tree-menu mutation can rename an ancestor without changing the open folder.
            _currentFolder = currentState.ToRemoteFolder();
            RememberFolder(_currentFolder);
            _search.PlaceholderText = $"搜索“{currentState.Name}”";
            RenderBreadcrumbs();
            UpdatePermissionBadge();
        }
        // PopulateFolderChildren queues navigation to the surviving parent if the open
        // folder was removed/moved; RunBusyAsync applies it after this operation finishes.
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
            ?? throw new InvalidOperationException("尚未连接到云端。"), state);
        state.PendingListingConsumed = true;
        return listing;
    }

    private async Task<FolderListingResult> GetListingForExpansionAsync(NavigationFolderState state)
    {
        if (state.ChildrenLoaded)
            return state.PendingListing ?? new FolderListingResult([], state.Permission, true);
        if (state.ListingTask is not null) return await AwaitFolderListingTaskAsync(state);
        if (state.PendingListing is { } prefetched) return prefetched;

        var client = Client ?? throw new InvalidOperationException("尚未连接到云端。");
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
        TraceUiStage($"等待目录与权限响应：Disk={state.Disk.Id}; Folder={state.Id}");
        var listing = await client.ListFolderWithPermissionsAsync(state.Disk.Id, state.Id);
        if (_isClosed) throw new OperationCanceledException("主界面已关闭。");
        if (!_folderNodes.TryGetValue(state.Node, out var registered) || !ReferenceEquals(registered, state))
            throw new OperationCanceledException("导航树已更新，丢弃旧节点的目录响应。");
        TraceUiStage($"目录与权限响应完成：Disk={state.Disk.Id}; Folder={state.Id}; "
            + $"Items={listing.Items.Count}; Permission={listing.Permission}; Valid={listing.IsValidPage}");
        if (!listing.IsValidPage)
            throw new IOException("服务器返回的目录列表不完整。");

        Callbacks.StoreMainWindowDirectoryListing(
            new FileIdentity(state.Disk.Id, state.ParentId, state.Id, 0),
            state.ParentId, state.Name, listing.Items, listing.Permission);
        state.Permission = Program.GetCachedDirectoryPermission(state.Disk.Id, state.Id);
        state.PendingListing = listing;
        state.PendingListingConsumed = false;
        TraceUiStage($"开始更新目录子节点：Disk={state.Disk.Id}; Folder={state.Id}");
        PopulateFolderChildren(state, listing.Items);
        TraceUiStage($"目录子节点更新完成：Disk={state.Disk.Id}; Folder={state.Id}");
        RememberFolder(state.ToRemoteFolder());
        return listing;
    }

    private void PopulateFolderChildren(NavigationFolderState parent,
        IReadOnlyList<YuWangItem> items)
    {
        // Preserve surviving node instances, their descendants and history when a parent refreshes.
        // Reject a folder pointing to itself/an ancestor before handing the hierarchy to TreeView.
        var ancestors = new HashSet<ulong>();
        for (var node = parent.Node; node is not null; node = node.Parent)
            if (_folderNodes.TryGetValue(node, out var folder)) ancestors.Add(folder.Id);
        var folders = items.Where(item => item.IsFolder && !ancestors.Contains(item.Id))
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var desired = new List<TreeViewNode>();
        foreach (var item in folders)
        {
            if (_folderNodesByKey.TryGetValue((parent.Disk.Id, item.Id), out var existing)
                && ReferenceEquals(existing.Node.Parent, parent.Node))
            {
                existing.Name = item.Name;
                existing.Node.Content = item.Name;
                desired.Add(existing.Node);
                continue;
            }
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
            RegisterFolderNode(state);
            desired.Add(node);
        }
        _updatingNavigationTree = true;
        try
        {
            foreach (var oldChild in parent.Node.Children.ToArray())
            {
                if (desired.Contains(oldChild)) continue;
                if (ReferenceEquals(_currentNode, oldChild) || IsDescendantOf(_currentNode, oldChild))
                    _pendingCollapsedNode = parent.Node;
                parent.Node.Children.Remove(oldChild);
                RemoveFolderNode(oldChild);
            }
            for (var index = 0; index < desired.Count; index++)
            {
                var node = desired[index];
                if (index < parent.Node.Children.Count && ReferenceEquals(parent.Node.Children[index], node)) continue;
                parent.Node.Children.Remove(node);
                parent.Node.Children.Insert(index, node);
            }
            parent.ChildrenLoaded = true;
            parent.Node.HasUnrealizedChildren = false;
            if (_currentNode is { } current && _nodeKinds.ContainsKey(current)) SetNavigationSelection(current);
        }
        finally { _updatingNavigationTree = false; }
        RenderFavorites();
        if (_pendingCollapsedNode is not null) QueueNavigationChange();
    }

    private void RemoveFolderNode(TreeViewNode node)
    {
        foreach (var child in node.Children.ToArray()) RemoveFolderNode(child);
        _nodeKinds.Remove(node);
        _diskTreeNodes.Remove(node);
        if (!_folderNodes.Remove(node, out var state)) return;
        if (_folderNodesByKey.TryGetValue((state.Disk.Id, state.Id), out var indexed)
            && ReferenceEquals(indexed, state))
        {
            _folderNodesByKey.Remove((state.Disk.Id, state.Id));
            _knownFolders.Remove((state.Disk.Id, state.Id));
        }
    }

    private void ApplyCurrentFolderListing(RemoteFolder folder, NavigationFolderState state,
        FolderListingResult listing)
    {
        TraceUiStage($"开始应用目录清单：Disk={folder.Disk.Id}; Folder={folder.Id}");
        _currentItems = listing.Items.ToList();
        state.Permission = Program.GetCachedDirectoryPermission(folder.Disk.Id, folder.Id);
        var loadedFolder = state.ToRemoteFolder();
        _currentFolder = loadedFolder;
        RebuildBreadcrumbs(state.Node);
        RememberFolder(loadedFolder);
        UpdatePermissionBadge();
        RenderItems();
        SetStatus("");
        TraceUiStage($"目录清单应用完成：Disk={folder.Disk.Id}; Folder={folder.Id}");
    }

    #endregion

    #region File list

    private void SetCheckboxSelectionEnabled(bool enabled)
    {
        // SelectedItems is read-only in Single mode. Capture the old selection before
        // changing modes, then restore it through SelectedItem instead of clearing that vector.
        var first = _items.SelectionMode == ListViewSelectionMode.Single
            ? _items.SelectedItem
            : _items.SelectedItems.OfType<ListViewItem>().FirstOrDefault();
        _items.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        _items.IsMultiSelectCheckBoxEnabled = enabled;
        if (first is not null) _items.SelectedItem = first;
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

    private readonly DispatcherTimer _fileSortTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private int _fileRenderVersion;
    private bool _replacingFileRows;

    private void QueueFileSort()
    {
        if (_isClosed) return;
        _fileSortTimer.Stop();
        _fileSortTimer.Start();
    }

    private void ApplyPendingFileSort()
    {
        if (_busyNow && !_isClosed) return;
        _fileSortTimer.Stop();
        if (_isClosed) return;
        RunUi(() => { RenderItems(preserveSelection: true); return Task.CompletedTask; });
    }

    private void RenderItems(bool preserveSelection = false)
    {
        if (_isClosed) return;
        _fileSortTimer.Stop();
        CancelDragWarmup();
        TraceUiStage($"开始更新文件列表：Items={_currentItems.Count}");
        var selected = preserveSelection ? _items.SelectedItems.OfType<ListViewItem>().Select(row => row.Tag).ToHashSet() : null;
        _fileRenderVersion++;
        _fileRows.Clear();
        ResetFileAvailabilityRows();
        var filter = _search.Text?.Trim() ?? "";
        ListViewItem[] rows;
        if (_currentFolder is null && _currentNode is { } grouping)
        {
            var children = grouping.Children.Where(node => (node.Content?.ToString() ?? "")
                .Contains(filter, StringComparison.CurrentCultureIgnoreCase));
            children = _sortAscending
                ? children.OrderBy(node => node.Content?.ToString(), StringComparer.CurrentCultureIgnoreCase)
                : children.OrderByDescending(node => node.Content?.ToString(), StringComparer.CurrentCultureIgnoreCase);
            rows = children.Select(CreateGroupingRow).ToArray();
        }
        else
        {
            rows = SortItems(_currentItems
                    .Where(item => filter.Length == 0 || item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
                .Select(CreateItemRow).ToArray();
        }

        // Publish one snapshot. Clear + one WinUI collection change per row churns the native
        // visual tree and raises selection callbacks during a partially rebuilt file list.
        _replacingFileRows = true;
        try
        {
            _items.ItemsSource = rows;
            if (selected is not null)
                foreach (var row in rows.Where(row => selected.Contains(row.Tag)))
                    if (_items.SelectionMode == ListViewSelectionMode.Multiple) _items.SelectedItems.Add(row);
                    else { _items.SelectedItem = row; break; }
        }
        finally { _replacingFileRows = false; }
        _visibleCount = rows.Length;
        UpdateEmptyState(filter);
        UpdateCommandStates();
        QueueDragPreparation();
        TraceUiStage($"文件列表更新完成：Visible={rows.Length}");
    }

    private ListViewItem CreateGroupingRow(TreeViewNode node)
    {
        var row = new Grid { ColumnSpacing = 12 };
        AddFileColumns(row);
        var namePanel = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(20) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
            Children =
            {
                new FontIcon { Glyph = _folderNodes.ContainsKey(node) ? "\uEDA2" : "\uE8F1", FontSize = 18 },
                new TextBlock { Text = node.Content?.ToString() ?? "", VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        Grid.SetColumn((FrameworkElement)namePanel.Children[1], 1);
        row.Children.Add(namePanel);
        AddRowCell(row, 3, _folderNodes.ContainsKey(node) ? "云端磁盘" : "磁盘分组",
            WinUiUiHost.GetBrushResource("TextFillColorSecondaryBrush"), TextAlignment.Left);
        var container = new ListViewItem
        {
            Content = row, Tag = node, MinHeight = 36,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        container.ContextRequested += (_, args) =>
        {
            if (args.Handled || !_folderNodes.TryGetValue(node, out var folder)) return;
            args.Handled = true;
            RunContextMenu(() =>
            {
                if (!_busyNow && !_isClosed) ShowFolderFavoriteMenu(folder, container, args);
            });
        };
        return container;
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
        var namePanel = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(20) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
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
        Grid.SetColumn((FrameworkElement)namePanel.Children[1], 1);
        row.Children.Add(namePanel);

        AddAvailabilityCell(row, item);
        AddRowCell(row, 2, item.UploadTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "", secondary, TextAlignment.Left);
        var typeCell = AddRowCell(row, 3, GetTypeLabel(item), secondary, TextAlignment.Left);
        _ = LoadSystemIconIntoAsync(iconHost,
            item.IsFolder ? GetSystemFolderIconAsync() : GetSystemFileIconAsync(item.Name), 20, item, typeCell, _fileRenderVersion);
        AddRowCell(row, 4, item.IsFolder ? "" : FormatSize(item.Size), secondary, TextAlignment.Right);

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

    private static TextBlock AddRowCell(Grid row, int column, string text, Brush? foreground, TextAlignment alignment)
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
        return cell;
    }

    private string GetTypeLabel(YuWangItem item)
    {
        if (item.IsFolder) return _systemFolderType;
        return _systemFileTypes.GetValueOrDefault(SystemFileExtension(item.Name), "文件");
    }

    private void UpdateEmptyState(string filter)
    {
        var hasFolder = _currentNode is not null;
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
            _emptyDetail.Text = "从左侧选择磁盘或文件夹。";
        }
        else if (filter.Length > 0)
        {
            _emptyGlyph.Glyph = "\uE721";
            _emptyTitle.Text = $"没有与“{filter}”匹配的项目";
            _emptyDetail.Text = "仅搜索当前文件夹。";
        }
        else
        {
            _emptyGlyph.Glyph = FolderGlyph;
            _emptyTitle.Text = _currentFolder is null ? "此分组为空" : "此文件夹为空";
            _emptyDetail.Text = CanWrite ? "将文件或文件夹拖放到此处，或使用“上传”添加内容。" : "";
        }
    }

    private async Task LoadSystemIconIntoAsync(Border iconHost, Task<BitmapImage?> iconTask, double size,
        YuWangItem item, TextBlock typeCell, int renderVersion)
    {
        var icon = await iconTask;
        if (_isClosed || renderVersion != _fileRenderVersion) return;
        typeCell.Text = GetTypeLabel(item);
        ToolTipService.SetToolTip(typeCell, typeCell.Text);
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
        var extension = SystemFileExtension(fileName);
        if (!_systemFileIconTasks.TryGetValue(extension, out var task))
            _systemFileIconTasks[extension] = task = CreateSystemFileIconAsync(extension);
        return task;
    }

    private static string SystemFileExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 64
            || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || extension.EndsWith('.') || extension.EndsWith(' ') ? ".file" : extension;
    }

    private void QueueSystemTypeSort()
    {
        if (_sortColumn == SortColumn.Type) QueueFileSort();
    }

    private Task<BitmapImage?> GetSystemFolderIconAsync()
        => _systemFolderIconTask ??= CreateSystemFolderIconAsync();

    private static string SystemIconProbeDirectory()
    {
        var probeDirectory = Path.Combine(Path.GetTempPath(), ProductIdentity.Identifier, "system-file-icons");
        Directory.CreateDirectory(probeDirectory);
        return probeDirectory;
    }

    private async Task<BitmapImage?> CreateSystemFileIconAsync(string extension)
    {
        string? probePath = null;
        try
        {
            probePath = Path.Combine(SystemIconProbeDirectory(), "file" + extension);
            if (!File.Exists(probePath)) await File.WriteAllBytesAsync(probePath, []);
            var probeFile = await StorageFile.GetFileFromPathAsync(probePath);
            if (_isClosed) return null;
            _systemFileTypes[extension] = string.IsNullOrWhiteSpace(probeFile.DisplayType) ? "文件" : probeFile.DisplayType;
            QueueSystemTypeSort();
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

    private async Task<BitmapImage?> CreateSystemFolderIconAsync()
    {
        try
        {
            var probePath = Path.Combine(SystemIconProbeDirectory(), "folder");
            Directory.CreateDirectory(probePath);
            var probeFolder = await StorageFolder.GetFolderFromPathAsync(probePath);
            if (_isClosed) return null;
            _systemFolderType = string.IsNullOrWhiteSpace(probeFolder.DisplayType) ? "文件夹" : probeFolder.DisplayType;
            QueueSystemTypeSort();
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
        UpdateFavoriteCommands();
        _reloadDisks.IsEnabled = idle;
        _multiSelect.IsEnabled = idle && _currentFolder is not null;
        _back.IsEnabled = idle && _backHistory.Count > 0;
        _up.IsEnabled = idle && _currentNode?.Parent is not null;
        _forward.IsEnabled = idle && _forwardHistory.Count > 0;
        _refresh.IsEnabled = idle;
        _upload.IsEnabled = canWrite;
        _newFolder.IsEnabled = canWrite;
        _open.IsEnabled = idle && (selected.Count == 1
            || (_items.SelectedItem as ListViewItem)?.Tag is TreeViewNode);
        _download.IsEnabled = idle && selected.Count > 0 && Program.IsDragBackendAvailable;
        _rename.IsEnabled = canWrite && selected.Count == 1;
        _move.IsEnabled = canWrite && selected.Count > 0;
        _delete.IsEnabled = canWrite && selected.Count > 0;

        UpdateItemMenuStates();
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
            _summary.Text = _currentNode is { } grouping
                ? $"显示 {_visibleCount} / {grouping.Children.Count} 个项目" : "";
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
                "可上传、新建、重命名、移动和删除。"),
            DirectoryPermission.ReadOnly => ("\uE72E", "只读",
                "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush",
                "此文件夹为只读，仅可浏览和下载。"),
            _ => ("\uE9CE", "权限未确认",
                "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush",
                "无法确认权限，暂不能修改。"),
        };
        _permissionGlyph.Glyph = glyph;
        _permissionGlyph.Foreground = WinUiUiHost.GetBrushResource(foreground);
        _permissionText.Text = text;
        _permissionBadge.Background = WinUiUiHost.GetBrushResource(background);
        ToolTipService.SetToolTip(_permissionBadge, tooltip);
    }

    private List<YuWangItem> SelectedEntries()
    {
        if (_items.SelectionMode == ListViewSelectionMode.Single)
            return _items.SelectedItem is ListViewItem { Tag: YuWangItem item } ? [item] : [];
        return _items.SelectedItems.OfType<ListViewItem>()
            .Select(container => container.Tag).OfType<YuWangItem>().ToList();
    }

    private ItemActionTarget? GetSelectionTarget()
        => _currentFolder is { } parent ? new ItemActionTarget(parent, SelectedEntries()) : null;

    private bool IsItemTargetAvailable(ItemActionTarget? target)
        => !_isClosed && !_busyNow && target is { Items.Count: > 0 }
            && _folderNodes.TryGetValue(target.Parent.NavigationNode, out var state)
            && state.Disk.Id == target.Parent.Disk.Id && state.Id == target.Parent.Id;

    private bool CanWriteItems(ItemActionTarget? target)
        => IsItemTargetAvailable(target)
            && _folderNodes[target!.Parent.NavigationNode].Permission == DirectoryPermission.FullAccess;

    private void UpdateItemMenuStates()
    {
        var available = IsItemTargetAvailable(_itemMenuTarget);
        var canWrite = CanWriteItems(_itemMenuTarget);
        var count = _itemMenuTarget?.Items.Count ?? 0;
        _menuOpen.IsEnabled = available && count == 1;
        _menuDownload.IsEnabled = available && Program.IsDragBackendAvailable;
        _menuSaveAs.IsEnabled = available;
        var favoriteTarget = available ? GetFavoriteTarget(_itemMenuTarget) : null;
        _menuFavorite.Visibility = favoriteTarget is null ? Visibility.Collapsed : Visibility.Visible;
        _menuFavorite.IsEnabled = favoriteTarget is not null;
        _menuFavorite.Text = favoriteTarget is not null && IsFavorite(favoriteTarget)
            ? "取消收藏" : "添加到收藏";
        _menuRename.IsEnabled = canWrite && count == 1;
        _menuMove.IsEnabled = canWrite;
        _menuDelete.IsEnabled = canWrite;
    }

    private void RunItemMenuCommand(Func<ItemActionTarget?, Task> command)
    {
        var target = _itemMenuTarget;
        _itemMenuTarget = null;
        if (IsItemTargetAvailable(target)) RunUi(() => command(target));
    }

    private void OnItemContextRequested(ListViewItem container, ContextRequestedEventArgs args)
    {
        if (args.Handled) return;
        args.Handled = true;
        RunContextMenu(() =>
        {
            if (_busyNow || _isClosed || _currentFolder is null || container.Tag is not YuWangItem) return;
            _itemMenu.Hide();
            if (!container.IsSelected)
            {
                if (_items.SelectionMode == ListViewSelectionMode.Single)
                    _items.SelectedItem = container;
                else
                {
                    _items.SelectedItems.Clear();
                    _items.SelectedItems.Add(container);
                }
            }
            _itemMenuTarget = GetSelectionTarget();
            UpdateCommandStates();
            ShowContextMenu(_itemMenu, container, args);
        });
    }

    private void OnNavigationContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled) return;
        args.Handled = true;
        RunContextMenu(() =>
        {
            if (_busyNow || _isClosed) return;
            var hasPosition = args.TryGetPosition(_navigationTree, out var point);
            var container = hasPosition ? FindContainerAt<TreeViewItem>(_navigationTree, point)
                : _navigationTree.SelectedNode is { } selected
                    ? Project<TreeViewItem>(_navigationTree.ContainerFromNode(selected)) : null;
            var node = container is not null ? _navigationTree.NodeFromContainer(container)
                : hasPosition ? null : _navigationTree.SelectedNode;
            ShowNavigationMenu(node, hasPosition ? point : null);
        });
    }

    private void OnNavigationRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        args.Handled = true;
        var point = args.GetPosition(_navigationTree);
        RunContextMenu(() =>
        {
            var container = FindContainerAt<TreeViewItem>(_navigationTree, point);
            ShowNavigationMenu(container is null ? null : _navigationTree.NodeFromContainer(container), point);
        });
    }

    private void ShowNavigationMenu(TreeViewNode? node, Windows.Foundation.Point? point)
    {
        if (_busyNow || _isClosed || node is null || !_folderNodes.TryGetValue(node, out var folder)) return;
        // A mouse right-click raises both ContextRequested and RightTapped. Answer only the first:
        // hiding and re-showing the shared item menu while it opens left it closed.
        if (Environment.TickCount64 - _navigationMenuShownAt < NavigationMenuDebounceMilliseconds) return;
        if (folder.Id == 0)
        {
            // Disk roots have no parent item to rename/delete, but can be bookmarked.
            var menu = new MenuFlyout();
            var favorite = folder;
            var command = new MenuFlyoutItem { Text = IsFavorite(favorite) ? "取消收藏" : "添加到收藏" };
            command.Click += (_, _) => ToggleFavorite(favorite);
            menu.Items.Add(command);
            ShowNavigationMenuAt(menu, point);
            return;
        }
        if (node.Parent is not { } parentNode || !_folderNodes.TryGetValue(parentNode, out var parent)) return;
        var item = new YuWangItem { Type = 'D', Id = folder.Id, Name = folder.Name };
        _itemMenuTarget = new ItemActionTarget(parent.ToRemoteFolder(), [item]);
        UpdateItemMenuStates();
        ShowNavigationMenuAt(_itemMenu, point);
    }

    private void ShowNavigationMenuAt(MenuFlyout menu, Windows.Foundation.Point? point)
    {
        _navigationMenuShownAt = Environment.TickCount64;
        void Show()
        {
            if (point is { } position) menu.ShowAt(_navigationTree, new FlyoutShowOptions { Position = position });
            else menu.ShowAt(_navigationTree);
        }
        if (!menu.IsOpen) { Show(); return; }
        // The item menu may still be open for the file list; let it close before reopening here.
        menu.Hide();
        DispatcherQueue.TryEnqueue(() => RunContextMenu(() => { if (!_isClosed) Show(); }));
    }

    // NativeAOT may initially project a native control as its base DependencyObject.
    private static T? Project<T>(object? value) where T : class
    {
        if (value is T typed) return typed;
        if (value is null) return null;
        try { return WinRT.CastExtensions.As<T>(value); }
        catch (InvalidCastException) { return null; }
    }

    private static T? FindContainerAt<T>(UIElement surface, Windows.Foundation.Point point) where T : class
    {
        var hostPoint = surface.TransformToVisual(null).TransformPoint(point);
        foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, surface))
            for (DependencyObject? current = hit; current is not null && !ReferenceEquals(current, surface);
                 current = VisualTreeHelper.GetParent(current))
                if (Project<T>(current) is { } container) return container;
        return null;
    }

    private void OnListBackgroundContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || _currentFolder is null) return;
        args.Handled = true;
        RunContextMenu(() =>
        {
            if (_busyNow || _isClosed) return;
            UpdateCommandStates();
            ShowContextMenu(_backgroundMenu, _items, args);
        });
    }

    private void RunContextMenu(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            _itemMenuTarget = null;
            Serilog.Log.Error(error, "打开主界面右键菜单失败；HRESULT=0x{HResult:X8}", error.HResult);
            if (!_isClosed)
                try { ShowInfo(InfoBarSeverity.Error, "无法打开右键菜单", error.Message); } catch { }
        }
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
        if ((_items.SelectedItem as ListViewItem)?.Tag is TreeViewNode node)
        {
            await NavigateToNodeAsync(node);
            return;
        }
        await OpenItemsAsync(GetSelectionTarget());
    }

    private async Task OpenItemsAsync(ItemActionTarget? target)
    {
        if (target is null || !IsItemTargetAvailable(target) || target.Items.Count != 1) return;
        var selected = target.Items;
        var parent = target.Parent;
        var item = selected[0];
        if (item.IsFolder)
        {
            if (_folderNodesByKey.TryGetValue((parent.Disk.Id, item.Id), out var state))
                await NavigateToFolderAsync(state, addToHistory: true);
            else
                ShowInfo(InfoBarSeverity.Warning, $"无法打开“{item.Name}”", "请刷新后重试。");
            return;
        }

        await RunBusyAsync($"正在打开“{item.Name}”…", async () =>
        {
            // Open the actual sync-root file so local edits are preserved and Cloud Files
            // fetches only missing data when the associated application reads it.
            var openRoot = Path.Combine(Path.GetTempPath(), ProductIdentity.Identifier, "opened-files");
            var path = await PrepareLocalItemPathAsync(parent, item, openRoot, Program.ApplicationCancellationToken);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"已打开“{item.Name}”");
        }, "无法打开");
    }

    #endregion

    #region History

    private async Task GoBackAsync()
    {
        if (_busyNow || _currentNode is null) return;
        while (_backHistory.TryPop(out var target))
        {
            if (!_nodeKinds.ContainsKey(target) || ReferenceEquals(target, _currentNode)) continue;
            _forwardHistory.Push(_currentNode);
            await NavigateToNodeAsync(target, addToHistory: false);
            break;
        }
    }

    private async Task GoForwardAsync()
    {
        if (_busyNow || _currentNode is null) return;
        while (_forwardHistory.TryPop(out var target))
        {
            if (!_nodeKinds.ContainsKey(target) || ReferenceEquals(target, _currentNode)) continue;
            _backHistory.Push(_currentNode);
            await NavigateToNodeAsync(target, addToHistory: false);
            break;
        }
    }

    private async Task GoUpAsync()
    {
        if (_busyNow || _currentNode?.Parent is not { } parent) return;
        await NavigateToNodeAsync(parent);
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
        var canDrop = !_outboundDragActive && CanWrite && !args.DataView.Properties.ContainsKey(BrowserDragSource)
            && args.DataView.Contains(StandardDataFormats.StorageItems);
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

    private void OnFileListPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _dragIntentTimer.Stop();
        _dragSourceRow = null;
        if (_busyNow || _outboundDragActive || _isClosed) return;
        var point = args.GetCurrentPoint(_items);
        if (!point.Properties.IsLeftButtonPressed) return;
        var row = FindContainerAt<ListViewItem>(_items, point.Position);
        if (row?.Tag is not YuWangItem) return;
        _dragSourceRow = row;
        _dragPointer = point;
        _dragPressPosition = NativeFileDrag.CursorPosition;
        _dragIntentTimer.Start();
        // Preserve Ctrl/Shift click selection; starting a drag resolves its source separately.
        var modifiers = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            | InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (modifiers.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        SelectDragSource(row);
        QueueDragPreparation();
    }

    private void SelectDragSource(ListViewItem row)
    {
        if (row.IsSelected) return;
        if (_items.SelectionMode == ListViewSelectionMode.Single) _items.SelectedItem = row;
        else
        {
            _items.SelectedItems.Clear();
            _items.SelectedItems.Add(row);
        }
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        _dropOverlay.Visibility = Visibility.Collapsed;
        if (_outboundDragActive || !CanWrite || args.DataView.Properties.ContainsKey(BrowserDragSource)
            || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral();
        try
        {
            var storageItems = await args.DataView.GetStorageItemsAsync();
            await UploadStorageItemsAsync(storageItems);
        }
        catch (Exception error) { ShowInfo(InfoBarSeverity.Error, "拖放上传失败", error.Message); }
        finally { deferral.Complete(); }
    }

    private async Task UploadStorageItemsAsync(IReadOnlyList<IStorageItem> items)
    {
        var folder = _currentFolder;
        var client = Client;
        if (folder is null || folder.Permission != DirectoryPermission.FullAccess
            || client is null || !client.IsLoggedIn)
        {
            ShowInfo(InfoBarSeverity.Warning, "无法上传", "尚未连接，请稍后重试。");
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
            else ShowInfo(InfoBarSeverity.Warning, $"有 {errors.Count} 项未能上传", JoinFailures(errors, "\n"));
        }, "上传失败");
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
        var shortName = ShortenFileName(localFile.Name);
        SetTransferProgress($"正在计算校验值 · {shortName}", 0);
        _transferProgress.IsIndeterminate = true;
        var hash = await SHA256.HashDataAsync(stream, Program.ApplicationCancellationToken);
        stream.Position = 0;
        var verify = UploadVerificationPolicy.ShouldVerify(message => Serilog.Log.Information("{Msg}", message));
        // Progress arrives on pool threads; the UI samples the latest value on its own timer.
        var tracker = new BrowserUploadProgress(verify);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => ShowBrowserUploadProgress(tracker.Latest, shortName);
        ShowBrowserUploadProgress(tracker.Latest, shortName);
        timer.Start();
        try
        {
            await client.UploadNewFileAsync(diskId, folderId, localFile.Name, stream,
                File.GetLastWriteTimeUtc(localFile.Path), hash, Program.ApplicationCancellationToken,
                progress: tracker.Report, verifyContent: verify);
        }
        finally
        {
            timer.Stop();
            _transferProgress.IsIndeterminate = false;
        }
    }

    private void ShowBrowserUploadProgress(BrowserUploadProgress.State state, string shortName)
    {
        if (_isClosed) return;
        string Bytes(long done) => $"{FormatSize(done)} / {FormatSize(state.TotalBytes)}";
        var text = state.Stage switch
        {
            UploadStage.Preparing => $"正在准备上传 · {shortName}",
            UploadStage.Uploading => $"正在上传 · {shortName} · {Percent(state.SentBytes):F2}% · {Bytes(state.SentBytes)}",
            UploadStage.Confirming => $"已上传，正在等待云端确认 · {shortName}",
            UploadStage.Verifying => $"正在下载校验 · {shortName} · {Percent(state.VerifiedBytes):F2}% · {Bytes(state.VerifiedBytes)}",
            _ => state.Verifies ? $"校验通过，正在完成 · {shortName}" : $"正在完成 · {shortName}",
        };
        SetTransferProgress(text, state.OverallPercent);
        _transferProgress.IsIndeterminate = state.Stage is UploadStage.Preparing or UploadStage.Confirming;

        double Percent(long done) => state.TotalBytes <= 0 ? 100 : Math.Clamp(done * 100.0 / state.TotalBytes, 0, 100);
    }

    /// <summary>Thread-safe latest-value holder for a main-window upload.</summary>
    private sealed class BrowserUploadProgress(bool verifies)
    {
        internal readonly record struct State(UploadStage Stage, long TotalBytes, long SentBytes, long VerifiedBytes,
            double OverallPercent, bool Verifies);

        private readonly object _gate = new();
        private State _latest = new(UploadStage.Preparing, 0, 0, 0, 0, verifies);

        public State Latest { get { lock (_gate) return _latest; } }

        public void Report(UploadProgress progress)
        {
            lock (_gate)
            {
                var verifying = _latest.Verifies || progress.Stage == UploadStage.Verifying;
                var sent = Math.Max(_latest.SentBytes, progress.SentBytes);
                var verified = Math.Max(_latest.VerifiedBytes, progress.VerifiedBytes);
                var overall = UploadProgressReporter.OverallFraction(progress.TotalBytes, sent, verified, verifying) * 100;
                _latest = new(progress.Stage, progress.TotalBytes, sent, verified,
                    Math.Max(_latest.OverallPercent, overall), verifying);
            }
        }
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
        }, "无法创建文件夹");
    }

    private Task RenameSelectionAsync() => RenameItemsAsync(GetSelectionTarget());

    private async Task RenameItemsAsync(ItemActionTarget? target)
    {
        if (target is null || !CanWriteItems(target) || target.Items.Count != 1) return;
        var selected = target.Items;
        var parent = target.Parent;
        var client = Client;
        if (client is null || !client.IsLoggedIn) return;
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
        if (!CanWriteItems(target)) return;
        await RunBusyAsync("正在重命名…", async () =>
        {
            if (!await client.RenameRemoteItemAsync(parent.Disk.Id, parent.Id, item.Id, item.IsFolder, null, newName))
                throw new IOException("服务器未确认重命名。");
            await RefreshItemParentAsync(client, parent);
            SetStatus($"已重命名为“{newName}”");
        }, "重命名失败");
    }

    private Task MoveSelectionAsync() => MoveItemsAsync(GetSelectionTarget());

    private async Task MoveItemsAsync(ItemActionTarget? target)
    {
        if (target is null || !CanWriteItems(target)) return;
        var selected = target.Items;
        var source = target.Parent;
        var client = Client;
        if (client is null || !client.IsLoggedIn) return;
        var choices = _knownFolders.Values
            .Where(folder => folder.Disk.Id == source.Disk.Id
                && folder.Id != source.Id
                && folder.Permission == DirectoryPermission.FullAccess
                && !selected.Any(item => item.IsFolder
                    && (item.Id == folder.Id
                        || (_folderNodesByKey.TryGetValue((source.Disk.Id, item.Id), out var moved)
                            && IsDescendantOf(folder.NavigationNode, moved.Node)))))
            .Select(folder => (Folder: folder, Path: GetNavigationPath(folder.NavigationNode)))
            .OrderBy(choice => choice.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "没有可用的目标文件夹",
                "请先在左侧展开目标文件夹（需在当前磁盘且可写）。");
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
                    Text = "仅显示已打开过的可写文件夹。",
                    Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle"),
                },
            },
        };
        if (await ShowInputDialogAsync("移动", dialogContent, "移动") != ContentDialogResult.Primary) return;
        if (targetPicker.SelectedItem is not ComboBoxItem { Tag: RemoteFolder destination }
            || destination.Id == source.Id || !CanWriteItems(target)) return;

        await RunBusyAsync("正在移动…", async () =>
        {
            foreach (var item in selected)
            {
                if (!await client.RenameRemoteItemAsync(source.Disk.Id, source.Id, item.Id, item.IsFolder,
                        destination.Id, item.Name))
                    throw new IOException($"服务器未确认移动“{item.Name}”。");
                RelocateFavoritePaths(source, item, destination);
            }
            await RefreshItemParentAsync(client, source);
            if (_currentFolder is { } current && current.Disk.Id == destination.Disk.Id
                && current.Id == destination.Id && _folderNodes.ContainsKey(current.NavigationNode))
                await RefreshCurrentFolderCoreAsync(client, current);
            SetStatus($"已移动 {selected.Count} 项到“{destination.Name}”");
        }, "移动失败");
    }

    private Task DeleteSelectionAsync() => DeleteItemsAsync(GetSelectionTarget());

    private async Task DeleteItemsAsync(ItemActionTarget? target)
    {
        if (target is null || !CanWriteItems(target)) return;
        var selected = target.Items;
        var parent = target.Parent;
        var client = Client;
        if (client is null || !client.IsLoggedIn) return;
        var prompt = selected.Count == 1 ? $"删除“{selected[0].Name}”？" : $"删除这 {selected.Count} 项？";
        if (await ShowConfirmDialogAsync(prompt, "将从云端删除，无法撤销。", "删除") != ContentDialogResult.Primary) return;
        if (!CanWriteItems(target)) return;
        await RunBusyAsync($"正在删除 {selected.Count} 项…", async () =>
        {
            var failures = new List<string>();
            foreach (var item in selected)
            {
                if (!await client.DeleteRemoteItemAsync(parent.Disk.Id, parent.Id, item.Id, item.IsFolder))
                    failures.Add(item.Name);
            }
            await RefreshItemParentAsync(client, parent);
            if (failures.Count == 0) SetStatus($"已删除 {selected.Count} 项");
            else ShowInfo(InfoBarSeverity.Warning, $"有 {failures.Count} 项未能删除", JoinFailures(failures, "、"));
        }, "删除失败");
    }

    #endregion

    #region Download

    // Opening uses the actual placeholder when present; the associated app hydrates on demand.
    private async Task<string> PrepareLocalItemPathAsync(RemoteFolder folder, YuWangItem item,
        string fallbackDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _downloadedPaths.TryGetValue((folder.Disk.Id, item.Id), out var downloaded);
        var path = await Task.Run(() => Program.ResolveLocalItemPath(folder.Disk.Id, folder.Id, item)
            ?? (downloaded is not null && File.Exists(downloaded) ? downloaded : null), token);
        if (path is not null) return path;
        path = await DownloadEntryAsync(folder.Disk.Id, item, fallbackDirectory, token);
        _downloadedPaths[(folder.Disk.Id, item.Id)] = path;
        return path;
    }

    private Task DownloadSelectionAsync() => HydrateItemsAsync(GetSelectionTarget());

    private async Task SaveItemsAsAsync(ItemActionTarget? target)
    {
        if (target is null || !IsItemTargetAvailable(target)) return;
        var selected = target.Items;
        var parent = target.Parent;
        var owner = WindowNative.GetWindowHandle(this);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloadsFolder = Path.Combine(userProfile, "Downloads");
        if (!Directory.Exists(downloadsFolder)) downloadsFolder = userProfile;
        var destination = await FolderPicker.PickAsync(owner, "选择另存为位置", downloadsFolder);
        if (string.IsNullOrWhiteSpace(destination) || !IsItemTargetAvailable(target)) return;
        await RunBusyAsync($"正在另存 {selected.Count} 项…", async () =>
        {
            foreach (var item in selected)
                await DownloadEntryAsync(parent.Disk.Id, item, destination);
            SetStatus("");
            ShowInfo(InfoBarSeverity.Success, "另存为完成", $"已保存 {selected.Count} 项到“{destination}”。",
                "打开文件夹", () => Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }));
        }, "另存为失败");
    }

    private async Task<string> DownloadEntryAsync(ulong diskId, YuWangItem item, string destination,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = Program.ApplicationCancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (!item.IsFolder)
            return await DownloadFileAsync(item, destination, cancellationToken);
        var folderPath = GetAvailablePath(destination, SafeFileName(item.Name), directory: true);
        Directory.CreateDirectory(folderPath);
        var children = await Client!.ListFolderAsync(diskId, item.Id).WaitAsync(cancellationToken);
        foreach (var child in children)
            await DownloadEntryAsync(diskId, child, folderPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return folderPath;
    }

    private async Task<string> DownloadFileAsync(YuWangItem item, string destination, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = Program.ApplicationCancellationToken;
        var client = Client ?? throw new InvalidOperationException("尚未连接到云端。");
        var probe = await client.ProbeRealSizeAsync(item.Id, item.Name, item.Size).WaitAsync(cancellationToken)
            ?? throw new IOException($"无法获取“{item.Name}”的下载地址或文件大小。");
        var folder = Path.GetFullPath(destination);
        Directory.CreateDirectory(folder);
        var finalPath = GetAvailablePath(folder, SafeFileName(item.Name), directory: false);
        var temporaryPath = finalPath + ".yw-download-" + Guid.NewGuid().ToString("N");
        long receivedBytes = 0;
        long lastReportedBytes = -1;
        void ReportDownloadProgress()
        {
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            var completed = Interlocked.Read(ref receivedBytes);
            if (completed == lastReportedBytes) return;
            lastReportedBytes = completed;
            SetTransferProgress($"正在下载“{item.Name}”… {FormatSize(completed)} / {FormatSize(probe.Size)}",
                probe.Size <= 0 ? 100 : completed * 100d / probe.Size);
            SetBrowserDownloadProgress(item.Id, completed, probe.Size);
        }
        // HTTP callbacks run off the UI thread. Sample their latest byte count rather than
        // waiting for a complete 4 MB range or queuing one UI action for every network read.
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        progressTimer.Tick += (_, _) => ReportDownloadProgress();
        try
        {
            await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long offset = 0;
            ReportDownloadProgress();
            progressTimer.Start();
            while (offset < probe.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rangeOffset = offset;
                var requested = Math.Min(4 * 1024 * 1024, probe.Size - offset);
                var range = await client.DownloadRangeDirectAsync(probe.DirectUrl, offset, requested,
                    reportProgress: (_, completed) =>
                    {
                        // A retry may start this range again at zero. Keep displayed progress
                        // monotonic and clamp whole-file fallback responses to this range.
                        var received = rangeOffset + Math.Clamp(completed, 0, requested);
                        Interlocked.Exchange(ref receivedBytes, Math.Max(Interlocked.Read(ref receivedBytes), received));
                    })
                    .WaitAsync(cancellationToken);
                if (range is null || range.Value.Data.Length == 0)
                    throw new EndOfStreamException($"“{item.Name}”下载提前结束。");
                await output.WriteAsync(range.Value.Data, cancellationToken);
                offset += range.Value.Data.Length;
                Interlocked.Exchange(ref receivedBytes, offset);
                ReportDownloadProgress();
            }
            await output.FlushAsync(cancellationToken);
            output.Close();
            File.Move(temporaryPath, finalPath);
            if (item.UploadTime is { } modified) File.SetLastWriteTimeUtc(finalPath, modified.ToUniversalTime());
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
        finally
        {
            progressTimer.Stop();
            if (!_isClosed) SetBrowserDownloadProgress(item.Id, null, 0);
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

    private async Task RunBusyAsync(string initialStatus, Func<Task> action, string failureTitle = "操作失败")
    {
        if (_busyNow || _isClosed) return;
        CancelDragWarmup();
        _busyNow = true;
        if (_infoBar.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning) _infoBar.IsOpen = false;
        _loading.Visibility = Visibility.Visible;
        SetProgressStatus(initialStatus);
        UpdateCommandStates();
        UpdateEmptyState(_search.Text?.Trim() ?? "");
        try { await action(); }
        catch (Exception error)
        {
            if (_isClosed) return;
            Serilog.Log.Warning(error, "主界面操作失败；最近阶段={Stage}; HRESULT=0x{HResult:X8}",
                LastDiagnosticStage, error.HResult);
            SetStatus("");
            ShowInfo(InfoBarSeverity.Error, failureTitle, error.Message);
        }
        finally
        {
            _busyNow = false;
            if (!_isClosed)
            {
                _loading.Visibility = Visibility.Collapsed;
                _transferProgress.Visibility = Visibility.Collapsed;
                if (_statusIsProgress) SetStatus("");
                UpdateCommandStates();
                UpdateEmptyState(_search.Text?.Trim() ?? "");
                QueueDragPreparation();
                if (_pendingCollapsedNode is not null || _pendingSelection is not null) QueueNavigationChange();
            }
        }
    }

    /// <summary>Shows a result message; it clears itself after a few seconds.</summary>
    private void SetStatus(string text)
    {
        _statusClearTimer.Stop();
        _statusIsProgress = false;
        _status.Text = text;
        if (text.Length > 0) _statusClearTimer.Start();
    }

    /// <summary>Shows an in-progress message; it stays until the operation finishes.</summary>
    private void SetProgressStatus(string text)
    {
        _statusClearTimer.Stop();
        _statusIsProgress = true;
        _status.Text = text;
    }

    private static string JoinFailures(IReadOnlyList<string> failures, string separator)
        => string.Join(separator, failures.Take(3)) + (failures.Count > 3 ? $" 等 {failures.Count} 项" : "");

    private void SetTransferProgress(string text, double percent)
    {
        SetProgressStatus(text);
        _transferProgress.Value = Math.Clamp(percent, 0, 100);
        _transferProgress.Visibility = Visibility.Visible;
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message,
        string? actionText = null, Action? action = null)
    {
        _infoBarCloseTimer.Stop();
        StopInfoBarCountdown();
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
        OpenInfoBar();
        if (severity != InfoBarSeverity.Success) return;
        _infoBarCloseTimer.Start();
        StartInfoBarCountdown();
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
