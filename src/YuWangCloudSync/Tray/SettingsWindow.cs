using System.Diagnostics;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.System;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Tray;

/// <summary>WinUI settings window. It edits a snapshot; Program remains responsible for applying it.</summary>
internal sealed class SettingsWindow : Window
{
    private const double RowHeight = 36;
    private const double SettingsTextBoxHeight = 32;
    private const double LabelColumnWidth = 170;
    private const int ApplyFadeMilliseconds = 180;
    private const int MinimumApplyIndicatorMilliseconds = 300;
    private const int CompositionFrameGraceMilliseconds = 40;
    private const int SuccessIndicatorMilliseconds = 1400;

    private enum ApplyVisualState { Normal, Loading, Success }

    private readonly SyncRootContext _source;
    private readonly List<AccountCredential> _accounts = [];
    private readonly ComboBox _accountSelector = new()
    {
        Name = "AccountSelectorComboBox",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MinWidth = 0,
    };
    private readonly TextBox _path = new() { Name = "SyncRootPathTextBox", Height = SettingsTextBoxHeight };
    private readonly TextBox _directoryRefreshInterval = NewMinutesInput(5, "DirectoryRefreshIntervalTextBox");
    private readonly CheckBox _automaticFileCacheCleanup = new()
    {
        Name = "AutomaticFileCacheCleanupCheckBox",
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ComboBox _fileCacheCleanupInterval = new()
    {
        Name = "FileCacheCleanupIntervalComboBox",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 0,
    };
    private readonly CheckBox _persist = new() { Name = "PersistSyncRootCheckBox" };
    private readonly TextBlock _fileCacheCleanupStatus = NewNote("上次清理：无记录");
    private readonly DispatcherTimer _fileCacheCleanupStatusTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private FileCacheCleanupRecord? _lastFileCacheCleanup;
    private readonly CheckBox _enableNavigationEntry = new() { Name = "EnableExplorerNavigationEntryCheckBox" };
    private readonly CheckBox _enableMainWindow = new() { Name = "EnableMainWindowCheckBox" };
    private readonly RadioButton _shortcutOpensMainWindow = new()
    {
        Name = "ShortcutOpensMainWindowRadioButton",
        Content = "主界面",
        GroupName = "ShortcutLaunchTarget",
        MinWidth = 0,
    };
    private readonly RadioButton _shortcutOpensSyncFolder = new()
    {
        Name = "ShortcutOpensSyncFolderRadioButton",
        Content = "同步目录",
        GroupName = "ShortcutLaunchTarget",
        MinWidth = 0,
    };
    private readonly RadioButton _trayClickOpensMainWindow = new()
    {
        Name = "TrayClickOpensMainWindowRadioButton",
        Content = "主界面",
        GroupName = "TrayClickTarget",
        MinWidth = 0,
    };
    private readonly RadioButton _trayClickOpensSyncFolder = new()
    {
        Name = "TrayClickOpensSyncFolderRadioButton",
        Content = "同步目录",
        GroupName = "TrayClickTarget",
        MinWidth = 0,
    };
    private bool _updatingFeatureAvailability;
    private readonly CheckBox _automaticUpdateCheck = new() { Name = "AutomaticUpdateCheckCheckBox" };
    private readonly CheckBox _trayUpdateHint = new() { Name = "TrayUpdateHintCheckBox" };
    private readonly CheckBox _automaticUpdateDownload = new() { Name = "AutomaticUpdateDownloadCheckBox" };
    private readonly TextBox _containerTypeId = new()
    {
        Name = "ContainerTypeIdTextBox",
        Height = SettingsTextBoxHeight,
        MaxLength = 20,
    };
    private readonly TextBox _providerName = new()
    {
        Name = "ProviderNameTextBox",
        Height = SettingsTextBoxHeight,
    };
    private readonly InfoBar _status = new() { Name = "SettingsStatusInfoBar", IsClosable = false };
    private readonly StackPanel _content = new() { Spacing = 10, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ScrollViewer _scroll;
    private readonly Button _okButton;
    private readonly Button _applyButton;
    private readonly Button _cancelButton;
    private readonly TextBlock _applyLabel = new()
    {
        Text = "应用",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };
    private readonly ProgressRing _applyProgress = new()
    {
        Width = 18,
        Height = 18,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0,
        IsHitTestVisible = false,
    };
    private readonly SymbolIcon _applySuccess = new()
    {
        Symbol = Symbol.Accept,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0,
        IsHitTestVisible = false,
    };
    private bool _applied;
    private bool _dirty;
    private bool _isBuildingResult;
    private bool _isApplying;
    private bool _isClosed;
    private bool _applyButtonHeightLocked;
    private long _applyStartedAt;
    private int _applyStateVersion;
    private ApplyVisualState _applyVisualState;

    internal Grid Root { get; }
    internal Task LoadedTask => _loaded.Task;
    internal SyncRootContext? Result { get; private set; }
    internal event Action<SyncRootContext, bool>? ApplyRequested;
    internal event Action<SyncRootContext>? AccountSwitchRequested;
    internal event Action<SettingsWindow>? AccountManagementRequested;
    internal event Action<SettingsWindow>? DirectoryRefreshManagementRequested;
    internal SyncRootContext AccountSource => _source;
    internal bool IsClosed => _isClosed;
    internal string? SelectedAccountName => (_accountSelector.SelectedItem as ComboBoxItem)?.Tag as string;

    internal IReadOnlyList<AccountCredential> GetAccountsSnapshot() => _accounts
        .Select(account => new AccountCredential
        {
            Username = account.Username,
            ProtectedPassword = account.ProtectedPassword,
            DirectoryRefreshIntervalMinutes = account.DirectoryRefreshIntervalMinutes,
        }).ToList();

    internal void UpdateAccountsFromManager(IReadOnlyList<AccountCredential> accounts, string? selectedAccount)
    {
        if (_isClosed) return;

        _accounts.Clear();
        _accounts.AddRange(accounts.Where(account => !string.IsNullOrWhiteSpace(account.Username))
            .Select(account => new AccountCredential
            {
                Username = account.Username,
                ProtectedPassword = account.ProtectedPassword,
                DirectoryRefreshIntervalMinutes = account.DirectoryRefreshIntervalMinutes,
            }));
        _accountSelector.Items.Clear();
        foreach (var account in _accounts)
            _accountSelector.Items.Add(CreateAccountSelectorItem(account.Username));
        _accountSelector.SelectedItem = _accountSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedAccount,
                StringComparison.OrdinalIgnoreCase))
            ?? _accountSelector.Items.FirstOrDefault();
        MarkDirty();
    }

