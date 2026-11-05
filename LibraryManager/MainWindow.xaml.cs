using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LibraryManager.Models;
using LibraryManager.Services;
using LibraryManager.Views;
using LibraryManager.Views.Main;

namespace LibraryManager;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly ThemeService _themeService = new();
    private AppSettings _settings = new();
    private ModernView? _modernView;
    private ClassicView? _classicView;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        AppServices.Requests.RequestCountChanged += (_, _) => Dispatcher.Invoke(UpdateRequestCounter);
        ApplicationVersionTextBlock.Text = $"{DistributionInfo.Current.DevLabel}v{SelfUpdateService.GetCurrentVersionText()}";
        Title = $"Library Manager {DistributionInfo.Current.DevLabel}v{SelfUpdateService.GetCurrentVersionText()}";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        try { await new LogService().CleanupAsync(_settings.LogRetentionDays); }
        catch { /* Log cleanup must never prevent application startup. */ }
        ApplySettings();
        await RefreshGitHubRateLimitAsync();
        ShowWorkspace();
        ShowFirstLaunchGuideIfNeeded();
        await RefreshNotificationCountAsync();
        if (_settings.CheckForApplicationUpdates) await CheckApplicationUpdateAtStartupAsync();
    }

    private void ApplySettings()
    {
        _themeService.Apply(_settings);
        RepositoryPathRun.Text = string.IsNullOrWhiteSpace(_settings.RepositoryPath) ? "Not configured" : _settings.RepositoryPath;
        RequestCountTextBlock.Visibility = _settings.ShowRequestCounter ? Visibility.Visible : Visibility.Collapsed;
        UpdateRequestCounter();
        ApplyBackgroundImage();
    }

    private void ApplyBackgroundImage()
    {
        ApplicationBackgroundImage.Opacity = 0;
        ApplicationBackgroundImage.Source = null;
        if (string.IsNullOrWhiteSpace(_settings.BackgroundImagePath) || !File.Exists(_settings.BackgroundImagePath)) return;
        try
        {
            ApplicationBackgroundImage.Source = new BitmapImage(new Uri(_settings.BackgroundImagePath, UriKind.Absolute));
            ApplicationBackgroundImage.Opacity = .32;
        }
        catch { ApplicationBackgroundImage.Source = null; }
    }

    private void ShowWorkspace()
    {
        if (_settings.Design == AppDesign.Classic)
        {
            _classicView ??= new ClassicView();
            MainContent.Content = _classicView;
            return;
        }

        _modernView ??= new ModernView();
        MainContent.Content = _modernView;
    }

    private void NotificationButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new NewsView();
        view.UnreadCountChanged += UpdateNotificationCount;
        var window = new ThemedWindow
        {
            Title = "Notifications",
            Owner = this,
            Content = view,
            Width = 760,
            Height = 620,
            MinWidth = 620,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"),
            Padding = new Thickness(18)
        };
        window.ShowDialog();
        _ = RefreshNotificationCountAsync();
    }

    private void UpdateNotificationCount(int count)
    {
        NotificationCountTextBlock.Text = count > 0 ? count.ToString() : string.Empty;
        NotificationCountTextBlock.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationButton.ToolTip = count > 0 ? $"Notifications ({count} unread)" : "Notifications";
    }

    private async Task RefreshNotificationCountAsync()
    {
        try
        {
            var feed = await new NewsService().LoadAsync();
            var settings = await _settingsService.LoadAsync();
            var read = settings.ReadNewsIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unread = feed.Sections.All()
                .Select(item => item.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(id => !read.Contains(id));
            UpdateNotificationCount(unread);
        }
        catch
        {
            UpdateNotificationCount(0);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings();
    }

    private void ShowSettings()
    {
        var view = new SettingsView();
        view.SaveRequested += settings =>
        {
            _settings = settings;
            ApplySettings();
            _modernView?.ApplySettings(settings);
            ShowWorkspace();
        };
        view.CancelRequested += ShowWorkspace;
        view.FirstLaunchGuideRequested += () => ShowFirstLaunchGuide(true, view.ApplyGuideSettings);
        MainContent.Content = view;
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new HelpView();
        view.CancelRequested += ShowWorkspace;
        MainContent.Content = view;
    }

    private void ShowFirstLaunchGuideIfNeeded()
    {
        if (_settings.ShowFirstLaunchGuide) ShowFirstLaunchGuide();
    }

    private void ShowFirstLaunchGuide(bool returnToSettings = false, Action<AppSettings>? settingsSelectedInSettings = null)
    {
        var view = new FirstLaunchGuideView();
        var guideSettingsChanged = false;
        var window = new ThemedWindow
        {
            Title = "First Launch Guide",
            Owner = this,
            Content = view,
            Width = 820,
            Height = 680,
            MinWidth = 680,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"),
            Padding = new Thickness(18)
        };
        view.StartupPreferenceChanged += showAtStartup => _settings.ShowFirstLaunchGuide = showAtStartup;
        view.SettingsChanged += settings =>
        {
            _settings = settings;
            ApplySettings();
            _modernView?.ApplySettings(settings);
            settingsSelectedInSettings?.Invoke(settings);
            guideSettingsChanged = true;
        };
        view.CloseRequested += window.Close;
        window.Closed += (_, _) =>
        {
            if (guideSettingsChanged && !returnToSettings) ShowWorkspace();
        };
        window.ShowDialog();
    }
    private void UpdateRequestCounter()
    {
        var account = AppServices.GitHubAuthentication.Account;
        var remaining = account.RateLimitReset is null ? "unknown" : account.RemainingRequests.ToString();
        var connectionHint = account.IsConnected
            ? string.Empty
            : " (Connect a GitHub account to obtain a higher rate limit.)";
        RequestCountTextBlock.Text = $"GitHub API requests remaining: {remaining}{connectionHint} | This session: {AppServices.Requests.RequestCount}";
    }

    private async Task RefreshGitHubRateLimitAsync()
    {
        if (!_settings.ShowRequestCounter) return;
        try { await AppServices.GitHubAuthentication.RefreshAccountAsync(); }
        catch { }
        UpdateRequestCounter();
    }

    private async Task CheckApplicationUpdateAtStartupAsync()
    {
        try
        {
            var updateService = new SelfUpdateService();
            var update = await updateService.CheckAsync();
            if (update is null) return;

            var updateWindow = new ApplicationUpdateWindow(update) { Owner = this };
            if (updateWindow.ShowDialog() != true) return;

            var progress = new Progress<int>();
            await updateService.DownloadAndRestartAsync(update, progress);
        }
        catch
        {
            // Startup checks stay silent. The About section shows detailed errors on demand.
        }
    }
}

