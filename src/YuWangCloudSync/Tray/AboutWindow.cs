using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using YuWangCloudSync.Core;
using YuWangCloudSync.Updates;

namespace YuWangCloudSync.Tray;

/// <summary>Application information window opened from the native tray menu.</summary>
internal sealed class AboutWindow : Window
{
    private const int UpdateFadeMilliseconds = 180;
    private const string RepositoryUrl = "https://github.com/TOBYmiz/YuWangSync";

    private static readonly (string Component, string License)[] OpenSourceLicenses =
    [
        ("Microsoft.WindowsAppSDK 及其子包", "随包提供 LICENSE / NOTICE"),
        ("Vanara", "MIT"),
        ("Microsoft.Data.Sqlite", "MIT"),
        ("SQLitePCLRaw", "Apache-2.0；SQLite 原生代码处于公共领域"),
        ("Serilog 及日志接收器", "Apache-2.0"),
        ("H.NotifyIcon 及图标依赖", "MIT"),
        (".NET 支持库", "MIT"),
        ("Material UI / Material Icons", "MIT / Apache-2.0"),
    ];

    private readonly Grid _root;
    private readonly Button _updateButton;
    private readonly TextBlock _updateLabel;
    private UpdateActivity _dialogDisplayedActivity = UpdateActivity.Idle;
    private ContentDialog? _releaseDialog;
    private Button? _releaseDialogUpdateButton;
    private UpdateButtonVisual? _releaseDialogUpdateVisual;
    private TextBlock? _releaseDialogStatus;
    private string? _promptedReleaseTag;
    private bool _isAboutVisible;
    private bool _releaseDialogOperationStarted;
    private string? _lastShownUpdateError;
    private bool _isClosed;

    internal AboutWindow()
    {
        Title = $"{ProductIdentity.DisplayNameZh} - 关于";

        _root = new Grid { Margin = new Thickness(24, 18, 24, 18), RowSpacing = 16 };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = new StackPanel { Spacing = 16 };
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _root.Children.Add(scroll);

        content.Children.Add(CreateHeader());
        content.Children.Add(CreateDetailsCard());
        content.Children.Add(CreateDescriptionCard());
        content.Children.Add(CreateLicenseCard());

        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _updateLabel = new TextBlock
        {
            Text = "检查更新",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _updateButton = new Button
        {
            Content = _updateLabel,
            Style = (Style)Application.Current.Resources["DefaultButtonStyle"],
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Visible,
        };
        AutomationProperties.SetName(_updateButton, "检查更新");
        _updateButton.Click += OnUpdateButtonClick;
        var close = new Button
        {
            Content = "关闭",
            MinWidth = 88,
        };
        close.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 0 || args.NewSize.Height <= 0) return;
            _updateButton.Width = args.NewSize.Width;
            _updateButton.Height = args.NewSize.Height;
        };
        close.Click += (_, _) => Close();
        footerButtons.Children.Add(_updateButton);
        footerButtons.Children.Add(close);
        Grid.SetRow(footer, 1);
        Grid.SetColumn(footerButtons, 1);
        footer.Children.Add(footerButtons);
        _root.Children.Add(footer);