    internal SettingsWindow(SyncRootContext snapshot)
    {
        _source = snapshot;
        _accounts.AddRange((snapshot.Accounts ?? []).Where(account => !string.IsNullOrWhiteSpace(account.Username))
            .Select(account => new AccountCredential
            {
                Username = account.Username,
                ProtectedPassword = account.ProtectedPassword,
                DirectoryRefreshIntervalMinutes = account.DirectoryRefreshIntervalMinutes
            }));
        if (_accounts.Count == 0 && !string.IsNullOrWhiteSpace(snapshot.Username))
            _accounts.Add(new AccountCredential
            {
                Username = snapshot.Username,
                ProtectedPassword = string.IsNullOrWhiteSpace(snapshot.ProtectedPassword)
                    ? DpapiSecret.Protect(snapshot.Password) : snapshot.ProtectedPassword,
                DirectoryRefreshIntervalMinutes = 5
            });
        Title = $"{ProductIdentity.DisplayNameZh} - 设置";

        Root = new Grid { Name = "SettingsRoot", Margin = new Thickness(20, 16, 20, 14), RowSpacing = 12 };
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = _content;
        _scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(_scroll, 0);
        Root.Children.Add(_scroll);

        var accountGrid = NewGrid(RowHeight, 0);
        Add(accountGrid, NewLabel("当前账户"), 0, 0);
        Add(accountGrid, AccountInputRow(), 0, 1);
        Add(accountGrid, NewNote("切换账户会清空同步目录。"), 1, 0);
        AddCard(content, "账户", accountGrid);

        var pathGrid = NewGrid(RowHeight, 0);
        Add(pathGrid, NewLabel("目录位置"), 0, 0);
        Add(pathGrid, PathInputRow(), 0, 1);
        Add(pathGrid, NewNote("请选择空目录，更换后会清理旧目录。"), 1, 0, 2);
        AddCard(content, "同步目录", pathGrid);

        var cacheGrid = NewGrid(RowHeight, 0, RowHeight, RowHeight, 0);
        Add(cacheGrid, NewLabel("刷新间隔（分钟）"), 0, 0);
        Add(cacheGrid, MinutesInputRow(_directoryRefreshInterval, 1, 60), 0, 1);
        Add(cacheGrid, NewNote("仅对保持更新的目录生效，其他目录在主界面中刷新。"), 1, 0, 2);
        var manageKeptDirectories = new Button { Name = "ManageKeptDirectoriesButton", Content = "管理", MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right };
        manageKeptDirectories.Click += (_, _) => DirectoryRefreshManagementRequested?.Invoke(this);
        Add(cacheGrid, NewLabel("保持更新的目录"), 2, 0);
        Add(cacheGrid, manageKeptDirectories, 2, 1);
        _automaticFileCacheCleanup.Content = "自动清理文件缓存";
        foreach (var (text, hours) in new[] { ("每 8 小时", 8), ("每 12 小时", 12), ("每天", 24), ("每 7 天", 168) })
            _fileCacheCleanupInterval.Items.Add(new ComboBoxItem { Content = text, Tag = hours });
        Add(cacheGrid, _automaticFileCacheCleanup, 3, 0);
        Add(cacheGrid, _fileCacheCleanupInterval, 3, 1);
        _fileCacheCleanupStatus.Name = "FileCacheCleanupStatusTextBlock";
        Add(cacheGrid, _fileCacheCleanupStatus, 4, 0, 2);
        try { UpdateFileCacheCleanupHistory(FileCacheCleanupHistory.Read(Program.FileCacheCleanupHistoryPath)); }
        catch (Exception error) { Serilog.Log.Warning(error, "无法读取自动清理文件缓存记录"); }
        _fileCacheCleanupStatusTimer.Tick += (_, _) => RefreshFileCacheCleanupStatus();
        AddCard(content, "缓存与刷新", cacheGrid);

        _persist.Content = "退出后保留同步目录";
        var persistGrid = NewGrid(RowHeight);
        AddCheck(persistGrid, _persist, 0);
        AddCard(content, "退出", persistGrid);

        _enableNavigationEntry.Content = "导航栏入口";
        _enableMainWindow.Content = "启用主界面";
        var featuresGrid = NewGrid(RowHeight, RowHeight, 0, RowHeight);
        featuresGrid.RowSpacing = 4;
        _enableNavigationEntry.VerticalAlignment = VerticalAlignment.Center;
        _enableMainWindow.VerticalAlignment = VerticalAlignment.Center;
        Add(featuresGrid, _enableNavigationEntry, 0, 0);
        Add(featuresGrid, _enableMainWindow, 0, 1);
        Add(featuresGrid, NewLabel("快捷方式打开"), 1, 0);
        var shortcutTargets = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        shortcutTargets.Children.Add(_shortcutOpensMainWindow);
        shortcutTargets.Children.Add(_shortcutOpensSyncFolder);
        Add(featuresGrid, shortcutTargets, 1, 1);
        Add(featuresGrid, NewNote("适用于安装时创建的桌面快捷方式。"), 2, 0, 2);
        Add(featuresGrid, NewLabel("单击托盘图标"), 3, 0);
        var trayClickTargets = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        trayClickTargets.Children.Add(_trayClickOpensMainWindow);
        trayClickTargets.Children.Add(_trayClickOpensSyncFolder);
        Add(featuresGrid, trayClickTargets, 3, 1);
        AddCard(content, "功能", featuresGrid);

        var cloudGrid = NewGrid(RowHeight, 0, RowHeight, 0);
        Add(cloudGrid, NewLabel("顶层目录 TypeId"), 0, 0);
        Add(cloudGrid, TypeIdRow(), 0, 1);
        Add(cloudGrid, NewNote("同步目录顶层对应的云端目录。"), 1, 0, 2);
        Add(cloudGrid, NewLabel("显示名称"), 2, 0);
        Add(cloudGrid, Styled(_providerName), 2, 1);
        Add(cloudGrid, NewNote("资源管理器中显示的名称，立即生效。"), 3, 0, 2);
        AddCard(content, "云盘参数", cloudGrid);

        var serverGrid = NewGrid(RowHeight, 0);
        Add(serverGrid, NewLabel("服务器地址"), 0, 0);
        Add(serverGrid, DisabledField(snapshot.Server), 0, 1);
        Add(serverGrid, ConfigurationFileNote(), 1, 0, 2);
        AddCard(content, "服务器", serverGrid);

        _automaticUpdateCheck.Content = "自动检查更新";
        _trayUpdateHint.Content = "托盘图标更新提示";
        _automaticUpdateDownload.Content = "自动下载更新";
        var updateGrid = NewGrid(RowHeight, 0, RowHeight, 0, RowHeight, 0);
        AddCheck(updateGrid, _automaticUpdateCheck, 0);
        Add(updateGrid, NewNote("每天检查一次，有更新时在托盘菜单中提示。"), 1, 0, 2);
        AddCheck(updateGrid, _trayUpdateHint, 2);
        Add(updateGrid, NewNote("有更新时在托盘图标上显示标记。"), 3, 0, 2);
        AddCheck(updateGrid, _automaticUpdateDownload, 4);
        Add(updateGrid, NewNote("下载后需手动重启完成更新。"), 5, 0, 2);
        AddCard(content, "更新", updateGrid);

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(_status, 1);
        Root.Children.Add(_status);
        _okButton = NewButton("确定", 1, "SettingsOKButton");
        _applyButton = NewButton("应用", 2, "SettingsApplyButton");
        _cancelButton = NewButton("取消", 3, "SettingsCancelButton");
        AutomationProperties.SetName(_applyButton, "应用");
        var applyContent = new Grid();
        applyContent.Children.Add(_applyLabel);
        applyContent.Children.Add(_applyProgress);
        applyContent.Children.Add(_applySuccess);
        _applyButton.Content = applyContent;
        _okButton.SizeChanged += (_, args) => MatchApplyButtonHeight(args.NewSize.Height);
        _applyButton.SizeChanged += (_, _) => MatchApplyButtonHeight(_okButton.ActualHeight);
        _okButton.Click += async (_, _) => await CommitAsync(close: true);
        _applyButton.Click += async (_, _) => await CommitAsync(close: false);
        _cancelButton.Click += (_, _) => Close();
        footer.Children.Add(_okButton);
        footer.Children.Add(_applyButton);
        footer.Children.Add(_cancelButton);
        Grid.SetRow(footer, 2);
        Root.Children.Add(footer);

        WinUiWindowChrome.Attach(this, Root, Title);
        _path.Text = snapshot.SyncRootPath;
        foreach (var account in _accounts)
            _accountSelector.Items.Add(CreateAccountSelectorItem(account.Username));
        var activeAccount = string.IsNullOrWhiteSpace(snapshot.ActiveAccount)
            ? snapshot.Username : snapshot.ActiveAccount;
        _accountSelector.SelectedItem = _accountSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, activeAccount,
                StringComparison.OrdinalIgnoreCase))
            ?? _accountSelector.Items.FirstOrDefault();
        UpdateDirectoryRefreshIntervalText();
        _automaticFileCacheCleanup.IsChecked = snapshot.AutomaticFileCacheCleanup;
        _fileCacheCleanupInterval.SelectedItem = _fileCacheCleanupInterval.Items.OfType<ComboBoxItem>()
            .First(item => (int)item.Tag == SyncRootContext.NormalizeFileCacheCleanupHours(snapshot.FileCacheCleanupHours));
        _fileCacheCleanupInterval.IsEnabled = snapshot.AutomaticFileCacheCleanup;
        _containerTypeId.Text = snapshot.RootContainerTypeId.ToString();
        _providerName.Text = snapshot.ProviderName;
        _persist.IsChecked = snapshot.PersistSyncRoot;
        _enableNavigationEntry.IsChecked = snapshot.EnableExplorerNavigationEntry;
        _enableMainWindow.IsChecked = snapshot.EnableMainWindow;
        if (snapshot.ShouldShortcutOpenMainWindow()) _shortcutOpensMainWindow.IsChecked = true;
        else _shortcutOpensSyncFolder.IsChecked = true;
        if (snapshot.ShouldTrayClickOpenMainWindow()) _trayClickOpensMainWindow.IsChecked = true;
        else _trayClickOpensSyncFolder.IsChecked = true;
        UpdateFeatureAvailability();
        _automaticUpdateCheck.IsChecked = snapshot.AutomaticUpdateCheck;
        _trayUpdateHint.IsChecked = snapshot.TrayUpdateHintEnabled;
        _automaticUpdateDownload.IsChecked = snapshot.AutomaticUpdateDownload;
        WireDirty();

        Root.Loaded += (_, _) =>
        {
            RefreshFileCacheCleanupStatus();
            _fileCacheCleanupStatusTimer.Start();
            _loaded.TrySetResult(true);
        };
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Closed += (_, _) =>
        {
            _isClosed = true;
            _fileCacheCleanupStatusTimer.Stop();
            _loaded.TrySetResult(false);
        };

        WinUiUiHost.ConfigureWindow(this, 550, 750, resizable: true,
            minimumWidthDip: 550, minimumHeightDip: 400);
    }

    private static TextBox NewMinutesInput(int value, string name) => new()
    {
        Name = name,
        Text = value.ToString(),
        Width = 76,
        TextAlignment = TextAlignment.Center,
    };

    private static StackPanel MinutesInputRow(TextBox input, int minimum = 1, int maximum = 120)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var decrement = new Button { Content = "−", MinWidth = 32 };
        var increment = new Button { Content = "+", MinWidth = 32 };
        decrement.Click += (_, _) => ChangeMinutes(input, -1, minimum, maximum);
        increment.Click += (_, _) => ChangeMinutes(input, 1, minimum, maximum);
        row.Children.Add(decrement);
        row.Children.Add(input);
        row.Children.Add(increment);
        return row;
    }

    private static void ChangeMinutes(TextBox input, int delta, int minimum = 1, int maximum = 120)
    {
        var current = int.TryParse(input.Text, out var value) ? value : 1;
        input.Text = Math.Clamp(current + delta, minimum, maximum).ToString();
    }

    private static Grid NewGrid(params double[] rowHeights)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var height in rowHeights)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = Math.Max(0, height) });
        return grid;
    }

    private static void Add(Grid grid, FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        grid.Children.Add(element);
    }

    private static Border NewCard(string title, UIElement content)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Style = WinUiUiHost.GetStyleResource("BodyStrongTextBlockStyle") });
        stack.Children.Add(content);
        return new Border
        {
            Style = WinUiUiHost.GetStyleResource("YuWangCardStyle"),
            Child = stack,
        };
    }

    private void AddCard(Panel panel, string title, UIElement content)
    {
        var card = NewCard(title, content);
        panel.Children.Add(card);
    }

    private static TextBlock NewLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0),
    };

    private static TextBlock NewNote(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = WinUiUiHost.GetStyleResource("YuWangCaptionTextBlockStyle"),
        Margin = new Thickness(0, 2, 0, 6),
    };

    private static T Styled<T>(T control) where T : Control
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        return control;
    }

    private static Button NewButton(string text, int column, string name)
    {
        var button = new Button { Name = name, Content = text, Width = 88 };
        Grid.SetColumn(button, column);
        return button;
    }

    private static void AddCheck(Grid grid, CheckBox checkBox, int row)
    {
        checkBox.VerticalAlignment = VerticalAlignment.Center;
        Add(grid, checkBox, row, 0, 2);
    }

    private static TextBox ReadOnlyField(string value) => new()
    {
        Text = value,
        IsReadOnly = true,
        Width = 300,
    };

    private static TextBox DisabledField(string value) => new()
    {
        Text = value,
        IsEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Height = SettingsTextBoxHeight,
        Name = "ServerAddressTextBox",
    };

    private Grid AccountInputRow()
    {
        var row = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var manage = new Button { Name = "ManageAccountsButton", Content = "管理账户", MinWidth = 100 };
        manage.Click += (_, _) => AccountManagementRequested?.Invoke(this);
        Grid.SetColumn(_accountSelector, 0);
        Grid.SetColumn(manage, 1);
        row.Children.Add(_accountSelector);
        row.Children.Add(manage);
        return row;
    }

    private TextBlock ConfigurationFileNote()
    {
        var note = NewNote("");
        note.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "如需修改，请编辑" });
        var link = new Microsoft.UI.Xaml.Documents.Hyperlink();
        link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "配置文件" });
        link.Click += async (_, _) => await OpenConfigurationFileAsync();
        note.Inlines.Add(link);
        note.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "后重启应用。" });
        return note;
    }

    private async Task OpenConfigurationFileAsync()
    {
        try
        {
            var configPath = AppPaths.ConfigPath;
            var start = new ProcessStartInfo("explorer.exe", $"/select,\"{configPath}\"")
            {
                UseShellExecute = true,
            };
            Process.Start(start);
        }
        catch (Exception error)
        {
            await ShowMessageAsync("配置文件", $"无法定位配置文件：{error.Message}");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "是否关闭应用？",
            Content = "编辑配置文件需先关闭应用，未保存的设置将丢失。",
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        dialog.Style = WinUiUiHost.GetStyleResource("DefaultContentDialogStyle");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Close();
            Program.RequestShutdownFromUi();
        }
    }

    private static ComboBoxItem CreateAccountSelectorItem(string username) => new()
    {
        Content = AccountNameDisplay.ForDisplay(username),
        Tag = username,
    };

    private Grid PathInputRow()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _path.PlaceholderText = "选择同步目录";
        var browse = new Button { Name = "BrowseSyncRootButton", Content = "浏览", Width = 88 };
        browse.Click += OnBrowse;
        grid.Children.Add(_path);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    private StackPanel TypeIdRow()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _containerTypeId.Width = 120;
        var detail = new Button { Content = "详情", Width = 74 };
        detail.Click += async (_, _) => await ShowTypeIdDetailAsync();
        panel.Children.Add(_containerTypeId);
        panel.Children.Add(detail);
        return panel;
    }

    private void MatchApplyButtonHeight(double height)
    {
        if (_applyButtonHeightLocked || height <= 0) return;
        _applyButton.Height = height;
        _applyButtonHeightLocked = true;
    }

    private async Task ShowTypeIdDetailAsync()
    {
        await ShowMessageAsync("TypeId 说明",
            "决定同步目录顶层对应的云端目录，默认 0 表示根目录。\n"
            + "获取方法：在网页端按 F12 打开开发者工具，切换到“网络”，打开目标目录，在 fmsDiskInfoIndexAction.do 请求地址中找到 TypeId。\n"
            + "修改后会重建同步目录并清理缓存。填写无效值会导致目录为空。");
    }

    private async void OnBrowse(object sender, RoutedEventArgs args)
    {
        var owner = WindowNative.GetWindowHandle(this);
        var picked = await FolderPicker.PickAsync(owner, "选择同步目录",
            !string.IsNullOrWhiteSpace(_path.Text) && Directory.Exists(_path.Text) ? _path.Text : null);
        if (string.IsNullOrWhiteSpace(picked)) return;
        _path.Text = picked;
        await ShowProbeResultAsync(picked);
    }

    private async Task ShowProbeResultAsync(string path)
    {
        if (IsCurrentSyncRoot(path)) return;
        if (SyncRootProbe.IsDriveRoot(path))
        {
            SetStatus(InfoBarSeverity.Informational, "");
            return;
        }

        SyncRootProbeResult probe;
        try { probe = SyncRootProbe.Probe(path, _source.SyncRootPath, _source.ProviderName); }
        catch (Exception error)
        {
            SetStatus(InfoBarSeverity.Informational, $"无法检测该位置：{error.Message}");
            return;
        }

        if (probe.Ok)
        {
            var message = "位置可用：" + probe.Describe();
            if (probe.Warnings.Count > 0)
                SetStatus(InfoBarSeverity.Warning, message + "；注意：" + string.Join("；", probe.Warnings));
            else SetStatus(InfoBarSeverity.Informational, message);
        }
        else
        {
            SetStatus(InfoBarSeverity.Error, "位置不可用：" + string.Join("；", probe.Failures));
        }
        await Task.CompletedTask;
    }

    private bool IsCurrentSyncRoot(string path)
    {
        try
        {
            var current = Path.GetFullPath(_source.SyncRootPath ?? "");
            var selected = Path.GetFullPath(path);
            return string.Equals(current.TrimEnd('\\'), selected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool ProbeOk(string path, out string? error)
    {
        error = null;
        try
        {
            var result = SyncRootProbe.Probe(path, _source.SyncRootPath, _source.ProviderName);
            if (result.Ok) return true;
            error = "所选位置不可用：\n" + string.Join("\n", result.Failures);
            return false;
        }
        catch (Exception exception)
        {
            SetStatus(InfoBarSeverity.Informational, $"无法检测该位置：{exception.Message}");
            return true;
        }
    }

    private async Task<(string? Path, string? Error)> TryResolvePathAsync()
    {
        var raw = (_path.Text ?? "").Trim();
        if (raw.Length == 0) return (null, "请选择同步目录。");

        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception exception) { return (null, $"路径不合法：{exception.Message}"); }
        if (!Path.IsPathRooted(full) || full.StartsWith("\\\\", StringComparison.Ordinal))
            return (null, "同步目录必须位于本地磁盘。");

        string root = "";
        try { root = Path.GetFullPath(_source.SyncRootPath); } catch { }
        try
        {
            var currentRoot = root.TrimEnd('\\') + "\\";
            var newRoot = full.TrimEnd('\\') + "\\";
            if (currentRoot.Length > 1 && newRoot.Length > currentRoot.Length
                && newRoot.StartsWith(currentRoot, StringComparison.OrdinalIgnoreCase))
                return (null, "不能选择当前同步目录内的位置。");
        }
        catch { }

        if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return (full, null);

        if (!Directory.Exists(full))
        {
            try { Directory.CreateDirectory(full); }
            catch (Exception exception) { return (null, $"无法创建目录：\n{full}\n{exception.Message}"); }
        }
        try { _ = Directory.EnumerateFileSystemEntries(full).FirstOrDefault(); }
        catch (Exception exception) { return (null, $"目录不可访问：\n{full}\n{exception.Message}"); }

        if (SyncRootProbe.InSensitiveRoot(full))
            return (null, SyncRootProbe.DescribeSensitiveRoot(full));

        if (!SyncRootProbe.HasAnyEntry(full))
        {
            if (!ProbeOk(full, out var probeError)) return (null, probeError);
            return (full, null);
        }

        var message = (SyncRootProbe.IsDriveRoot(full) ? "不能直接使用磁盘根目录。\n" : "")
            + $"所选目录不为空：{full}\n是否在其中新建子目录“YuWang”并使用？";
        if (!await AskYesNoAsync("目录不为空", message)) return (null, null);

        var child = Path.Combine(full, "YuWang");
        if (Directory.Exists(child))
        {
            try { _ = Directory.EnumerateFileSystemEntries(child).FirstOrDefault(); }
            catch (Exception exception) { return (null, $"子目录不可访问：\n{child}\n{exception.Message}"); }
            if (SyncRootProbe.HasAnyEntry(child))
                return (null, $"子目录已存在且不为空：\n{child}\n请清空后重试。");
        }
        else
        {
            try { Directory.CreateDirectory(child); }
            catch (Exception exception) { return (null, $"无法创建子目录：\n{child}\n{exception.Message}"); }
        }
        if (!ProbeOk(child, out var childProbeError)) return (null, childProbeError);
        return (child, null);
    }

    private async Task<string?> BuildResultAsync()
    {
        Result = null;
        var (path, pathError) = await TryResolvePathAsync();
        if (pathError is not null) return pathError;
        if (path is null) return null;

        var validationError = ValidateEditableValues(out var refreshMinutes, out var containerTypeId);
        if (validationError is not null) return validationError;
        if (_enableNavigationEntry.IsChecked != true && _enableMainWindow.IsChecked != true)
            return "导航栏入口和主界面至少启用一项。";

        var providerName = _providerName.Text.Trim();
        var selectedAccount = (_accountSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(selectedAccount))
            return "请先选择账户，或通过“管理账户”添加。";
        var selectedCredential = _accounts.FirstOrDefault(account =>
            account.Username.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase));
        if (selectedCredential is null) return "找不到账户密码，请重新添加账户。";
        selectedCredential.DirectoryRefreshIntervalMinutes = refreshMinutes;
        string password;
        try
        {
            password = DpapiSecret.Unprotect(selectedCredential.ProtectedPassword);
        }
        catch (Exception error)
        {
            return $"无法解密账户密码：{error.Message}";
        }
        Result = new SyncRootContext
        {
            SyncRootPath = path,
            Server = _source.Server,
            Username = selectedCredential.Username,
            ActiveAccount = selectedCredential.Username,
            Accounts = _accounts.Select(account => new AccountCredential
            {
                Username = account.Username,
                ProtectedPassword = account.ProtectedPassword,
                DirectoryRefreshIntervalMinutes = account.DirectoryRefreshIntervalMinutes,
            }).ToList(),
            Password = password,
            RootDiskId = _source.RootDiskId,
            RootFolderId = _source.RootFolderId,
            RootContainerTypeId = containerTypeId,
            UserKey = _source.UserKey,
            ProviderName = providerName.Length == 0 ? _source.ProviderName : providerName,
            DatabasePath = _source.DatabasePath,
            LogPath = _source.LogPath,
            LogsToKeep = _source.LogsToKeep,
            SessionCheckIntervalMinutes = _source.SessionCheckIntervalMinutes,
            HttpRequestTimeoutSeconds = _source.HttpRequestTimeoutSeconds,
            DownloadTimeoutMinutes = _source.DownloadTimeoutMinutes,
            LegacySyncRootPaths = _source.LegacySyncRootPaths,
            AutomaticFileCacheCleanup = _automaticFileCacheCleanup.IsChecked == true,
            FileCacheCleanupHours = (_fileCacheCleanupInterval.SelectedItem as ComboBoxItem)?.Tag as int? ?? 24,
            PersistSyncRoot = _persist.IsChecked == true,
            EnableExplorerNavigationEntry = _enableNavigationEntry.IsChecked == true,
            EnableMainWindow = _enableMainWindow.IsChecked == true,
            // “主界面”仅在启用主界面时有效；禁用主界面时自动回退到同步目录。
            ShortcutOpensMainWindow = _enableMainWindow.IsChecked == true && _shortcutOpensMainWindow.IsChecked == true,
            TrayClickOpensMainWindow = _enableMainWindow.IsChecked == true && _trayClickOpensMainWindow.IsChecked == true,
            AutomaticUpdateCheck = _automaticUpdateCheck.IsChecked == true,
            TrayUpdateHintEnabled = _trayUpdateHint.IsChecked == true,
            AutomaticUpdateDownload = _automaticUpdateDownload.IsChecked == true,
        };
        return null;
    }

    private string? ValidateEditableValues(out int refreshMinutes, out ulong containerTypeId)
    {
        refreshMinutes = int.TryParse(_directoryRefreshInterval.Text, out var refreshValue) ? refreshValue : 0;
        containerTypeId = 0;
        if (refreshMinutes is < 1 or > 60)
            return "刷新间隔须为 1—60 分钟。";
        if (!ulong.TryParse(_containerTypeId.Text.Trim(), out containerTypeId))
            return "TypeId 必须是整数。";
        return null;
    }

    internal bool ValidateForSelfTest()
    {
        var originalTypeId = _containerTypeId.Text;
        var originalRefreshInterval = _directoryRefreshInterval.Text;
        try
        {
            _containerTypeId.Text = "not-an-id";
            if (ValidateEditableValues(out _, out _) != "TypeId 必须是整数。") return false;
            _containerTypeId.Text = "0";
            if (ValidateEditableValues(out _, out var zeroTypeId) is not null || zeroTypeId != 0) return false;
            _containerTypeId.Text = originalTypeId;
            _directoryRefreshInterval.Text = "0";
            return ValidateEditableValues(out _, out _) == "刷新间隔须为 1—60 分钟。";
        }
        finally
        {
            _containerTypeId.Text = originalTypeId;
            _directoryRefreshInterval.Text = originalRefreshInterval;
        }
    }

    internal bool ValidateAutomationOptionsForSelfTest()
    {
        var original = _automaticFileCacheCleanup.IsChecked;
        try
        {
            if (!_fileCacheCleanupInterval.Items.OfType<ComboBoxItem>().Select(item => (int)item.Tag)
                    .SequenceEqual(new[] { 8, 12, 24, 168 })) return false;
            if (_automaticUpdateDownload.Content as string != "自动下载更新") return false;
            _automaticFileCacheCleanup.IsChecked = false;
            if (_fileCacheCleanupInterval.IsEnabled) return false;
            _automaticFileCacheCleanup.IsChecked = true;
            return _fileCacheCleanupInterval.IsEnabled;
        }
        finally { _automaticFileCacheCleanup.IsChecked = original; }
    }

    private async Task CommitAsync(bool close)
    {
        if (_isBuildingResult || _isApplying) return;

        SetStatus(InfoBarSeverity.Informational, "");
        _isBuildingResult = true;
        _applyStartedAt = Stopwatch.GetTimestamp();
        SetApplyVisualState(ApplyVisualState.Loading);
        _applyButton.IsEnabled = true;
        await Task.Delay(TimeSpan.FromMilliseconds(CompositionFrameGraceMilliseconds));
        if (_isClosed)
        {
            _isBuildingResult = false;
            return;
        }

        string? error;
        try { error = await BuildResultAsync(); }
        catch (Exception preparationError)
        {
            Serilog.Log.Error(preparationError, "准备设置结果失败");
            error = $"无法保存设置：{preparationError.Message}";
        }
        finally { _isBuildingResult = false; }

        if (_isClosed) return;

        SetStatus(InfoBarSeverity.Informational, "");
        if (error is not null)
        {
            SetApplyVisualState(ApplyVisualState.Normal);
            await ShowMessageAsync("设置未完成", error);
            return;
        }
        var result = Result;
        if (result is null)
        {
            SetApplyVisualState(ApplyVisualState.Normal);
            return;
        }
        if (!result.Username.Equals(_source.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (!await AskYesNoAsync("切换账户",
                    "切换账户会清空同步目录，是否继续？"))
            {
                SetApplyVisualState(ApplyVisualState.Normal);
                return;
            }
            var switchRequested = AccountSwitchRequested;
            if (switchRequested is null)
            {
                SetApplyVisualState(ApplyVisualState.Normal);
                await ShowMessageAsync("切换账户失败", "暂时无法切换账户，请稍后重试。");
                return;
            }
            try
            {
                _isApplying = true;
                _scroll.IsEnabled = false;
                _okButton.IsEnabled = false;
                _applyButton.IsEnabled = false;
                _cancelButton.IsEnabled = false;
                switchRequested(result);
                Close();
            }
            catch (Exception switchError)
            {
                _isApplying = false;
                _scroll.IsEnabled = true;
                _okButton.IsEnabled = true;
                _applyButton.IsEnabled = true;
                _cancelButton.IsEnabled = true;
                SetApplyVisualState(ApplyVisualState.Normal);
                await ShowMessageAsync("切换账户失败", switchError.Message);
            }
            return;
        }
        if (_applied && !_dirty)
        {
            if (close) Close();
            else SetApplyVisualState(ApplyVisualState.Normal);
            return;
        }

        var applyRequested = ApplyRequested;
        if (applyRequested is null)
        {
            SetApplyVisualState(ApplyVisualState.Normal);
            await ShowMessageAsync("设置未完成", "暂时无法应用设置，请稍后重试。");
            return;
        }

        _isApplying = true;
        _scroll.IsEnabled = false;
        _okButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        _applyStartedAt = Stopwatch.GetTimestamp();
        try { applyRequested(result, close); }
        catch (Exception applyStartError)
        {
            Serilog.Log.Error(applyStartError, "启动设置应用流程失败");
            await CompleteApplyAsync(false, applyStartError.Message, close);
        }
    }

    private void SetStatus(InfoBarSeverity severity, string message)
    {
        if (_isBuildingResult || _isApplying)
        {
            _status.IsOpen = false;
            return;
        }
        _status.Severity = severity;
        _status.Message = message;
        _status.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    internal async Task CompleteApplyAsync(bool ok, string message, bool closeOnSuccess)
    {
        var elapsed = Stopwatch.GetElapsedTime(_applyStartedAt);
        // Keep the ring fully visible for at least 300 ms after its 180 ms fade-in.
        var minimumVisibleTime = TimeSpan.FromMilliseconds(
            MinimumApplyIndicatorMilliseconds + ApplyFadeMilliseconds + CompositionFrameGraceMilliseconds);
        var remaining = minimumVisibleTime - elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining).ConfigureAwait(false);

        if (!WinUiUiHost.Post(() => _ = CompleteApplyOnUiAsync(ok, message, closeOnSuccess)))
            Serilog.Log.Warning("WinUI UI 线程不可用，无法显示设置应用结果");
    }

    private async Task CompleteApplyOnUiAsync(bool ok, string message, bool closeOnSuccess)
    {
        if (!_isApplying || _isClosed) return;

        if (ok)
        {
            _applied = true;
            _dirty = false;
            _isApplying = false;
            _scroll.IsEnabled = true;
            _okButton.IsEnabled = true;
            _cancelButton.IsEnabled = true;
            if (closeOnSuccess)
            {
                Close();
                return;
            }

            SetApplyVisualState(ApplyVisualState.Success);
            _applyButton.IsEnabled = true;
            _ = RestoreApplyButtonAfterDelayAsync(_applyStateVersion);
            return;
        }

        try
        {
            await ShowMessageAsync("无法应用设置", message);
        }
        finally
        {
            _isApplying = false;
            _scroll.IsEnabled = true;
            _okButton.IsEnabled = true;
            _cancelButton.IsEnabled = true;
            SetApplyVisualState(ApplyVisualState.Normal);
            _applyButton.IsEnabled = true;
        }
    }

    private void SetApplyVisualState(ApplyVisualState state)
    {
        if (_applyVisualState == state) return;
        _applyVisualState = state;
        _applyStateVersion++;
        AutomationProperties.SetName(_applyButton, state switch
        {
            ApplyVisualState.Loading => "正在应用设置",
            ApplyVisualState.Success => "设置已应用",
            _ => "应用",
        });
        _applyProgress.IsActive = state == ApplyVisualState.Loading;
        AnimateOpacity(_applyLabel, state == ApplyVisualState.Normal ? 1 : 0);
        AnimateOpacity(_applyProgress, state == ApplyVisualState.Loading ? 1 : 0);
        AnimateOpacity(_applySuccess, state == ApplyVisualState.Success ? 1 : 0);
    }

    private static void AnimateOpacity(UIElement element, float targetOpacity)
    {
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, targetOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(ApplyFadeMilliseconds);
        animation.Target = "Opacity";
        element.StartAnimation(animation);
    }

    private async Task RestoreApplyButtonAfterDelayAsync(int stateVersion)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(SuccessIndicatorMilliseconds)).ConfigureAwait(false);
        WinUiUiHost.Post(() =>
        {
            if (!_isClosed && !_isApplying && _applyStateVersion == stateVersion
                && _applyVisualState == ApplyVisualState.Success)
                SetApplyVisualState(ApplyVisualState.Normal);
        });
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        dialog.Style = WinUiUiHost.GetStyleResource("DefaultContentDialogStyle");
        await dialog.ShowAsync();
    }

    private async Task<bool> AskYesNoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "是",
            CloseButtonText = "否",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        dialog.Style = WinUiUiHost.GetStyleResource("DefaultContentDialogStyle");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_isApplying)
        {
            args.Handled = true;
            return;
        }

        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            Close();
        }
        else if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await CommitAsync(close: true);
        }
    }

    /// <summary>最后一个入口保持启用并锁定；启动目标随主界面可用状态回退。</summary>
    private void UpdateFeatureAvailability()
    {
        if (_updatingFeatureAvailability) return;
        _updatingFeatureAvailability = true;
        try
        {
            if (_enableNavigationEntry.IsChecked != true && _enableMainWindow.IsChecked != true)
                _enableNavigationEntry.IsChecked = true;
            var mainWindowEnabled = _enableMainWindow.IsChecked == true;
            _enableNavigationEntry.IsEnabled = mainWindowEnabled;
            _enableMainWindow.IsEnabled = _enableNavigationEntry.IsChecked == true;
            _shortcutOpensMainWindow.IsEnabled = mainWindowEnabled;
            _trayClickOpensMainWindow.IsEnabled = mainWindowEnabled;
            if (!mainWindowEnabled)
            {
                _shortcutOpensSyncFolder.IsChecked = true;
                _trayClickOpensSyncFolder.IsChecked = true;
            }
        }
        finally { _updatingFeatureAvailability = false; }
    }

    private void WireDirty()
    {
        _accountSelector.SelectionChanged += (_, _) =>
        {
            UpdateDirectoryRefreshIntervalText();
            MarkDirty();
        };
        _path.TextChanged += (_, _) => MarkDirty();
        _directoryRefreshInterval.TextChanged += (_, _) => MarkDirty();
        _automaticFileCacheCleanup.Checked += (_, _) => UpdateFileCacheCleanupEnabled();
        _automaticFileCacheCleanup.Unchecked += (_, _) => UpdateFileCacheCleanupEnabled();
        _fileCacheCleanupInterval.SelectionChanged += (_, _) => MarkDirty();
        _containerTypeId.TextChanged += (_, _) => MarkDirty();
        _providerName.TextChanged += (_, _) => MarkDirty();
        _persist.Checked += (_, _) => MarkDirty();
        _persist.Unchecked += (_, _) => MarkDirty();
        _enableNavigationEntry.Checked += (_, _) => { UpdateFeatureAvailability(); MarkDirty(); };
        _enableNavigationEntry.Unchecked += (_, _) => { UpdateFeatureAvailability(); MarkDirty(); };
        _enableMainWindow.Checked += (_, _) =>
        {
            UpdateFeatureAvailability();
            MarkDirty();
        };
        _enableMainWindow.Unchecked += (_, _) =>
        {
            UpdateFeatureAvailability();
            MarkDirty();
        };
        _shortcutOpensMainWindow.Checked += (_, _) => MarkDirty();
        _shortcutOpensSyncFolder.Checked += (_, _) => MarkDirty();
        _trayClickOpensMainWindow.Checked += (_, _) => MarkDirty();
        _trayClickOpensSyncFolder.Checked += (_, _) => MarkDirty();
        _automaticUpdateCheck.Checked += (_, _) => MarkDirty();
        _automaticUpdateCheck.Unchecked += (_, _) => MarkDirty();
        _trayUpdateHint.Checked += (_, _) => MarkDirty();
        _trayUpdateHint.Unchecked += (_, _) => MarkDirty();
        _automaticUpdateDownload.Checked += (_, _) => MarkDirty();
        _automaticUpdateDownload.Unchecked += (_, _) => MarkDirty();
    }

    private void UpdateFileCacheCleanupEnabled()
    {
        _fileCacheCleanupInterval.IsEnabled = _automaticFileCacheCleanup.IsChecked == true;
        MarkDirty();
    }

    internal void UpdateFileCacheCleanupHistory(FileCacheCleanupRecord? record)
    {
        if (_isClosed) return;
        _lastFileCacheCleanup = record;
        RefreshFileCacheCleanupStatus();
    }

    private void RefreshFileCacheCleanupStatus()
        => _fileCacheCleanupStatus.Text = FileCacheCleanupHistory.Format(_lastFileCacheCleanup, DateTimeOffset.Now);

    private void MarkDirty()
    {
        _dirty = true;
        if (!_isApplying)
            SetApplyVisualState(ApplyVisualState.Normal);
    }

    private void UpdateDirectoryRefreshIntervalText()
    {
        var selected = (_accountSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        var account = _accounts.FirstOrDefault(item =>
            item.Username.Equals(selected, StringComparison.OrdinalIgnoreCase));
        _directoryRefreshInterval.Text = Math.Clamp(account?.DirectoryRefreshIntervalMinutes ?? 5, 1, 60).ToString();
    }
}
