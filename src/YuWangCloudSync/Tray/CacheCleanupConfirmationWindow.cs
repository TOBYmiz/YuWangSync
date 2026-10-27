using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Tray;

/// <summary>Confirmation prompt shown before dehydrating cached files.</summary>
internal sealed class CacheCleanupConfirmationWindow : Window
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _confirmed;

    internal Task<bool> Completion => _completion.Task;

    internal CacheCleanupConfirmationWindow()
    {
        Title = $"{ProductIdentity.DisplayNameZh} - 清理";

        var root = new Grid { Margin = new Thickness(24, 18, 24, 18), RowSpacing = 20 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var message = new TextBlock
        {
            Text = "确定清理已缓存文件？",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 16,
        };
        root.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        var confirm = new Button { Content = "确定", Width = 88 };
        var cancel = new Button { Content = "取消", Width = 88 };
        confirm.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        root.Loaded += (_, _) => confirm.Focus(FocusState.Programmatic);
        Closed += (_, _) => _completion.TrySetResult(_confirmed);

        WinUiWindowChrome.Attach(this, root, Title, "\uE8D4");
        WinUiUiHost.ConfigureWindow(this, 430, 190, resizable: false);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            _confirmed = true;
            Close();
        }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            Close();
        }
    }
}