        WinUiWindowChrome.Attach(this, _root, Title, "\uE946");
        WinUiUiHost.ConfigureWindow(this, 520, 640, resizable: false);
        UpdateService.StateChanged += OnUpdateStateChanged;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _isAboutVisible = false;
            UpdateService.StateChanged -= OnUpdateStateChanged;
        };
        ApplyUpdateState(UpdateService.CurrentState);
    }

    private static UIElement CreateHeader()
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 12),
        };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yw_tray_256.png");
        panel.Children.Add(new Image
        {
            Source = new BitmapImage { UriSource = new Uri(iconPath) },
            Stretch = Stretch.Uniform,
            Width = 72,
            Height = 72,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = ProductIdentity.DisplayNameZh,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = ProductIdentity.DisplayNameEn,
            Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return panel;
    }

    private static UIElement CreateDetailsCard()
    {
        var details = new StackPanel { Spacing = 0 };
        AddDetailRow(details, "版本", GetVersion());
        AddDivider(details);
        AddDetailRow(details, "作者", "TOBYmiz");
        AddDivider(details);
        details.Children.Add(CreateRepositoryRow());

        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock
        {
            Text = "应用信息",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        card.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["YuWangCardStyle"],
            Child = details,
        });
        return card;
    }

    private static UIElement CreateDescriptionCard()
    {
        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock
        {
            Text = "关于此应用",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        card.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["YuWangCardStyle"],
            Child = new TextBlock
            {
                Text = "将育网云盘目录接入 Windows 文件资源管理器，按需获取云端文件。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
            },
        });
        return card;
    }

    private static UIElement CreateLicenseCard()
    {
        var entries = new StackPanel { Spacing = 0 };
        for (var index = 0; index < OpenSourceLicenses.Length; index++)
        {
            var entry = OpenSourceLicenses[index];
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock
            {
                Text = entry.Component,
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = entry.License,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
            });
            entries.Children.Add(new Border
            {
                Padding = new Thickness(0, 8, 0, 8),
                Child = text,
            });
            if (index < OpenSourceLicenses.Length - 1)
                AddDivider(entries);
        }

        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock
        {
            Text = "开源许可",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        card.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["YuWangCardStyle"],
            Child = entries,
        });
        return card;
    }

    private static void AddDetailRow(Panel panel, string label, string value)
    {
        var row = new Grid { MinHeight = 42, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
        });
        var valueText = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        panel.Children.Add(row);
    }

    private static UIElement CreateRepositoryRow()
    {
        var row = new Grid { MinHeight = 42, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = "开源仓库",
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
        });

        var linkText = new TextBlock
        {
            Text = RepositoryUrl,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            MaxWidth = 330,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        var link = new HyperlinkButton
        {
            Content = linkText,
            NavigateUri = new Uri(RepositoryUrl),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        link.PointerEntered += (_, _) =>
        {
            linkText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            linkText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        };
        link.PointerExited += (_, _) =>
        {
            linkText.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            linkText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        };
        Grid.SetColumn(link, 1);
        row.Children.Add(link);
        return row;
    }

    private static void AddDivider(Panel panel)
    {
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        });
    }

    private static string GetVersion()
    {
        return ProductVersion.Current;
    }

    private async void OnUpdateButtonClick(object sender, RoutedEventArgs args)
    {
        if (_isClosed || !_updateButton.IsEnabled) return;
        var current = UpdateService.CurrentState;
        if (current.IsAvailable)
        {
            await ShowAvailableReleaseDialogAsync(current, force: true);
        }
        else
        {
            var outcome = await UpdateService.CheckNowAsync(Program.ApplicationCancellationToken);
            if (!_isClosed && outcome == UpdateCheckOutcome.UpdateAvailable)
            {
                await ShowAvailableReleaseDialogAsync(UpdateService.CurrentState, force: true);
            }
            else if (!_isClosed && outcome == UpdateCheckOutcome.NoUpdate)
            {
                await ShowUpdateMessageAsync("检查更新", "已是最新版本");
            }
        }
        if (!_isClosed)
            _updateButton.IsEnabled = UpdateService.CurrentState.Activity == UpdateActivity.Idle;
    }

    internal void OnEnteredAbout()
    {
        if (_isClosed) return;
        _isAboutVisible = true;
        _promptedReleaseTag = null;

        var state = UpdateService.CurrentState;
        ApplyUpdateState(state);
        if (state.Activity == UpdateActivity.Idle && state.IsAvailable && state.ErrorMessage is null)
        {
            _ = ShowAvailableReleaseDialogAsync(state);
        }
    }

    private void OnUpdateStateChanged(UpdateUiState state)
    {
        if (!WinUiUiHost.Post(() =>
        {
            ApplyUpdateState(state);
            ApplyReleaseDialogUpdateState(state);
            if (_isAboutVisible && state.Activity == UpdateActivity.Idle
                && state.IsAvailable && state.ErrorMessage is null)
                _ = ShowAvailableReleaseDialogAsync(state);
        }))
            Serilog.Log.Debug("关于窗口关闭前未能投递自动更新状态");
    }

    private void ApplyUpdateState(UpdateUiState state)
    {
        if (_isClosed) return;

        _updateButton.Visibility = Visibility.Visible;
        _updateButton.IsEnabled = state.Activity == UpdateActivity.Idle;
        _updateButton.Style = (Style)Application.Current.Resources[
            state.IsAvailable ? "AccentButtonStyle" : "DefaultButtonStyle"];
        _updateLabel.Text = state.Activity switch
        {
            UpdateActivity.Checking => "检查中…",
            UpdateActivity.Connecting => "连接中…",
            UpdateActivity.Downloading => "下载中…",
            UpdateActivity.Launching => "启动中…",
            _ => state.IsAvailable ? "更新" : "检查更新",
        };
        AutomationProperties.SetName(_updateButton, state.Activity switch
        {
            UpdateActivity.Checking => "正在检查更新",
            UpdateActivity.Connecting => "正在连接更新服务器",
            UpdateActivity.Downloading => "正在下载更新",
            UpdateActivity.Launching => "正在启动更新安装器",
            _ => state.IsAvailable ? "更新到 " + (state.Release?.TagName ?? "最新版本") : "检查更新",
        });

        if (state.ErrorMessage is null)
        {
            _lastShownUpdateError = null;
            return;
        }
        if (string.Equals(_lastShownUpdateError, state.ErrorMessage, StringComparison.Ordinal)) return;
        _lastShownUpdateError = state.ErrorMessage;
        if (_releaseDialog is not null) return;
        var title = state.ErrorMessage.StartsWith("检查更新失败", StringComparison.Ordinal)
            ? "检查更新失败" : "更新失败";
        _ = ShowUpdateMessageAsync(title, state.ErrorMessage);
    }

    private async Task ShowAvailableReleaseDialogAsync(UpdateUiState state, bool force = false)
    {
        if (_isClosed || !_isAboutVisible || !state.IsAvailable || state.Release is null
            || _releaseDialog is not null
            || (!force && string.Equals(_promptedReleaseTag, state.Release.TagName, StringComparison.Ordinal)))
            return;

        var release = state.Release;
        _promptedReleaseTag = release.TagName;
        _releaseDialogOperationStarted = false;
        _dialogDisplayedActivity = UpdateActivity.Idle;
        _releaseDialogUpdateVisual = CreateUpdateButtonVisual("更新");
        _releaseDialogStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = FormatReleaseSummary(release),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["YuWangCaptionTextBlockStyle"],
        });
        content.Children.Add(new ScrollViewer
        {
            Content = CreateMarkdownContent(release.Body),
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        content.Children.Add(_releaseDialogStatus);

        var dialog = new ContentDialog
        {
            Title = "发现新版本",
            Content = content,
            PrimaryButtonText = "更新",
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        dialog.Opened += (_, _) =>
        {
            _releaseDialogUpdateButton = FindPrimaryDialogButton(dialog);
            if (_releaseDialogUpdateButton is null)
            {
                Serilog.Log.Warning("无法找到更新提示框的主按钮动画目标");
                return;
            }

            _releaseDialogUpdateButton.Content = _releaseDialogUpdateVisual.Content;
            AutomationProperties.SetName(_releaseDialogUpdateButton, "更新");
            ApplyReleaseDialogUpdateState(UpdateService.CurrentState);
        };
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true;
            if (_releaseDialogOperationStarted || UpdateService.CurrentState.Activity != UpdateActivity.Idle)
                return;

            _releaseDialogOperationStarted = true;
            if (_releaseDialogUpdateButton is not null)
                _releaseDialogUpdateButton.IsEnabled = false;
            _ = UpdateService.DownloadAndLaunchAsync(Program.ApplicationCancellationToken);
        };

        _releaseDialog = dialog;
        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            _promptedReleaseTag = null;
            Serilog.Log.Warning(error, "无法显示更新提示");
        }
        finally
        {
            if (ReferenceEquals(_releaseDialog, dialog))
            {
                _releaseDialog = null;
                _releaseDialogUpdateButton = null;
                _releaseDialogUpdateVisual = null;
                _releaseDialogStatus = null;
                _releaseDialogOperationStarted = false;
            }
        }
    }

    private void ApplyReleaseDialogUpdateState(UpdateUiState state)
    {
        if (_releaseDialog is null || _releaseDialogUpdateVisual is null) return;

        var visual = _releaseDialogUpdateVisual;
        visual.Label.Text = state.Activity switch
        {
            UpdateActivity.Checking => "检查中…",
            UpdateActivity.Launching => "启动中…",
            _ => "更新",
        };
        visual.Percent.Text = state.DownloadPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        if (_releaseDialogUpdateButton is not null)
        {
            _releaseDialogUpdateButton.IsEnabled = state.Activity == UpdateActivity.Idle && state.IsAvailable;
            AutomationProperties.SetName(_releaseDialogUpdateButton, state.Activity switch
            {
                UpdateActivity.Checking => "正在检查更新",
                UpdateActivity.Connecting => "正在连接更新服务器",
                UpdateActivity.Downloading => "正在下载更新 " + visual.Percent.Text,
                UpdateActivity.Launching => "正在启动更新安装器",
                _ => state.IsAvailable ? "更新到 " + (state.Release?.TagName ?? "最新版本") : "更新",
            });
        }

        if (_dialogDisplayedActivity != state.Activity)
        {
            _dialogDisplayedActivity = state.Activity;
            var isChecking = state.Activity is UpdateActivity.Checking or UpdateActivity.Connecting;
            visual.Progress.IsActive = isChecking;
            AnimateOpacity(visual.Label, state.Activity is UpdateActivity.Idle or UpdateActivity.Launching ? 1 : 0);
            AnimateOpacity(visual.Progress, isChecking ? 1 : 0);
            AnimateOpacity(visual.Percent, state.Activity == UpdateActivity.Downloading ? 1 : 0);
        }

        if (_releaseDialogStatus is not null)
        {
            _releaseDialogStatus.Text = state.ErrorMessage ?? "";
            _releaseDialogStatus.Visibility = string.IsNullOrWhiteSpace(state.ErrorMessage)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (state.Activity == UpdateActivity.Idle && state.ErrorMessage is not null)
            _releaseDialogOperationStarted = false;
    }

    private static Button? FindPrimaryDialogButton(ContentDialog dialog)
    {
        var descendants = new Stack<DependencyObject>();
        descendants.Push(dialog);
        Button? textMatch = null;
        while (descendants.Count > 0)
        {
            var current = descendants.Pop();
            if (current is Button button)
            {
                if (button.Name.Contains("PrimaryButton", StringComparison.OrdinalIgnoreCase))
                    return button;
                if (button.Content is string text && string.Equals(text, "更新", StringComparison.Ordinal))
                    textMatch = button;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = childCount - 1; index >= 0; index--)
                descendants.Push(VisualTreeHelper.GetChild(current, index));
        }
        return textMatch;
    }

    private static string FormatReleaseSummary(GitHubRelease release)
    {
        if (release.PublishedDate is not { } publishedDate)
            return release.TagName;

        var dateText = publishedDate.Year == DateTime.Now.Year
            ? $"{publishedDate.Month} 月 {publishedDate.Day} 日"
            : $"{publishedDate.Year} 年 {publishedDate.Month} 月 {publishedDate.Day} 日";
        return release.TagName + " · " + dateText;
    }

    private static UpdateButtonVisual CreateUpdateButtonVisual(string label)
    {
        var content = new Grid { MinWidth = 56, Height = 20 };
        var text = new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var progress = new ProgressRing
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var percent = new TextBlock
        {
            Text = "0%",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        content.Children.Add(text);
        content.Children.Add(progress);
        content.Children.Add(percent);
        return new UpdateButtonVisual(content, text, progress, percent);
    }

    private static UIElement CreateMarkdownContent(string markdown)
    {
        var panel = new StackPanel { Spacing = 8 };
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var codeLines = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    codeLines.Add(lines[index++]);
                if (index < lines.Length) index++;
                panel.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["YuWangCardStyle"],
                    Child = new TextBlock
                    {
                        Text = string.Join(Environment.NewLine, codeLines),
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                });
                continue;
            }

            var headingLevel = GetHeadingLevel(lines[index], out var headingText);
            if (headingLevel > 0)
            {
                var heading = new TextBlock { TextWrapping = TextWrapping.Wrap };
                heading.FontSize = headingLevel switch
                {
                    1 => 22,
                    2 => 20,
                    3 => 18,
                    _ => 16,
                };
                heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                AppendMarkdownInlines(heading.Inlines, headingText);
                panel.Children.Add(heading);
                index++;
                continue;
            }

            if (IsHorizontalRule(lines[index]))
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                });
                index++;
                continue;
            }

            if (TryParseListItem(lines[index], out var indentation, out var marker, out var itemText))
            {
                var list = new StackPanel { Spacing = 4 };
                while (index < lines.Length
                    && TryParseListItem(lines[index], out indentation, out marker, out itemText))
                {
                    var item = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(Math.Min(indentation, 8) * 12, 0, 0, 0),
                    };
                    item.Inlines.Add(new Run { Text = marker + " " });
                    AppendMarkdownInlines(item.Inlines, itemText);
                    list.Children.Add(item);
                    index++;
                }
                panel.Children.Add(list);
                continue;
            }

            if (IsBlockQuote(lines[index]))
            {
                var quoteLines = new List<string>();
                while (index < lines.Length && IsBlockQuote(lines[index]))
                {
                    quoteLines.Add(StripBlockQuote(lines[index++]));
                }
                var quote = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Margin = new Thickness(12, 0, 0, 0),
                };
                AppendMarkdownInlines(quote.Inlines, string.Join(" ", quoteLines));
                panel.Children.Add(quote);
                continue;
            }

            var paragraphLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])
                && !StartsMarkdownBlock(lines[index]))
            {
                paragraphLines.Add(lines[index++].Trim());
            }
            if (paragraphLines.Count == 0)
            {
                paragraphLines.Add(lines[index++].Trim());
            }
            var paragraph = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AppendMarkdownInlines(paragraph.Inlines, string.Join(" ", paragraphLines));
            panel.Children.Add(paragraph);
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "暂无发布说明。", TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static int GetHeadingLevel(string line, out string text)
    {
        var trimmed = line.TrimStart();
        var count = 0;
        while (count < trimmed.Length && count < 6 && trimmed[count] == '#') count++;
        if (count == 0 || count == trimmed.Length || !char.IsWhiteSpace(trimmed[count]))
        {
            text = "";
            return 0;
        }
        text = trimmed[(count + 1)..].Trim();
        return count;
    }

    private static bool IsHorizontalRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3) return false;
        var marker = trimmed[0];
        if (marker is not ('-' or '*' or '_')) return false;
        var count = 0;
        foreach (var character in trimmed)
        {
            if (character == marker) count++;
            else if (!char.IsWhiteSpace(character)) return false;
        }
        return count >= 3;
    }

    private static bool TryParseListItem(string line, out int indentation, out string marker, out string text)
    {
        var trimmed = line.TrimStart();
        indentation = line.Length - trimmed.Length;
        marker = "";
        text = "";
        if (trimmed.Length < 2) return false;

        var markerLength = 0;
        if (trimmed[0] is '-' or '*' or '+')
        {
            marker = "•";
            markerLength = 1;
        }
        else
        {
            while (markerLength < trimmed.Length && char.IsAsciiDigit(trimmed[markerLength])) markerLength++;
            if (markerLength == 0 || markerLength >= trimmed.Length || trimmed[markerLength] is not ('.' or ')'))
                return false;
            marker = trimmed[..(markerLength + 1)];
            markerLength++;
        }

        if (markerLength >= trimmed.Length || !char.IsWhiteSpace(trimmed[markerLength])) return false;
        text = trimmed[(markerLength + 1)..].Trim();
        return true;
    }

    private static bool IsBlockQuote(string line) => line.TrimStart().StartsWith(">", StringComparison.Ordinal);

    private static string StripBlockQuote(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 1 ? trimmed[1..].TrimStart() : "";
    }

    private static bool StartsMarkdownBlock(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal)
        || GetHeadingLevel(line, out _) > 0
        || IsHorizontalRule(line)
        || IsBlockQuote(line)
        || TryParseListItem(line, out _, out _, out _);

    private static void AppendMarkdownInlines(InlineCollection inlines, string text, int depth = 0)
    {
        if (depth >= 8)
        {
            inlines.Add(new Run { Text = text });
            return;
        }

        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            inlines.Add(new Run { Text = plain.ToString() });
            plain.Clear();
        }

        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                plain.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if ((text[index] == '[' || text[index] == '!')
                && TryReadMarkdownLink(text, index, out var linkEnd, out var label, out var linkUri))
            {
                FlushPlain();
                var link = new Hyperlink
                {
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                };
                link.Click += (_, _) => OpenExternalLink(linkUri);
                AppendMarkdownInlines(link.Inlines, label, depth + 1);
                inlines.Add(link);
                index = linkEnd;
                continue;
            }

            if (TryReadBareLink(text, index, out var bareLinkEnd, out var bareLinkUri))
            {
                FlushPlain();
                var link = new Hyperlink
                {
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                };
                link.Click += (_, _) => OpenExternalLink(bareLinkUri);
                link.Inlines.Add(new Run { Text = bareLinkUri.AbsoluteUri });
                inlines.Add(link);
                index = bareLinkEnd;
                continue;
            }

            var marker = GetInlineMarker(text, index);
            if (marker is not null)
            {
                var closing = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
                if (closing > index + marker.Length)
                {
                    FlushPlain();
                    var span = new Span();
                    if (marker is "**" or "__")
                        span.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    else if (marker == "~~")
                        span.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                    else if (marker != "`")
                        span.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    if (marker == "`")
                        span.FontFamily = new FontFamily("Consolas");
                    AppendMarkdownInlines(span.Inlines,
                        text[(index + marker.Length)..closing], depth + 1);
                    inlines.Add(span);
                    index = closing + marker.Length;
                    continue;
                }
            }

            plain.Append(text[index++]);
        }
        FlushPlain();
    }

    private static string? GetInlineMarker(string text, int index)
    {
        if (index + 1 < text.Length)
        {
            var pair = text.Substring(index, 2);
            if (pair is "**" or "__" or "~~") return pair;
        }
        return text[index] is '`' or '*' or '_' ? text[index].ToString() : null;
    }

    private static bool TryReadMarkdownLink(
        string text,
        int index,
        out int end,
        out string label,
        out Uri uri)
    {
        var bracketStart = text[index] == '!' ? index + 1 : index;
        end = index;
        label = "";
        uri = new Uri("about:blank");
        if (bracketStart >= text.Length || text[bracketStart] != '[') return false;

        var bracketEnd = text.IndexOf("](", bracketStart + 1, StringComparison.Ordinal);
        if (bracketEnd <= bracketStart + 1) return false;
        var parenthesisEnd = text.IndexOf(')', bracketEnd + 2);
        if (parenthesisEnd < 0) return false;
        var target = text[(bracketEnd + 2)..parenthesisEnd].Trim();
        if (target.StartsWith('<') && target.EndsWith('>')) target = target[1..^1];
        var titleStart = target.IndexOfAny([' ', '\t']);
        if (titleStart >= 0) target = target[..titleStart];
        if (!TryCreateExternalUri(target, out uri)) return false;

        label = text[(bracketStart + 1)..bracketEnd];
        end = parenthesisEnd + 1;
        return true;
    }

    private static bool TryReadBareLink(string text, int index, out int end, out Uri uri)
    {
        end = index;
        uri = new Uri("about:blank");
        if (!(text.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.AsSpan(index).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.AsSpan(index).StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
            return false;

        var tokenEnd = index;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])
            && text[tokenEnd] is not ('<' or '>'))
            tokenEnd++;
        while (tokenEnd > index && text[tokenEnd - 1] is '.' or ',' or ';' or ':' or '!' or '?')
            tokenEnd--;
        if (!TryCreateExternalUri(text[index..tokenEnd], out uri)) return false;
        end = tokenEnd;
        return true;
    }

    private static bool TryCreateExternalUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme is "http" or "https" or "mailto")
            return true;
        uri = new Uri("about:blank");
        return false;
    }

    private static void OpenExternalLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "无法打开 Release 说明中的链接");
        }
    }

    private async Task ShowUpdateMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        try { await dialog.ShowAsync(); }
        catch (Exception error) { Serilog.Log.Warning(error, "无法显示更新失败信息"); }
    }

    private static void AnimateOpacity(UIElement element, float targetOpacity)
    {
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, targetOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(UpdateFadeMilliseconds);
        animation.Target = "Opacity";
        element.StartAnimation(animation);
    }

    private sealed record UpdateButtonVisual(Grid Content, TextBlock Label, ProgressRing Progress, TextBlock Percent);
}
