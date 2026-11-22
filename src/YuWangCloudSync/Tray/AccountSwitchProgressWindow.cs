using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YuWangCloudSync.Tray;

internal sealed class AccountSwitchProgressWindow : Window
{
    private readonly TextBlock _stage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _elapsed = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly Button _close = new() { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource _dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _canClose;

    internal Task Dismissed => _dismissed.Task;

    internal AccountSwitchProgressWindow(string account)
    {
        Title = "切换账户";
        var root = new Grid { Margin = new Thickness(24), RowSpacing = 12 };
        for (var row = 0; row < 6; row++)
            root.RowDefinitions.Add(new RowDefinition { Height = row == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        FrameworkElement[] rows =
        [
            new TextBlock { Text = $"正在切换到 {account}", FontSize = 20, TextWrapping = TextWrapping.Wrap },
            _stage, _progress,
            new ScrollViewer { Content = _detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            _elapsed, _close,
        ];
        for (var row = 0; row < rows.Length; row++)
        {
            Grid.SetRow(rows[row], row);
            root.Children.Add(rows[row]);
        }
        _close.Click += (_, _) => Close();
        _timer.Tick += (_, _) => _elapsed.Text = $"已用时 {Stopwatch.GetElapsedTime(_started):m\\:ss}";
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _dismissed.TrySetResult(); };
        WinUiWindowChrome.Attach(this, root, Title, "\uE8D4");
        WinUiUiHost.ConfigureWindow(this, 520, 350, resizable: false);
        AppWindow.Closing += (_, args) => args.Cancel = !_canClose;
        Update("正在停止旧账户的后台任务…", null, "切换完成后将自动连接新账户。");
    }

    internal void Update(string stage, double? percent, string detail)
    {
        _stage.Text = stage;
        _detail.Text = detail;
        _progress.IsIndeterminate = percent is null;
        if (percent is { } value) _progress.Value = Math.Clamp(value, 0, 100);
    }

    internal void Fail(string message)
    {
        Update("账户切换失败", 0, message);
        _timer.Stop();
        _canClose = true;
        _close.Visibility = Visibility.Visible;
    }

    internal void Finish()
    {
        _canClose = true;
        Close();
    }
}
