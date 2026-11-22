using Microsoft.UI.Xaml.Controls;
using YuWangCloudSync.CloudFiles;
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
            browser._itemMenu.Hide();

            var oldItem = new YuWangItem { Id = 30, Name = "先前选中.txt", Type = 'F' };
            var draggedItem = new YuWangItem { Id = 31, Name = "实际拖拽.txt", Type = 'F' };
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

            browser._savedState.FavoritesExpanded = true;
            browser.ConstrainFavoritesHeight();
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
            Check("Favorites reversed animation settles", !browser._favoritesAnimation.IsEnabled
                && Math.Abs(browser._favoritesRow.Height.Value - expandedHeight) < 0.1 && browser._favoritesBody.Opacity == 1,
                "A rapid reverse ends at the expanded height with visible contents.");
            if (saveShots) await WinUiUiHost.SaveWindowPngAsync(browser._root, "browser-favorites-expanded", testRoot);
        }
        catch (Exception error) { Check("Browser interaction self-test", false, error.ToString()); }
        finally { browser.Close(); }
        return checks;
    }
}
