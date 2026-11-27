using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YuWangCloudSync.CloudFiles;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Windows.Storage.FileProperties;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

internal sealed partial class MainWindow
{
    internal static async Task<IReadOnlyList<UiSelfTestCheck>> CheckBrowserInteractionsAsync(
        SyncRootContext config, string testRoot, bool saveShots = false)
    {
        var checks = new List<UiSelfTestCheck>();
        void Check(string name, bool passed, string detail) => checks.Add(new(name, passed, detail));
        var expectedIconSizes = new (double Scale, int PixelSize)[]
        {
            (1, 20), (1.25, 25), (1.5, 30), (1.75, 35), (2, 40), (2.25, 45),
            (2.5, 50), (3, 60), (3.5, 70), (4, 80), (5, 100),
        };
        foreach (var (scale, expectedPixels) in expectedIconSizes)
        {
            var metrics = SystemFileIconMetrics.FromRasterizationScale(scale);
            Check($"System icon display-pixel metrics ({scale:P0})", metrics.PixelSize == expectedPixels
                && metrics.SizeDip == SystemFileIconMetrics.LogicalSizeDip
                && Math.Abs(metrics.SizeDip * scale - expectedPixels) < 0.0001,
                $"Scale={scale:0.##}; requested={metrics.PixelSize}px; arranged={metrics.SizeDip:0.####} DIP "
                    + $"({metrics.SizeDip * scale:0.####} physical px).");
        }
        var lowDpi = SystemFileIconMetrics.FromRasterizationScale(0.5);
        var highDpi = SystemFileIconMetrics.FromRasterizationScale(20);
        Check("System icon DPI metrics keep the normal DIP layout",
            lowDpi.PixelSize == 10 && highDpi.PixelSize == 400
                && lowDpi.SizeDip == SystemFileIconMetrics.LogicalSizeDip
                && highDpi.SizeDip == SystemFileIconMetrics.LogicalSizeDip,
            $"Low DPI={lowDpi.PixelSize}px; high DPI={highDpi.PixelSize}px; layout={highDpi.SizeDip} DIP.");
        // The center source pixel contributes 1/9 of each destination pixel (3px -> 2px).
        var sourcePixels = new byte[3 * 3 * 4];
        new byte[] { 45, 90, 135, 180 }.CopyTo(sourcePixels, 4 * 4);
        var downsampled = SystemFileAssociation.DownsampleIconPixels(sourcePixels, 3, 2);
        Check("System icon downsampling averages premultiplied pixels across fractional edges",
            downsampled.AsSpan().SequenceEqual(new byte[] { 5, 10, 15, 20, 5, 10, 15, 20, 5, 10, 15, 20, 5, 10, 15, 20 }),
            $"3px -> 2px: {Convert.ToHexString(downsampled)}.");
        var dpiIconChecks = await Task.WhenAll(expectedIconSizes.Select(async size =>
            {
                var metrics = SystemFileIconMetrics.FromRasterizationScale(size.Scale);
                try
                {
                    var association = await SystemFileAssociation.QueryAsync(".png", metrics.PixelSize)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    var byteCount = association.IconPixels?.Length ?? 0;
                    return new UiSelfTestCheck($"Windows Shell PNG icon pixels ({size.Scale:P0})",
                        !string.IsNullOrWhiteSpace(association.TypeName)
                            && (metrics.PixelSize >= 48 || association.IconSourcePixelSize > metrics.PixelSize)
                            && byteCount == metrics.PixelSize * metrics.PixelSize * 4,
                        $"Shell source={association.IconSourcePixelSize}px; target={metrics.PixelSize}px; "
                            + $"returned={byteCount} bytes; type='{association.TypeName}'.");
                }
                catch (Exception error)
                {
                    return new UiSelfTestCheck($"Windows Shell PNG icon pixels ({size.Scale:P0})", false, error.ToString());
                }
            }));
        checks.AddRange(dpiIconChecks);
        var browser = new MainWindow(config, loadDiskTree: false);
        try
        {
            browser._updatingNavigationTree = true;
            var root = browser.CreateNavigationNode("云端", NavigationNodeKind.Root);
            var group = browser.CreateNavigationNode("单位", NavigationNodeKind.Container);
            var subgroup = browser.CreateNavigationNode("下层单位", NavigationNodeKind.Container);
            var diskNode = browser.CreateNavigationNode("TF2428", NavigationNodeKind.Disk);
            var folderNode = browser.CreateNavigationNode("右键目标", NavigationNodeKind.Folder);
            var nestedFolder = browser.CreateNavigationNode("下层文件夹", NavigationNodeKind.Folder);
            root.Children.Add(group);
            group.Children.Add(subgroup);
            subgroup.Children.Add(diskNode);
            diskNode.Children.Add(folderNode);
            folderNode.Children.Add(nestedFolder);
            browser._navigationTree.RootNodes.Add(root);
            var disk = new DiskTreeNode { Id = 10, Name = "TF2428", NodeType = "disk" };
            var diskState = new NavigationFolderState
            {
                Disk = disk, Id = 0, ParentId = 0, Name = disk.Name, Node = diskNode,
                Permission = DirectoryPermission.FullAccess, ChildrenLoaded = true,
            };
            var folderState = new NavigationFolderState
            {
                Disk = disk, Id = 20, ParentId = 0, Name = "右键目标", Node = folderNode,
                Permission = DirectoryPermission.FullAccess, ChildrenLoaded = true,
            };
            browser.RegisterFolderNode(diskState);
            browser.RegisterFolderNode(folderState);
            browser._currentNode = diskNode;
            browser._currentFolder = diskState.ToRemoteFolder();
            root.IsExpanded = group.IsExpanded = subgroup.IsExpanded = diskNode.IsExpanded = folderNode.IsExpanded = true;
            browser.Activate();
            for (var attempt = 0; attempt < 100 && browser._root.ActualWidth == 0; attempt++) await Task.Delay(50);
            browser._root.UpdateLayout();
            await Task.Delay(100);
            void CheckIndentation(TreeViewNode node, int depth, string scenario)
            {
                var row = Project<TreeViewItem>(browser._navigationTree.ContainerFromNode(node));
                var content = row is null ? null : NavigationTemplates.FindContentGrid(row);
                Check($"Navigation depth indentation ({scenario})", node.Depth == depth && content is not null
                    && Math.Abs(content.Padding.Left - depth * 8) < 0.01,
                    $"{node.Content}: depth={node.Depth}; rendered indent={content?.Padding.Left}; expected={depth * 8} DIP.");
            }
            CheckIndentation(root, 0, "root");
            CheckIndentation(group, 1, "unit");
            CheckIndentation(subgroup, 2, "nested unit");
            CheckIndentation(diskNode, 3, "disk");
            CheckIndentation(folderNode, 4, "folder");
            CheckIndentation(nestedFolder, 5, "nested folder");

            // Moving a populated subtree changes every descendant's depth, without changing
            // their node kinds. Verify the realized rows follow the new hierarchy.
            subgroup.Children.Remove(diskNode);
            group.Children.Add(diskNode);
            browser._root.UpdateLayout();
            await Task.Delay(100);
            CheckIndentation(diskNode, 2, "reparented disk");
            CheckIndentation(folderNode, 3, "reparented folder");
            CheckIndentation(nestedFolder, 4, "reparented nested folder");
            diskNode.IsExpanded = false;
            browser._root.UpdateLayout();
            diskNode.IsExpanded = true;
            browser._root.UpdateLayout();
            await Task.Delay(100);
            CheckIndentation(nestedFolder, 4, "reexpanded subtree");

            var container = Project<TreeViewItem>(browser._navigationTree.ContainerFromNode(diskNode));
            if (container is not null)
            {
                var point = container.TransformToVisual(browser._navigationTree)
                    .TransformPoint(new Windows.Foundation.Point(container.ActualWidth / 2, container.ActualHeight / 2));
                Check("Navigation pointer hit target", ReferenceEquals(
                    browser._navigationTree.NodeFromContainer(FindContainerAt<TreeViewItem>(browser._navigationTree, point)), diskNode),
                    "Hit testing the rendered row resolves its node rather than borrowing the selected folder.");
            }
            browser.ShowNavigationMenu(folderNode, null);
            Check("Navigation folder context target", browser._itemMenuTarget?.Items.Single().Id == 20
                && browser._itemMenuTarget.Parent.Id == 0 && browser._menuRename.IsEnabled,
                "Folder menu targets the clicked folder in its parent disk and exposes writable actions.");
            Check("Item cache-removal menu", browser._menuRemoveCache.Text == "移除缓存"
                && Equals(browser._itemMenu.Items[browser._itemMenu.Items.IndexOf(browser._menuDownload) + 1], browser._menuRemoveCache)
                && !browser._menuRemoveCache.IsEnabled,
                "The shared file/folder menu places cache removal after Download and disables it without a SyncRoot connection.");
            browser._itemMenu.Hide();

            var oldItem = new YuWangItem { Id = 30, Name = "先前选中.txt", Type = 'F', Size = 1024 };
            var draggedItem = new YuWangItem
            {
                Id = 31, Name = "实际拖拽.txt", Type = 'F', Size = 2048, UploadTime = new DateTime(2026, 11, 20, 9, 30, 0),
            };
            browser._currentItems = [oldItem, draggedItem];
            browser.RenderItems();
            await Task.Delay(200); // Let initial shell type metadata arrive before the sort stress.
            var firstItemRow = browser._items.Items.OfType<ListViewItem>().FirstOrDefault();
            var rowGrid = firstItemRow?.Content as Grid;
            var namePanel = rowGrid?.Children.OfType<Grid>().FirstOrDefault();
            var iconHost = namePanel?.Children.OfType<Border>().FirstOrDefault();
            browser._root.UpdateLayout();
            var originalWindowSize = browser.AppWindow.Size;
            var originalSidebarWidth = browser._preferredSidebarWidth;
            var originalNameWidth = browser._fileColumnWidths[0];
            var accountFooter = (Grid)browser._root.FindName("CloudAccountFooter");
            var addressBox = (Border)browser._root.FindName("CloudAddressBox");
            var diskHeader = (Grid)browser._root.FindName("CloudDiskHeader");
            var filePane = (Border)browser._root.FindName("CloudFilePane");
            var typeCell = rowGrid!.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 3);
            var savedFolder = browser._currentFolder;
            browser._currentFolder = savedFolder! with { Permission = DirectoryPermission.ReadOnly };
            browser.UpdatePermissionBadge();
            browser.RenderBreadcrumbs();
            var breadcrumbIcon = browser._breadcrumbItems.Children.OfType<FontIcon>().First();
            browser.ToggleFavorite(diskState);
            var favoriteButton = browser._favoriteRows.Children.OfType<Button>().First();
            var favoriteContent = (Grid)favoriteButton.Content;
            var favoriteText = favoriteContent.Children.OfType<TextBlock>().Single();
            var favoriteIcon = favoriteContent.Children.OfType<FontIcon>().Single();
            var titleBar = browser.AppWindow.TitleBar;
            checks.AddRange(await WinUiUiHost.CheckLiveThemeAsync((FrameworkElement)browser.Content,
                ("File pane surface follows theme", () => WinUiUiHost.BrushColor(filePane.Background)),
                ("Breadcrumb surface follows theme", () => WinUiUiHost.BrushColor(addressBox.Background)),
                ("Breadcrumb glyph follows theme", () => WinUiUiHost.BrushColor(breadcrumbIcon.Foreground)),
                ("Existing file type text follows theme", () => WinUiUiHost.BrushColor(typeCell.Foreground)),
                ("Selected favorite surface follows theme", () => WinUiUiHost.BrushColor(favoriteButton.Background)),
                ("Existing favorite name follows theme", () => WinUiUiHost.BrushColor(favoriteText.Foreground)),
                ("Existing favorite glyph follows theme", () => WinUiUiHost.BrushColor(favoriteIcon.Foreground)),
                ("Read-only badge follows theme", () => WinUiUiHost.BrushColor(browser._permissionBadge.Background)),
                ("Read-only glyph follows theme", () => WinUiUiHost.BrushColor(browser._permissionGlyph.Foreground)),
                ("Read-only text follows theme", () => WinUiUiHost.BrushColor(browser._permissionText.Foreground)),
                ("Active caption buttons follow theme", () => titleBar.ButtonForegroundColor),
                ("Inactive caption buttons follow theme", () => titleBar.ButtonInactiveForegroundColor)));
            browser.ToggleFavorite(diskState);
            browser._currentFolder = savedFolder;
            browser.UpdatePermissionBadge();
            var commandHost = (Grid)browser._root.FindName("CloudCommandBarHost");
            var statusFooter = browser._statusBar!;
            var layoutScale = browser._root.XamlRoot?.RasterizationScale ?? 1;
            var layoutTolerance = 1 / layoutScale + 0.01;

            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(browser._root)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bool Aligned(params double[] points) => points.Max() - points.Min() <= layoutTolerance;
            double CenterX(FrameworkElement element) => Bounds(element).Left + element.ActualWidth / 2;
            double CenterY(FrameworkElement element) => Bounds(element).Top + element.ActualHeight / 2;
            void CheckWindowGuides(string scenario)
            {
                var baseline = new[] { browser._reloadDisks, browser._back, browser._forward, browser._up, browser._refresh }
                    .Select(CenterY).Append(CenterY(addressBox)).Append(CenterY(browser._search)).ToArray();
                Check($"Address controls share a center line ({scenario})", Aligned(baseline)
                    && Aligned(Bounds(diskHeader).Bottom, Bounds(addressBox).Bottom + ToolbarBottomPadding),
                    $"Vertical centers: {string.Join(", ", baseline.Select(y => y.ToString("F2")))} DIP.");
                var left = new[] { Bounds(browser._back).Left, Bounds(browser._commandBar).Left, Bounds(browser._summary).Left };
                var right = new[] { Bounds(browser._search).Right, Bounds(commandHost).Right - commandHost.Padding.Right,
                    Bounds(statusFooter).Right - statusFooter.Padding.Right };
                Check($"Content regions share side insets ({scenario})", Aligned(left) && Aligned(right),
                    $"Left spread={left.Max() - left.Min():F2} DIP; right spread={right.Max() - right.Min():F2} DIP.");
                Check($"Sidebar and content footers align ({scenario})", Aligned(Bounds(accountFooter).Top, Bounds(statusFooter).Top)
                    && Aligned(CenterY(browser._settings), CenterY(browser._summary))
                    && Aligned(CenterX(browser._settings), CenterX(browser._favoriteCurrent), CenterX(browser._reloadDisks)),
                    $"Account divider={Bounds(accountFooter).Top:F2} DIP; status divider={Bounds(statusFooter).Top:F2} DIP.");
            }

            void CheckColumnGuides(string scenario)
            {
                var row = browser._items.Items.OfType<ListViewItem>().First().Content as Grid
                    ?? throw new InvalidOperationException("Missing file row.");
                var offsets = new double[5];
                for (var column = 0; column < offsets.Length; column++)
                {
                    var header = browser._columnsHeader.Children.OfType<FrameworkElement>()
                        .First(child => Grid.GetColumn(child) == column);
                    var label = header is Button { Content: StackPanel sortLabel }
                        ? sortLabel.Children.OfType<TextBlock>().Single() : (TextBlock)header;
                    var cell = row.Children.OfType<FrameworkElement>().Single(child => Grid.GetColumn(child) == column);
                    if (cell is Grid nameCell) cell = nameCell.Children.OfType<TextBlock>().Single();
                    offsets[column] = column switch
                    {
                        1 => CenterX(label) - CenterX(cell),
                        4 => Bounds(label).Right - Bounds(cell).Right,
                        _ => Bounds(label).Left - Bounds(cell).Left,
                    };
                }
                Check($"All file headers align with their row cells ({scenario})",
                    offsets.All(offset => Math.Abs(offset) <= layoutTolerance),
                    $"Name/status/time/type/size offsets: {string.Join(", ", offsets.Select(x => x.ToString("F2")))} DIP.");
            }

            CheckWindowGuides("default");
            CheckColumnGuides("single selection");
            Check("Size values are right-aligned", browser._sizeSortHeader?.HorizontalContentAlignment == HorizontalAlignment.Right
                && rowGrid?.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 4).TextAlignment == TextAlignment.Right,
                "The header and numeric values remain right-aligned.");
            var iconScale = browser._root.XamlRoot?.RasterizationScale ?? 1;
            Windows.Foundation.Point? iconOrigin = null;
            try
            {
                if (iconHost is not null)
                    iconOrigin = iconHost.TransformToVisual(browser._root)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
            }
            catch (InvalidOperationException)
            {
            }
            var iconOriginPixelAligned = iconOrigin is { } origin
                && Math.Abs(origin.X * iconScale - Math.Round(origin.X * iconScale)) < 0.001
                && Math.Abs(origin.Y * iconScale - Math.Round(origin.Y * iconScale)) < 0.001;
            var iconSizePixelAligned = iconHost is not null
                && Math.Abs(iconHost.ActualWidth * iconScale - browser._systemFileIconMetrics.PixelSize) < 0.001
                && Math.Abs(iconHost.ActualHeight * iconScale - browser._systemFileIconMetrics.PixelSize) < 0.001;
            Check("File-list icons use pixel-aligned layout", firstItemRow?.UseLayoutRounding == true
                && rowGrid?.UseLayoutRounding == true && namePanel?.UseLayoutRounding == true
                && iconHost?.UseLayoutRounding == true
                && Math.Abs((iconHost?.Width ?? 0) - browser._systemFileIconMetrics.SizeDip) < 0.0001
                && iconOriginPixelAligned && iconSizePixelAligned,
                $"Scale={iconScale:0.##}; "
                    + $"pixels={browser._systemFileIconMetrics.PixelSize}; "
                    + $"layout={iconHost?.Width:0.####} DIP; "
                    + $"origin=({iconOrigin?.X * iconScale:0.####}, {iconOrigin?.Y * iconScale:0.####}) px; "
                    + $"origin aligned={iconOriginPixelAligned}; size aligned={iconSizePixelAligned}.");
            var correctedFractionalParentOffset = false;
            var appliedCorrectionX = 0d;
            var appliedCorrectionY = 0d;
            if (rowGrid is not null && iconHost is not null
                && iconHost.RenderTransform is TranslateTransform iconTranslation)
            {
                var previousRowTransform = rowGrid.RenderTransform;
                try
                {
                    rowGrid.RenderTransform = new TranslateTransform { X = 0.25, Y = 0.25 };
                    browser._root.UpdateLayout();
                    browser.SnapFileIconToPhysicalPixels(iconHost, iconTranslation);
                    browser._root.UpdateLayout();
                    appliedCorrectionX = iconTranslation.X * iconScale;
                    appliedCorrectionY = iconTranslation.Y * iconScale;
                    var shiftedOrigin = iconHost.TransformToVisual(browser._root)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    correctedFractionalParentOffset =
                        (Math.Abs(appliedCorrectionX) > 0.01 || Math.Abs(appliedCorrectionY) > 0.01)
                        && Math.Abs(shiftedOrigin.X * iconScale - Math.Round(shiftedOrigin.X * iconScale)) < 0.001
                        && Math.Abs(shiftedOrigin.Y * iconScale - Math.Round(shiftedOrigin.Y * iconScale)) < 0.001;
                }
                finally
                {
                    rowGrid.RenderTransform = previousRowTransform;
                    browser._root.UpdateLayout();
                    browser.SnapFileIconToPhysicalPixels(iconHost, iconTranslation);
                    browser._root.UpdateLayout();
                }
            }
            Check("File-list icon snap removes fractional parent offsets", correctedFractionalParentOffset,
                $"Injected parent offset=0.25 DIP; applied correction="
                    + $"({appliedCorrectionX:0.####}, {appliedCorrectionY:0.####}) px.");
            foreach (var extension in new[] { ".txt", ".png" })
            {
                try
                {
                    var pixelSize = browser._systemFileIconMetrics.PixelSize;
                    var association = await SystemFileAssociation.QueryAsync(extension, pixelSize)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    var iconByteCount = association.IconPixels?.Length ?? 0;
                    Check($"Windows Shell file association ({extension})",
                        !string.IsNullOrWhiteSpace(association.TypeName)
                            && iconByteCount == pixelSize * pixelSize * 4,
                        $"Type='{association.TypeName}'; requested={pixelSize}px; icon bytes={iconByteCount}.");
                }
                catch (Exception error)
                {
                    Check($"Windows Shell file association ({extension})", false, error.ToString());
                }
            }

