using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.Storage.Streams;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.Tray;

/// <summary>On-demand WinUI captcha dialog. Closing, Escape, or Cancel marks the prompt as canceled.</summary>
internal sealed class CaptchaWindow : Window
{
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _root = new() { Name = "CaptchaRoot", Margin = new Thickness(20, 16, 20, 16) };
    private readonly Image _image = new() { Name = "CaptchaImage", Height = 86, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly TextBox _input = new()
    {
        Name = "CaptchaTextBox",
        MaxLength = 8,
        PlaceholderText = "输入验证码",
    };
    private bool _accepted;

    internal string Result { get; private set; } = "";
    internal bool WasCanceled { get; private set; }
    internal Task<bool> LoadedTask => _loaded.Task;
    internal Grid Root => _root;
    internal bool HasImageSource => _image.Source is not null;

    internal CaptchaWindow(string imagePath)
    {
        Title = $"{ProductIdentity.DisplayNameZh} - 验证码";

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["YuWangImageFrameStyle"],
            Child = _image,
        });
        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
        };
        var accept = new Button { Name = "CaptchaAcceptButton", Content = "确定", Width = 92 };
        var cancel = new Button { Name = "CaptchaCancelButton", Content = "取消", Width = 92 };
        accept.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Cancel();
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        _root.Children.Add(panel);
        WinUiWindowChrome.Attach(this, _root, Title, "\uE8D4");
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Closed += (_, _) =>
        {
            if (!_accepted) WasCanceled = true;
        };
        _root.Loaded += async (_, _) =>
        {
            try
            {
                await LoadImageAsync(imagePath);
                _input.Focus(FocusState.Programmatic);
                _loaded.TrySetResult(true);
            }
            catch (Exception error)
            {
                Serilog.Log.Warning(error, "验证码图片加载失败: {Path}", imagePath);
                _loaded.TrySetResult(false);
            }
        };

        // Leave enough client space below the textbox for both buttons on Windows 10.
        WinUiUiHost.ConfigureWindow(this, 390, 310, resizable: false);
    }

    private async Task LoadImageAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        _image.Source = bitmap;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            Accept();
        }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            Cancel();
        }
    }

    private void Accept()
    {
        Result = _input.Text ?? "";
        _accepted = true;
        Close();
    }

    private void Cancel()
    {
        WasCanceled = true;
        Close();
    }

    internal void AcceptForSelfTest(string value)
    {
        _input.Text = value;
        Accept();
    }

    internal void CancelForSelfTest() => Cancel();
}
