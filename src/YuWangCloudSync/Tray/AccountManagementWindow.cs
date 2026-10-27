using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;
using YuWangCloudSync.Net;

namespace YuWangCloudSync.Tray;

/// <summary>Manages saved account credentials while the settings window is locked.</summary>
internal sealed class AccountManagementWindow : Window
{
    private const double SettingsTextBoxHeight = 32;

    private readonly SyncRootContext _source;
    private readonly List<AccountCredential> _accounts = [];
    private readonly StackPanel _accountList = new() { Spacing = 8 };
    private readonly TextBlock _singleAccountNote = new()
    {
        Name = "SingleAccountNoteTextBlock",
        Text = "至少保留一个账户。",
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
        Margin = new Thickness(0, 2, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly string _runningAccount;
    private readonly Action<IReadOnlyList<AccountCredential>, string?> _accountsChanged;
    private string? _selectedAccount;

    internal Grid Root { get; }

    internal AccountManagementWindow(SyncRootContext source,
        IReadOnlyList<AccountCredential> accounts,
        string? selectedAccount,
        Action<IReadOnlyList<AccountCredential>, string?> accountsChanged)
    {
        _source = source;
        _runningAccount = source.Username;
        _selectedAccount = selectedAccount;
        _accountsChanged = accountsChanged;
        _accounts.AddRange(accounts.Select(CloneAccount));

        Title = $"{ProductIdentity.DisplayNameZh} - 管理账户";
        Root = new Grid { Name = "AccountManagementRoot", Margin = new Thickness(20, 16, 20, 14), RowSpacing = 12 };
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            Content = _accountList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Root.Children.Add(scroll);
        Grid.SetRow(_singleAccountNote, 1);
        Root.Children.Add(_singleAccountNote);

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add = new Button { Name = "AddAccountButton", Content = "添加账户", MinWidth = 100 };
        add.Click += async (_, _) => await AddAccountAsync();
        var close = new Button { Name = "AccountManagementCloseButton", Content = "完成", MinWidth = 88 };
        close.Click += (_, _) => Close();
        Grid.SetColumn(add, 1);
        Grid.SetColumn(close, 2);
        footer.Children.Add(add);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        Root.Children.Add(footer);

        WinUiWindowChrome.Attach(this, Root, Title);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        RefreshAccountList();
        WinUiUiHost.ConfigureWindow(this, 500, 500, resizable: true,
            minimumWidthDip: 440, minimumHeightDip: 360);
    }

    private void RefreshAccountList()
    {
        _singleAccountNote.Visibility = _accounts.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        _accountList.Children.Clear();
        foreach (var account in _accounts)
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 48 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock
            {
                Text = AccountNameDisplay.ForDisplay(account.Username),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var labels = new List<string>();
            if (account.Username.Equals(_runningAccount, StringComparison.OrdinalIgnoreCase))
                labels.Add("当前运行");
            if (account.Username.Equals(_selectedAccount, StringComparison.OrdinalIgnoreCase))
                labels.Add("设置中选中");
            if (labels.Count > 0)
                details.Children.Add(new TextBlock { Text = string.Join(" · ", labels), Opacity = 0.72 });

            var remove = new Button
            {
                Name = "DeleteAccountButton",
                Content = "删除",
                MinWidth = 72,
                IsEnabled = _accounts.Count > 1,
            };
            AutomationProperties.SetName(remove, $"删除账户 {AccountNameDisplay.ForDisplay(account.Username)}");
            remove.Click += async (_, _) => await DeleteAccountAsync(account);
            Grid.SetColumn(remove, 1);
            row.Children.Add(details);
            row.Children.Add(remove);
            _accountList.Children.Add(row);
        }
    }

    private async Task DeleteAccountAsync(AccountCredential account)
    {
        if (_accounts.Count <= 1 || !_accounts.Contains(account)) return;

        var displayName = AccountNameDisplay.ForDisplay(account.Username);
        var dialog = new ContentDialog
        {
            Title = "删除账户",
            Content = $"确定从设置账户列表中删除“{displayName}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _accounts.Remove(account);
        if (string.Equals(_selectedAccount, account.Username, StringComparison.OrdinalIgnoreCase))
            _selectedAccount = _accounts[0].Username;
        NotifyAccountsChanged();
        RefreshAccountList();
    }

    private async Task AddAccountAsync()
    {
        var username = new TextBox
        {
            Name = "AddAccountUsernameTextBox",
            Header = "账号",
            PlaceholderText = "输入登录名",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
        };
        var password = new PasswordBox
        {
            Name = "AddAccountPasswordBox",
            Header = "密码",
            PlaceholderText = "输入密码",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
        };
        var captchaCode = new TextBox
        {
            Name = "AddAccountCaptchaTextBox",
            PlaceholderText = "输入验证码",
            Height = SettingsTextBoxHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
        };
        var captchaImage = new Image
        {
            Name = "AddAccountCaptchaImage",
            Height = SettingsTextBoxHeight,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var refreshCaptcha = new Button
        {
            Name = "RefreshAddAccountCaptchaButton",
            Content = captchaImage,
            Height = SettingsTextBoxHeight,
            MinWidth = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(refreshCaptcha, "点击刷新验证码");
        ToolTipService.SetToolTip(refreshCaptcha, "点击刷新验证码");

        var captchaRow = new Grid
        {
            Name = "AddAccountCaptchaRow",
            ColumnSpacing = 8,
            Height = SettingsTextBoxHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        captchaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        captchaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(refreshCaptcha, 1);
        captchaRow.Children.Add(captchaCode);
        captchaRow.Children.Add(refreshCaptcha);

        var captchaLabel = new TextBlock { Text = "验证码", Margin = new Thickness(0, 0, 0, 4) };
        var status = new TextBlock
        {
            Name = "AddAccountStatusTextBlock",
            TextWrapping = TextWrapping.Wrap,
        };
        var form = new StackPanel { Spacing = 10, MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(username);
        form.Children.Add(password);
        form.Children.Add(captchaLabel);
        form.Children.Add(captchaRow);
        form.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "添加账户",
            Content = form,
            PrimaryButtonText = "登录并保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            IsPrimaryButtonEnabled = false,
        };
        var dialogClosed = false;
        var isRefreshingCaptcha = false;
        var isAuthenticating = false;
        using var loginLifetime = new CancellationTokenSource();
        using var client = new YuWangClient(_source.Server, "", "",
            message => Serilog.Log.Debug("{Message}", message),
            _source.HttpRequestTimeoutSeconds, _source.DownloadTimeoutMinutes, loginLifetime.Token);

        dialog.Closed += (_, _) =>
        {
            dialogClosed = true;
            loginLifetime.Cancel();
        };
        refreshCaptcha.Click += async (_, _) => await RefreshCaptchaAsync();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                if (dialogClosed || isAuthenticating) return;

                var name = username.Text.Trim();
                var plainPassword = password.Password;
                var code = captchaCode.Text.Trim();
                if (name.Length == 0 || plainPassword.Length == 0 || code.Length == 0)
                {
                    status.Text = "账号、密码和验证码不能为空。";
                    return;
                }
                if (captchaImage.Source is null)
                {
                    status.Text = "请先获取验证码。";
                    return;
                }

                isAuthenticating = true;
                SetLoginControlsEnabled(false);
                status.Text = "正在验证账号…";
                client.ConfigureCredentials(name, plainPassword);

                if (!await client.SubmitLoginCaptchaAsync(code))
                {
                    if (dialogClosed) return;
                    captchaCode.Text = "";
                    await RefreshCaptchaAsync();
                    if (!dialogClosed) status.Text = "账密或验证码不正确，请重试。";
                    return;
                }
                if (dialogClosed) return;

                var protectedPassword = DpapiSecret.Protect(plainPassword);
                Program.SaveAccountSession(name, client);
                var existing = _accounts.FirstOrDefault(account =>
                    account.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    _accounts.Add(new AccountCredential { Username = name, ProtectedPassword = protectedPassword });
                else
                    existing.ProtectedPassword = protectedPassword;
                _selectedAccount = name;
                NotifyAccountsChanged();
                RefreshAccountList();
                args.Cancel = false;
            }
            catch (OperationCanceledException) when (loginLifetime.IsCancellationRequested)
            {
                // Closing the add-account dialog cancels its pending network request.
            }
            catch (Exception error)
            {
                if (!dialogClosed)
                {
                    var message = $"登录失败：{error.Message}";
                    captchaCode.Text = "";
                    await RefreshCaptchaAsync();
                    if (!dialogClosed) status.Text = message;
                }
            }
            finally
            {
                isAuthenticating = false;
                if (!dialogClosed) SetLoginControlsEnabled(true);
                deferral.Complete();
            }
        };

        try
        {
            dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            var dialogOperation = dialog.ShowAsync();
            await RefreshCaptchaAsync();
            await dialogOperation;
        }
        finally
        {
            loginLifetime.Cancel();
        }

        async Task RefreshCaptchaAsync()
        {
            if (dialogClosed || isRefreshingCaptcha) return;

            isRefreshingCaptcha = true;
            refreshCaptcha.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            captchaCode.Text = "";
            status.Text = "正在获取验证码…";
            try
            {
                var imageBytes = await client.RefreshLoginCaptchaAsync();
                var bitmap = await CreateCaptchaImageAsync(imageBytes);
                if (dialogClosed) return;

                captchaImage.Source = bitmap;
                captchaImage.Width = SettingsTextBoxHeight * bitmap.PixelWidth / bitmap.PixelHeight;
                status.Text = "";
            }
            catch (OperationCanceledException) when (loginLifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!dialogClosed)
                {
                    captchaImage.Source = null;
                    captchaImage.Width = 0;
                    status.Text = $"获取验证码失败：{error.Message}";
                }
            }
            finally
            {
                isRefreshingCaptcha = false;
                if (!dialogClosed)
                {
                    refreshCaptcha.IsEnabled = !isAuthenticating;
                    dialog.IsPrimaryButtonEnabled = captchaImage.Source is not null && !isAuthenticating;
                }
            }
        }

        void SetLoginControlsEnabled(bool enabled)
        {
            username.IsEnabled = enabled;
            password.IsEnabled = enabled;
            captchaCode.IsEnabled = enabled;
            refreshCaptcha.IsEnabled = enabled && !isRefreshingCaptcha;
            dialog.IsPrimaryButtonEnabled = enabled && captchaImage.Source is not null;
        }
    }

    private void NotifyAccountsChanged() => _accountsChanged(
        _accounts.Select(CloneAccount).ToList(), _selectedAccount);

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        Close();
    }

    private static AccountCredential CloneAccount(AccountCredential account) => new()
    {
        Username = account.Username,
        ProtectedPassword = account.ProtectedPassword,
    };

    private static async Task<BitmapImage> CreateCaptchaImageAsync(byte[] imageBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        if (image.PixelWidth == 0 || image.PixelHeight == 0)
            throw new InvalidDataException("验证码图片为空。");
        return image;
    }
}