            try
            {
                var typeOnly = await SystemFileAssociation.QueryAsync(".png", includeIcon: false)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Check("Windows Shell association can read the type without extracting an icon",
                    !string.IsNullOrWhiteSpace(typeOnly.TypeName)
                        && typeOnly.IconPixels is null && typeOnly.IconSourcePixelSize is null,
                    $"Type='{typeOnly.TypeName}'; icon bytes={typeOnly.IconPixels?.Length ?? 0}.");
            }
            catch (Exception error)
            {
                Check("Windows Shell association can read the type without extracting an icon", false, error.ToString());
            }

            try
            {
                var probePath = Path.Combine(SystemIconProbeDirectory(), "thumbnail-selftest.png");
                if (!File.Exists(probePath)) await File.WriteAllBytesAsync(probePath, []);
                var probeFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(probePath);
                using var thumbnail = await probeFile.GetThumbnailAsync(ThumbnailMode.ListView,
                    checked((uint)browser._systemFileIconMetrics.PixelSize), ThumbnailOptions.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var thumbnailType = thumbnail?.Type.ToString() ?? "<null>";
                var thumbnailIcon = await ToIconImageAsync(thumbnail);
                var pngIcon = await browser.GetSystemFileIconAsync("system-association-selftest.png",
                        browser._systemFileIconMetrics.PixelSize)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                var pngTypeName = browser._systemFileTypes.GetValueOrDefault(".png");
                Check("GetThumbnailAsync file icon with Shell fallback (.png)", thumbnailIcon is not null || pngIcon is not null,
                    $"ThumbnailType={thumbnailType}; WinRT image={thumbnailIcon?.GetType().Name ?? "<null>"}; "
                        + $"final image={pngIcon?.GetType().Name ?? "<null>"}; "
                        + $"requested={browser._systemFileIconMetrics.PixelSize}px.");
                Check("Main window UWP file type and icon (.png)", pngIcon is not null
                    && !string.IsNullOrWhiteSpace(pngTypeName) && pngTypeName != "文件",
                    $"Type='{pngTypeName}'; image source={pngIcon?.GetType().Name ?? "<null>"}.");
                try { File.Delete(probePath); }
                catch (Exception error) { Serilog.Log.Debug(error, "清理 GetThumbnailAsync 自检文件失败：{Path}", probePath); }
            }
            catch (Exception error)
            {
                Check("Main window UWP file type and icon (.png)", false, error.ToString());
            }

            async Task SaveBrowserLayoutAsync(string name)
            {
                // RenderTargetBitmap omits the native Mica backdrop. Use its theme base color
                // in snapshots so the sidebar text remains visible on the exported PNG.
                var background = browser._root.Background;
                try
                {
                    browser._root.Background = WinUiUiHost.GetBrushResource("SolidBackgroundFillColorBaseBrush");
                    await WinUiUiHost.SaveWindowPngAsync(browser._root, name, testRoot);
                }
                finally { browser._root.Background = background; }
            }

            if (saveShots) await SaveBrowserLayoutAsync("browser-file-icons-and-columns");
            try
            {
                browser.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(900 * layoutScale), (int)Math.Round(560 * layoutScale)));
                browser._preferredSidebarWidth = MaximumSidebarWidth;
                browser.ConstrainSidebarWidth();
                browser.SetCheckboxSelectionEnabled(true);
                browser._fileColumnWidths[0] = 300;
                browser.UpdateFileColumnWidths();
                await Task.Delay(300); // Let the resize and WinUI multi-select template transitions settle.
                browser._root.UpdateLayout();
                Check("Compact browser uses horizontal file scrolling", browser._root.ActualWidth < 930
                    && browser._fileScroller.ActualWidth < CompactStatusWidth && browser._fileScroller.ScrollableWidth > 80,
                    $"Window={browser._root.ActualWidth:F2}; file viewport={browser._fileScroller.ActualWidth:F2}; "
                        + $"scrollable={browser._fileScroller.ScrollableWidth:F2} DIP.");
                CheckWindowGuides("compact");
                CheckColumnGuides("multi-selection after window and column resize");
                if (saveShots) await SaveBrowserLayoutAsync("browser-compact-layout");
                browser._fileScroller.ChangeView(80, null, null, true);
                await Task.Delay(100);
                Check("File table scrolls horizontally", browser._fileScroller.HorizontalOffset >= 79,
                    $"Horizontal offset={browser._fileScroller.HorizontalOffset:F2} DIP.");
                CheckColumnGuides("multi-selection after horizontal scroll");
            }
            finally
            {
                browser._fileScroller.ChangeView(0, null, null, true);
                browser.SetCheckboxSelectionEnabled(false);
                browser._fileColumnWidths[0] = originalNameWidth;
                browser._preferredSidebarWidth = originalSidebarWidth;
                browser.AppWindow.Resize(originalWindowSize);
                browser.ConstrainSidebarWidth();
                browser.UpdateFileColumnWidths();
                await Task.Delay(250);
                browser._root.UpdateLayout();
            }

            try
            {
                var folderIcon = await browser.GetSystemFolderIconAsync(browser._systemFileIconMetrics.PixelSize)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Check("Main window file-folder icon", folderIcon is not null,
                    $"Image source={folderIcon?.GetType().Name ?? "<null>"}; "
                        + $"layout={browser._systemFileIconMetrics.SizeDip} DIP; "
                        + $"request={browser._systemFileIconMetrics.PixelSize}px.");
            }
            catch (Exception error)
            {
                Check("Main window file-folder icon", false, error.ToString());
            }

            var beforeSort = browser._fileRenderVersion;
            browser._items.SelectedIndex = 0;
            var selectedBeforeSort = (browser._items.SelectedItem as ListViewItem)?.Tag;
            for (var i = 0; i < 40; ++i)
            {
                browser._sortColumn = (SortColumn)(i % 4);
                browser._sortAscending = i % 2 == 0;
                browser.UpdateSortHeaders();
                browser.QueueFileSort();
            }
            await Task.Delay(350);
            Check("Rapid file sorts publish one snapshot", browser._fileRenderVersion == beforeSort + 1
                && browser._items.Items.Count == 2 && selectedBeforeSort is not null
                && ReferenceEquals(selectedBeforeSort, (browser._items.SelectedItem as ListViewItem)?.Tag),
                "Forty rapid sort requests are coalesced and selection survives the reordered list.");
            Check("SyncRoot file availability column and menu commands", browser._columnsHeader.ColumnDefinitions.Count == 5
                && browser._columnsHeader.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(cell => cell.Text == "状态")
                && browser._menuDownload.Text == "下载" && browser._menuSaveAs.Text == "另存为…",
                "Availability is next to the filename; hydration and export are separate commands.");
            var errors = new SyncRootDownloadFailures();
            var file = new FileIdentity(10, 20, oldItem.Id, 1);
            var beforeFailure = errors.Observe(file);
            errors.Record(file);
            var downloadStatus = ReadFileSyncStatus(10, 20, oldItem, errors);
            var independentStatus = ReadFileSyncStatus(10, 21, oldItem, errors);
            var cell = new Border();
            browser.SetAvailability(cell, downloadStatus.Availability, downloadStatus.Error);
            var downloadIcon = cell.Child is Border { Child: FontIcon { Glyph: "\uE711" } }
                && ToolTipService.GetToolTip(cell) is string downloadHint && downloadHint.Contains("下载失败");
            browser.SetAvailability(cell, SyncRootAvailability.Local, FileSyncError.Writeback);
            var writebackIcon = cell.Child is Border { Child: FontIcon { Glyph: "\uE711" } }
                && ToolTipService.GetToolTip(cell) is string uploadHint && uploadHint.Contains("同步错误");
            browser.SetAvailability(cell, SyncRootAvailability.Local);
            Check("SyncRoot error state is distinct from availability", downloadIcon && writebackIcon
                && independentStatus.Error == FileSyncError.None && cell.Child is Border { Child: FontIcon { Glyph: "\uE73E" } },
                "Only the failing file shows a red X; tooltips distinguish download and writeback failures.");
            Check("SyncRoot download error clears only on verified recovery",
                errors.HasFailure(file, SyncRootAvailability.Local, beforeFailure)
                && errors.HasFailure(file, SyncRootAvailability.OnlineOnly, errors.Observe(file))
                && !errors.HasFailure(file, SyncRootAvailability.Pinned, errors.Observe(file)) && errors.Observe(file) == 0,
                "A stale scan or partial download cannot clear an error; a verified complete file can.");
            browser._currentItems = Enumerable.Range(1, 15).Select(i => new YuWangItem
            {
                Id = (ulong)(100 + i), Name = $"排序测试 {16 - i}.pptx", Type = 'F', Size = i * 1024L,
                UploadTime = new DateTime(2020, 1, i),
            }).ToList();
            browser.RenderItems();
            var completedSorts = 0;
            for (var i = 0; i < 8; ++i)
            {
                var before = browser._fileRenderVersion;
                browser._sortColumn = (SortColumn)(i % 4);
                browser._sortAscending = i >= 4;
                browser.UpdateSortHeaders();
                browser.QueueFileSort();
                for (var attempt = 0; attempt < 20 && browser._fileRenderVersion == before; attempt++)
                    await Task.Delay(25);
                if (browser._fileRenderVersion > before && browser._items.Items.Count == 15) completedSorts++;
            }
            Check("Repeated file sort renders complete", completedSorts == 8,
                "Eight separate WinUI reorder passes complete for a fifteen-item folder.");
            browser._currentItems = [oldItem, draggedItem];
            browser.RenderItems();
            var oldRow = browser._items.Items.OfType<ListViewItem>().Single(row => ReferenceEquals(row.Tag, oldItem));
            var draggedRow = browser._items.Items.OfType<ListViewItem>().Single(row => ReferenceEquals(row.Tag, draggedItem));
            browser._items.SelectedItem = oldRow;
            browser.SelectDragSource(draggedRow);
            Check("Drag unselected row selection", browser.SelectedEntries().SequenceEqual([draggedItem]),
                "Beginning on an unselected row replaces the previous selection.");
            Check("Drag intent is intercepted before OLE", !browser._items.CanDragItems && !browser._outboundDragActive,
                "WinUI automatic drag is disabled; pointer threshold starts asynchronous preparation.");
            var path = Path.Combine(testRoot, draggedItem.Name);
            await File.WriteAllTextAsync(path, "browser drag fixture");
            using (var prepared = await DragFilePreparation.CreateAsync([path], () => true, _ => { }, CancellationToken.None))
            {
                prepared.ValidateReady();
                Check("Prepared drag Shell/COM data", NativeFileDrag.CheckNativeDataObject(path),
                    "No paths are present during preparation; CF_HDROP supplies the correct Unicode path afterward; native IDropSource handles drop/cancel/backend loss.");
            }
            var blocked = false;
            try { using var prepared = await DragFilePreparation.CreateAsync([path], () => false, _ => { }, CancellationToken.None); }
            catch (IOException) { blocked = true; }
            Check("Unavailable drag backend is blocked", blocked, "No copy data is offered when the sync engine is unavailable.");

            var originalBackend = browser._dragBackendAvailable;
            browser._dragBackendAvailable = static () => true;
            browser._dragFixturePaths = [];
            browser._dragFixturePaths[(browser._currentFolder!.Disk.Id, draggedItem.Id)] = path;
            var manifest = await browser.BuildDragManifestAsync(browser._currentFolder, [draggedItem], CancellationToken.None);
            browser._dragBackendAvailable = originalBackend;
            Check("Drag metadata preparation does not hydrate", manifest.Entries.Count == 1
                && manifest.Entries[0].Name == draggedItem.Name && manifest.Entries[0].Size == new FileInfo(path).Length
                && !manifest.Entries[0].IsPreparationStarted,
                "Selection prepares accurate metadata, without starting file extraction or a custom preparation image.");
            var aggregate = new DragManifest();
            aggregate.AddFile("缓存文件.bin", 1024, 1024, ready: true);
            var hydratedProgress = aggregate.AddFile("在线文件.bin", 3072, 0);
            hydratedProgress(new("正在下载文件", "在线文件.bin", 1024, 3072));
            Check("Drag progress includes cached files", aggregate.Progress.Latest is
                { CompletedBytes: 2048, TotalBytes: 4096, Percent: 50, CompletedFiles: 1, TotalFiles: 2 },
                "Aggregate hydration progress counts already cached bytes and ready files before their streams are requested.");
            var preview = DragItemPreview.CheckLayout();
            Check("Live drag preview is rounded, compact and reuses the file-list image", preview.Passed, preview.Detail);
            browser.ShowDragPreparationProgress(new("正在下载文件", "精确进度.txt", 512, 2048, 1, 3));
            Check("Drag hydration progress UI", browser._transferProgress.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                && !browser._transferProgress.IsIndeterminate && browser._transferProgress.Value == 25
                && browser._status.Text.Contains("25.00%")
                && browser._status.Text.Contains("512 B / 2 KB")
                && browser._status.Text.Contains("1/3") && browser._status.Text.Contains("精确进度.txt")
                && browser._status.TextWrapping == Microsoft.UI.Xaml.TextWrapping.NoWrap
                && browser._cancelDragPreparation.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
                "The status bar shows actual percentage, suitable size units, file count, filename and a cancel button.");
            browser._root.UpdateLayout();
            var statusBarHeight = ((Microsoft.UI.Xaml.FrameworkElement)browser._status.Parent).ActualHeight;
            const string longName = "一个非常非常非常非常非常非常非常非常非常非常长的文件名称用于测试截断效果.docx";
            browser.ShowDragPreparationProgress(new("正在下载文件", longName, 512, 2048, 1, 3));
            browser._root.UpdateLayout();
            Check("Drag progress keeps status bar height",
                Math.Abs(((Microsoft.UI.Xaml.FrameworkElement)browser._status.Parent).ActualHeight - statusBarHeight) < 0.5
                && browser._status.Text.Contains('…') && browser._status.Text.EndsWith(".docx", StringComparison.Ordinal),
                "Long names are middle-truncated (keeping the extension) and progress never grows the status bar.");
            var shortName = ShortenFileName(longName);
            const string emojiName = "👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻👩‍💻.txt";
            Check("Status filename limit and tooltip", shortName.Length == 12
                && ShortenFileName("12345678.txt") == "12345678.txt"
                && ShortenFileName(emojiName) == "👩‍💻👩‍💻👩‍💻👩‍💻…👩‍💻👩‍💻👩‍💻.txt"
                && Equals(ToolTipService.GetToolTip(browser._status), browser._status.Text[..^shortName.Length] + longName),
                "The 12-character limit includes the ellipsis and extension, respects Unicode and keeps the complete name in the tooltip.");
            var statusText = browser._status.Text;
            var progressText = statusText[..^(shortName.Length + " · ".Length)];
            browser._status.Text = progressText;
            browser._root.UpdateLayout();
            browser._status.MaxWidth = browser._status.ActualWidth + 8;
            browser._status.Text = statusText;
            browser._root.UpdateLayout();
            Check("Progress before filename in narrow status bar", progressText.Contains("25.00% · 512 B / 2 KB")
                && statusText.EndsWith(" · " + shortName, StringComparison.Ordinal) && browser._status.IsTextTrimmed,
                "With limited text width the end ellipsis affects the filename first, after the progress numbers.");
            browser._status.MaxWidth = double.PositiveInfinity;
            var statusBar = browser._statusBar!;
            statusBar.Width = 430;
            browser._root.UpdateLayout();
            await Task.Delay(50);
            Check("Compact status bar prioritizes progress text", browser._summary.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed
                && browser._transferProgress.Width < TransferGaugeWidth && browser._status.ActualWidth > 240,
                "A narrow layout reclaims summary and gauge width while leaving the progress text and cancel action visible.");
            statusBar.Width = double.NaN;
            browser.UpdatePermissionBadge();
            browser._root.UpdateLayout();
            Check("Progress and permission controls align within the footer", browser._permissionBadge.Visibility == Visibility.Visible
                && Aligned(CenterY(browser._summary), CenterY(browser._status), CenterY(browser._transferProgress),
                    CenterY(browser._cancelDragPreparation), CenterY(browser._permissionGlyph), CenterY(browser._permissionText))
                && Aligned(Bounds(accountFooter).Top, Bounds(statusBar).Top),
                "Progress text, gauge, cancel and permission stay centered without moving the footer divider.");
            CheckColumnGuides("size sort indicator visible");
            if (saveShots) await SaveBrowserLayoutAsync("browser-drag-progress");
            browser.ShowDragPreparationProgress(new("正在下载文件", "大文件.bin", 1610612736, 3221225472));
            Check("Drag progress uses common units", browser._status.Text.Contains("1.5 GB / 3 GB")
                && !browser._status.Text.Contains("字节"), "Large file progress uses matching readable units.");
            using (var cancel = new CancellationTokenSource())
            {
                browser._dragCancellation = cancel;
                browser.CancelActiveDragPreparation();
                Check("Drag preparation cancel button", cancel.IsCancellationRequested && !browser._cancelDragPreparation.IsEnabled,
                    "The cancel action stops preparation and prevents repeated requests.");
                browser._dragCancellation = null;
                browser._cancelDragPreparation.IsEnabled = true;
            }
            browser.ShowDragPreparationProgress(new("正在统计文件", "测试文件夹"));
            Check("Drag scan progress is indeterminate", browser._transferProgress.IsIndeterminate
                && !browser._status.Text.Contains('%'), "An unknown total never displays an invented percentage.");
            var staleProgress = new DragProgressState();
            browser._dragProgressState = staleProgress;
            browser.StopDragPreparationProgress(clearStatus: true);
            staleProgress.Report(new("正在下载文件", "旧任务.txt", 1024, 2048));
            browser.UpdateDragPreparationProgress();
            Check("Drag progress cancellation cleanup", browser._transferProgress.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed
                && !browser._transferProgress.IsIndeterminate && !browser._dragProgressTimer.IsEnabled
                && browser._status.Text.Length == 0,
                "Completion and cancellation hide progress; late samples from an old selection cannot change the UI.");
            checks.AddRange(await DragItemPreview.CheckVisualsAsync(layoutScale));
            if (Environment.GetCommandLineArgs().Contains("--ui-selftest-pointer-drag", StringComparer.OrdinalIgnoreCase))
            {
                var externalPointer = Environment.GetCommandLineArgs().Contains("--ui-selftest-external-pointer", StringComparer.OrdinalIgnoreCase);
                var injectedTouch = Environment.GetCommandLineArgs().Contains("--ui-selftest-touch-ole", StringComparer.OrdinalIgnoreCase)
                    || Environment.GetCommandLineArgs().Contains("--ui-selftest-injected-touch", StringComparer.OrdinalIgnoreCase);
                if (!externalPointer)
                    checks.Add(await browser.CheckWinUiPointerDragAsync(draggedRow, path, testRoot, cancel: true,
                        injectedTouch: injectedTouch, additionalSelection: oldRow));
                checks.Add(await browser.CheckWinUiPointerDragAsync(draggedRow, path, testRoot, injectedTouch: injectedTouch));
                if (!externalPointer)
                {
                    checks.Add(await browser.CheckWinUiPointerDragAsync(draggedRow, path, testRoot,
                        injectedTouch: injectedTouch, additionalSelection: oldRow));
                    checks.Add(await browser.CheckWinUiPointerDragAsync(draggedRow, path, testRoot, repeat: true,
                        injectedTouch: injectedTouch, additionalSelection: oldRow));
                }
            }
            if (browser._isClosed) return checks;

            var infoBarAnimationsEnabled = browser._uiSettings.AnimationsEnabled;
            browser.ShowInfo(InfoBarSeverity.Success, "动画测试", "成功提示应平滑展开。");
            await Task.Delay(350);
            Check("Success InfoBar entrance settles", browser._infoBar.IsOpen
                && browser._infoBarHost.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                && browser._infoBarHost.Opacity == 1 && double.IsNaN(browser._infoBarHost.Height)
                && !browser._infoBarAnimating,
                "The notification ends fully visible at its natural height.");
            Check("Success countdown uses continuous animation", !infoBarAnimationsEnabled
                || browser._infoBarCountdownAnimation is { Target: "Scale" } countdown
                    && countdown.Duration == browser._infoBarCloseTimer.Interval,
                "A compositor scale animation spans the entire auto-close interval rather than stepping on timer ticks.");
            browser._infoBar.IsOpen = false;
            Check("Success InfoBar defers closing for exit", !infoBarAnimationsEnabled
                || browser._infoBarAnimating && !browser._infoBarAnimationShowing
                    && browser._infoBarCountdown.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
                "The green countdown stays with the bar while it fades and collapses.");
            await Task.Delay(60);
            browser.ShowInfo(InfoBarSeverity.Success, "替换成功提示", "正在收起时重新显示。");
            await Task.Delay(350);
            Check("Success InfoBar reverses an interrupted close", browser._infoBar.IsOpen
                && browser._infoBar.Title == "替换成功提示" && browser._infoBarHost.Opacity == 1
                && double.IsNaN(browser._infoBarHost.Height) && browser._infoBarCloseTimer.IsEnabled,
                "A new notification cancels the previous exit and receives a fresh countdown.");
            browser._infoBar.IsOpen = false;
            await Task.Delay(350);
            Check("Success InfoBar exit cleans up", !browser._infoBar.IsOpen && !browser._infoBarAnimating
                && browser._infoBarHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed
                && browser._infoBarCountdown.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed
                && !browser._infoBarCloseTimer.IsEnabled,
                "Manual closing releases the notification row, countdown and rendering handler.");
            var closeInterval = browser._infoBarCloseTimer.Interval;
            try
            {
                browser._infoBarCloseTimer.Interval = TimeSpan.FromMilliseconds(300);
                browser.ShowInfo(InfoBarSeverity.Success, "自动关闭", "倒计时结束后平滑消失。");
                await Task.Delay(800);
                Check("Success InfoBar auto-close animates and settles", !browser._infoBar.IsOpen
                    && browser._infoBarHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed
                    && !browser._infoBarAnimating && browser._infoBarCountdownAnimation is null,
                    "The timeout follows the same exit and cleanup path as manual closing.");
            }
            finally { browser._infoBarCloseTimer.Interval = closeInterval; }
            browser.ShowInfo(InfoBarSeverity.Success, "旧成功提示", "即将被警告替换。");
            browser._infoBar.IsOpen = false;
            browser.ShowInfo(InfoBarSeverity.Warning, "新警告", "应保持显示。");
            await Task.Delay(350);
            Check("InfoBar replacement cancels stale success dismissal", browser._infoBar.IsOpen
                && browser._infoBar.Severity == InfoBarSeverity.Warning
                && !browser._infoBarCloseTimer.IsEnabled && browser._infoBarCountdownAnimation is null,
                "A warning replacing an exiting success notification remains visible without its countdown.");
            browser._infoBar.IsOpen = false;
            await Task.Delay(350);
            browser._savedState.FavoritesExpanded = true;
            browser.ConstrainFavoritesHeight();
            browser._root.UpdateLayout();
            var expandedHeight = browser._favoritesRow.Height.Value;
            browser._savedState.FavoritesExpanded = false;
            browser.ConstrainFavoritesHeight(animate: true);
            await Task.Delay(80);
            var duringCollapse = browser._favoritesRow.Height.Value;
            var animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            Check("Favorites collapse animation", !animationsEnabled || duringCollapse > CollapsedFavoritesHeight && duringCollapse < expandedHeight,
                $"Height transitions from {expandedHeight:F1} through {duringCollapse:F1} DIP.");
            browser._savedState.FavoritesExpanded = true;
            browser.ConstrainFavoritesHeight(animate: true);
            await Task.Delay(300);
            Check("Favorites reversed animation settles", !browser._favoritesAnimating
                && Math.Abs(browser._favoritesRow.Height.Value - expandedHeight) < 0.1 && browser._favoritesBody.Opacity == 1,
                "A rapid reverse ends at the expanded height with visible contents.");
            if (saveShots) await SaveBrowserLayoutAsync("browser-favorites-expanded");
        }
        catch (Exception error) { Check("Browser interaction self-test", false, error.ToString()); }
        finally { if (!browser._isClosed) browser.Close(); }
        return checks;
    }
}
