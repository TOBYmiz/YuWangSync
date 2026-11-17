using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Tray;

internal sealed class KeptDirectoriesWindow : Window
{
    private readonly List<Program.KeptDirectoryRefreshInfo> _directories;
    private readonly Func<Program.KeptDirectoryRefreshInfo, bool> _remove;
    private readonly StackPanel _list = new() { Spacing = 4 };

    internal KeptDirectoriesWindow(IReadOnlyList<Program.KeptDirectoryRefreshInfo> directories,
        Func<Program.KeptDirectoryRefreshInfo, bool> remove)
    {
        _directories = directories.ToList();
        _remove = remove;
        Title = $"{ProductIdentity.DisplayNameZh} - 管理保持更新的目录";

        var root = new Grid { Name = "KeptDirectoriesRoot", Margin = new Thickness(18), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(scroll);
        var close = new Button { Content = "完成", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 1);
        root.Children.Add(close);
        WinUiWindowChrome.Attach(this, root, Title);
        RefreshList();
        WinUiUiHost.ConfigureWindow(this, 580, 520, resizable: true,
            minimumWidthDip: 460, minimumHeightDip: 340);
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        if (_directories.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "没有保持更新的目录。",
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        foreach (var directory in _directories.ToArray())
        {
            var row = new Grid { ColumnSpacing = 10, MinHeight = 48 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            details.Children.Add(new TextBlock { Text = directory.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            details.Children.Add(new TextBlock
            {
                Text = directory.Path,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.72,
            });
            var cancel = new Button
            {
                Content = "取消目录自动刷新",
                MinWidth = 142,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cancel.Click += (_, _) =>
            {
                if (!_remove(directory)) return;
                _directories.Remove(directory);
                RefreshList();
            };
            Grid.SetColumn(cancel, 1);
            row.Children.Add(details);
            row.Children.Add(cancel);
            _list.Children.Add(row);
        }
    }
}
