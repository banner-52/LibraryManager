using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LibraryManager.Models;
using LibraryManager.Services;
using Microsoft.Win32;
using System.Windows.Media;
using System.Runtime.InteropServices;
using AppThemeMode = LibraryManager.Models.ThemeMode;

namespace LibraryManager.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsService _settingsService = new();
    private readonly ThemeService _themeService = new();
    private readonly SelfUpdateService _selfUpdateService = new();
    private readonly BackgroundUpdateTaskService _backgroundUpdateTaskService = new();
    private AppSettings _settings = new();
    private ApplicationUpdate? _availableUpdate;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    public event Action<AppSettings>? SaveRequested;
    public event Action? CancelRequested;
    public event Action? FirstLaunchGuideRequested;

    public void ApplyGuideSettings(AppSettings settings)
    {
        _settings = settings;
        SourceFolderTextBox.Text = settings.RepositoryPath;
        DesignComboBox.SelectedIndex = (int)settings.Design;
        ThemeComboBox.SelectedIndex = ThemeToIndex(settings.Theme);
        UpdateCustomColourVisibility();
    }

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        OpenRepositoryButton.Tag = "https://github.com/" + DistributionInfo.Current.Repository;
        OpenReleasesButton.Tag = "https://github.com/" + DistributionInfo.Current.Repository + "/releases";
        _settings = await _settingsService.LoadAsync();
        SourceFolderTextBox.Text = _settings.RepositoryPath;
        EnableOnlineCatalogueCheckBox.IsChecked = _settings.EnableOnlineCatalogue;
        UseCustomOnlineCatalogueCheckBox.IsChecked = _settings.UseCustomOnlineCatalogue;
        OnlineCatalogueUrlTextBox.Text = _settings.UseCustomOnlineCatalogue ? _settings.OnlineCatalogueUrl : AppSettings.OfficialOnlineCatalogueUrl;
        UpdateCatalogueUrlEditability();
        DesignComboBox.SelectedIndex = (int)_settings.Design;
        ThemeComboBox.SelectedIndex = ThemeToIndex(_settings.Theme);
        BackgroundColourTextBox.Text = _settings.CustomBackgroundColor;
        SurfaceColourTextBox.Text = _settings.CustomSurfaceColor;
        AccentColourTextBox.Text = _settings.CustomAccentColor;
        BackgroundImageTextBox.Text = _settings.BackgroundImagePath ?? string.Empty;
        LoadBuiltInBackgrounds();
        PanelOpacitySlider.Value = _settings.PanelOpacity * 100;
        CheckSoftwareAtStartupCheckBox.IsChecked = _settings.CheckForUpdatesAtStartup;
        CheckApplicationAtStartupCheckBox.IsChecked = _settings.CheckForApplicationUpdates;
        EnableBackgroundNotificationsCheckBox.IsChecked = _settings.EnableBackgroundUpdateNotifications;
        DailyCheckTimeTextBox.Text = _settings.DailyUpdateCheckTime;
        BackgroundFrequencyComboBox.SelectedIndex = _settings.BackgroundUpdateFrequency == BackgroundUpdateFrequency.Weekly ? 1 : 0;
        WeeklyDayComboBox.SelectedIndex = DayOfWeekToIndex(_settings.WeeklyUpdateCheckDay);
        UpdateWeeklyDayVisibility();
        if (_settings.EnableBackgroundUpdateNotifications)
        {
            try { await _backgroundUpdateTaskService.EnsureCreatedAsync(_settings.DailyUpdateCheckTime, _settings.BackgroundUpdateFrequency, _settings.WeeklyUpdateCheckDay); }
            catch (Exception ex) { BackgroundTaskStatusTextBlock.Text = $"Scheduled task verification failed: {ex.Message}"; }
        }
        await RefreshBackgroundTaskStatusAsync();
        EnableLoggingCheckBox.IsChecked = _settings.EnableLogging;
        LogRetentionDaysTextBox.Text = _settings.LogRetentionDays.ToString();
        ShowRequestCounterCheckBox.IsChecked = _settings.ShowRequestCounter;
        VersionTextBlock.Text = $"Library Manager {DistributionInfo.Current.DevLabel}v{SelfUpdateService.GetCurrentVersionText()}";
        UpdateCustomColourVisibility();
        await RefreshGitHubStatusAsync();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var error = ValidateSettings();
        if (error is not null)
        {
            ThemedMessageBox.Show(error, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.RepositoryPath = SourceFolderTextBox.Text.Trim();
        _settings.EnableOnlineCatalogue = EnableOnlineCatalogueCheckBox.IsChecked == true;
        _settings.UseCustomOnlineCatalogue = UseCustomOnlineCatalogueCheckBox.IsChecked == true;
        if (_settings.UseCustomOnlineCatalogue)
            _settings.OnlineCatalogueUrl = OnlineCatalogueUrlTextBox.Text.Trim();
        _settings.Design = (AppDesign)Math.Max(0, DesignComboBox.SelectedIndex);
        _settings.Theme = IndexToTheme(ThemeComboBox.SelectedIndex);
        _settings.CustomBackgroundColor = BackgroundColourTextBox.Text.Trim();
        _settings.CustomSurfaceColor = SurfaceColourTextBox.Text.Trim();
        _settings.CustomAccentColor = AccentColourTextBox.Text.Trim();
        _settings.BackgroundImagePath = NullIfEmpty(BackgroundImageTextBox.Text);
        _settings.BuiltInBackground = BuiltInBackgroundComboBox.SelectedItem as string ?? "None";
        _settings.PanelOpacity = PanelOpacitySlider.Value / 100;
        _settings.CheckForUpdatesAtStartup = CheckSoftwareAtStartupCheckBox.IsChecked == true;
        _settings.CheckForApplicationUpdates = CheckApplicationAtStartupCheckBox.IsChecked == true;
        _settings.EnableBackgroundUpdateNotifications = EnableBackgroundNotificationsCheckBox.IsChecked == true;
        _settings.DailyUpdateCheckTime = DailyCheckTimeTextBox.Text.Trim();
        _settings.BackgroundUpdateFrequency = SelectedBackgroundUpdateFrequency;
        _settings.WeeklyUpdateCheckDay = IndexToDayOfWeek(WeeklyDayComboBox.SelectedIndex);
        _settings.EnableLogging = EnableLoggingCheckBox.IsChecked == true;
        _settings.LogRetentionDays = int.Parse(LogRetentionDaysTextBox.Text.Trim());
        _settings.ShowRequestCounter = ShowRequestCounterCheckBox.IsChecked == true;
        await _settingsService.SaveAsync(_settings);
        try
        {
            if (_settings.EnableBackgroundUpdateNotifications)
                await _backgroundUpdateTaskService.EnsureCreatedAsync(_settings.DailyUpdateCheckTime, _settings.BackgroundUpdateFrequency, _settings.WeeklyUpdateCheckDay);
            else
                await _backgroundUpdateTaskService.RemoveAsync();
            await RefreshBackgroundTaskStatusAsync();
        }
        catch (Exception ex)
        {
            BackgroundTaskStatusTextBlock.Text = $"Scheduled task update failed: {ex.Message}";
        }
        _themeService.Apply(_settings);
        SaveRequested?.Invoke(_settings);
    }

    private void CustomCatalogueCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OnlineCatalogueUrlTextBox is null) return;
        UpdateCatalogueUrlEditability();
    }

    private void UpdateCatalogueUrlEditability()
    {
        bool custom = UseCustomOnlineCatalogueCheckBox.IsChecked == true;
        if (custom)
        {
            OnlineCatalogueUrlTextBox.Text = _settings.OnlineCatalogueUrl;
        }
        else
        {
            if (OnlineCatalogueUrlTextBox.IsEnabled && !string.IsNullOrWhiteSpace(OnlineCatalogueUrlTextBox.Text))
                _settings.OnlineCatalogueUrl = OnlineCatalogueUrlTextBox.Text.Trim();
            OnlineCatalogueUrlTextBox.Text = AppSettings.OfficialOnlineCatalogueUrl;
        }
        OnlineCatalogueUrlTextBox.IsEnabled = custom;
    }

    private string? ValidateSettings()
    {
        if (UseCustomOnlineCatalogueCheckBox.IsChecked == true &&
            (!Uri.TryCreate(OnlineCatalogueUrlTextBox.Text.Trim(), UriKind.Absolute, out var catalogueUri) || catalogueUri.Scheme != Uri.UriSchemeHttps))
            return "The custom online catalogue URL must be a valid HTTPS address.";
        if (!int.TryParse(LogRetentionDaysTextBox.Text.Trim(), out var retentionDays) || retentionDays < 0) return "Log retention must be 0 or a positive number of days.";
        if (!TimeOnly.TryParseExact(DailyCheckTimeTextBox.Text.Trim(), "HH:mm", out _)) return "Update check time must use the 24-hour HH:mm format.";
        if (!string.IsNullOrWhiteSpace(SourceFolderTextBox.Text) && SourceFolderTextBox.Text.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "The repository path contains invalid characters.";
        if (IndexToTheme(ThemeComboBox.SelectedIndex) == AppThemeMode.Custom)
        {
            foreach (var colour in new[] { BackgroundColourTextBox.Text, SurfaceColourTextBox.Text, AccentColourTextBox.Text })
            {
                try { _ = System.Windows.Media.ColorConverter.ConvertFromString(colour); }
                catch { return $"Invalid colour value: {colour}"; }
            }
        }
        return null;
    }

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the package repository", InitialDirectory = Directory.Exists(SourceFolderTextBox.Text) ? SourceFolderTextBox.Text : null };
        if (dialog.ShowDialog() == true) SourceFolderTextBox.Text = dialog.FolderName;
    }

    private async void ImportCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import local catalogue", Filter = "JSON catalogue|*.json|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var catalogue = new CatalogService();
            await catalogue.ImportAsync(dialog.FileName, SourceFolderTextBox.Text.Trim());
            ThemedMessageBox.Show($"{catalogue.SoftwareItems.Count} profile(s) imported.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ThemedMessageBox.Show($"Import failed: {ex.Message}", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RebuildCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var repositoryPath = SourceFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            ThemedMessageBox.Show("Choose a valid package repository folder first.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ThemedMessageBox.Show("Build a new local catalogue from the folders in the selected package repository? Existing profile configuration will be replaced.", "Build local catalogue", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var catalogue = new CatalogService();
            await catalogue.RebuildFromRepositoryAsync(repositoryPath);
            ThemedMessageBox.Show($"{catalogue.SoftwareItems.Count} software folder(s) detected. A new local catalogue was created.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ThemedMessageBox.Show($"Catalogue rebuild failed: {ex.Message}", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ExportCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export local catalogue", FileName = "catalog.json", DefaultExt = ".json", Filter = "JSON catalogue|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var catalogue = new CatalogService();
            await catalogue.LoadAsync(SourceFolderTextBox.Text.Trim());
            await catalogue.ExportAsync(dialog.FileName);
            ThemedMessageBox.Show("Catalogue exported successfully.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ThemedMessageBox.Show($"Export failed: {ex.Message}", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ConnectGitHubOAuthButton_Click(object sender, RoutedEventArgs e)
    {
        SetGitHubButtonsEnabled(false);
        GitHubDeviceCodeTextBlock.Visibility = Visibility.Collapsed;
        try
        {
            var authorization = await AppServices.GitHubAuthentication.BeginOAuthDeviceFlowAsync();
            GitHubDeviceCodeTextBlock.Text = $"Enter code {authorization.UserCode} in the GitHub page opened in your browser.";
            GitHubDeviceCodeTextBlock.Visibility = Visibility.Visible;
            Process.Start(new ProcessStartInfo(authorization.VerificationUri) { UseShellExecute = true });
            GitHubStatusTextBlock.Text = "Waiting for GitHub authorization...";
            await AppServices.GitHubAuthentication.CompleteOAuthDeviceFlowAsync(authorization);
            GitHubDeviceCodeTextBlock.Visibility = Visibility.Collapsed;
            UpdateGitHubStatus();
        }
        catch (Exception ex)
        {
            GitHubStatusTextBlock.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            SetGitHubButtonsEnabled(true);
        }
    }

    private async void ConnectGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        SetGitHubButtonsEnabled(false);
        try
        {
            await AppServices.GitHubAuthentication.ConnectWithPersonalAccessTokenAsync(GitHubTokenPasswordBox.Password);
            GitHubTokenPasswordBox.Clear();
            UpdateGitHubStatus();
        }
        catch (Exception ex)
        {
            GitHubStatusTextBlock.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            SetGitHubButtonsEnabled(true);
        }
    }

    private async void RefreshGitHubButton_Click(object sender, RoutedEventArgs e) => await RefreshGitHubStatusAsync();

    private void DisconnectGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.GitHubAuthentication.Disconnect();
            GitHubTokenPasswordBox.Clear();
            UpdateGitHubStatus();
        }
        catch (Exception ex)
        {
            GitHubStatusTextBlock.Text = $"Disconnect failed: {ex.Message}";
        }
    }

    private async Task RefreshGitHubStatusAsync()
    {
        SetGitHubButtonsEnabled(false);
        try
        {
            await AppServices.GitHubAuthentication.RefreshAccountAsync();
            UpdateGitHubStatus();
        }
        catch (Exception ex)
        {
            UpdateGitHubStatus();
            GitHubRateLimitTextBlock.Text = $"Rate limit check failed: {ex.Message}";
        }
        finally
        {
            SetGitHubButtonsEnabled(true);
        }
    }

    private void UpdateGitHubStatus()
    {
        var account = AppServices.GitHubAuthentication.Account;
        GitHubStatusTextBlock.Text = account.IsConnected
            ? $"Connected as {account.UserName ?? "GitHub user"}"
            : "Anonymous GitHub access";
        GitHubRateLimitTextBlock.Text = account.RateLimitReset is { } reset
            ? $"{account.RemainingRequests} API requests remaining. Resets {reset.LocalDateTime:g}."
            : "Rate limit information is not available yet.";
        DisconnectGitHubButton.IsEnabled = account.IsConnected;
    }

    private void SetGitHubButtonsEnabled(bool enabled)
    {
        ConnectGitHubOAuthButton.IsEnabled = enabled;
        ConnectGitHubButton.IsEnabled = enabled;
        RefreshGitHubButton.IsEnabled = enabled;
        DisconnectGitHubButton.IsEnabled = enabled && AppServices.GitHubAuthentication.Account.IsConnected;
    }

    private void ChooseColourButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox target }) return;

        uint customColor = 0;
        try
        {
            if (ColorConverter.ConvertFromString(target.Text) is Color parsed)
                customColor = (uint)(parsed.R | (parsed.G << 8) | (parsed.B << 16));
        }
        catch { }

        var customColors = new uint[16];
        var handle = GCHandle.Alloc(customColors, GCHandleType.Pinned);
        try
        {
            var chooseColor = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = Window.GetWindow(this) is { } owner ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero,
                rgbResult = customColor,
                lpCustColors = handle.AddrOfPinnedObject(),
                Flags = CC_FULLOPEN | CC_RGBINIT
            };

            if (!ChooseColor(ref chooseColor)) return;
            var value = chooseColor.rgbResult;
            var r = (byte)(value & 0xFF);
            var g = (byte)((value >> 8) & 0xFF);
            var b = (byte)((value >> 16) & 0xFF);
            target.Text = $"#FF{r:X2}{g:X2}{b:X2}";
        }
        finally
        {
            handle.Free();
        }
    }

    private const uint CC_RGBINIT = 0x00000001;
    private const uint CC_FULLOPEN = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public uint rgbResult;
        public IntPtr lpCustColors;
        public uint Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref CHOOSECOLOR chooseColor);

    private async Task RefreshBackgroundTaskStatusAsync()
    {
        var status = await _backgroundUpdateTaskService.GetStatusAsync();
        BackgroundTaskStatusTextBlock.Text = status;
    }

    private async void RepairBackgroundTaskButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _backgroundUpdateTaskService.EnsureCreatedAsync(
                DailyCheckTimeTextBox.Text.Trim(),
                SelectedBackgroundUpdateFrequency,
                IndexToDayOfWeek(WeeklyDayComboBox.SelectedIndex));
            await RefreshBackgroundTaskStatusAsync();
        }
        catch (Exception ex) { BackgroundTaskStatusTextBlock.Text = $"Task repair failed: {ex.Message}"; }
    }

    private async void CheckBackgroundUpdatesNowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _backgroundUpdateTaskService.RunNowAsync();
            BackgroundTaskStatusTextBlock.Text = "Background update check started.";
        }
        catch (Exception ex) { BackgroundTaskStatusTextBlock.Text = $"Background check could not start: {ex.Message}"; }
    }

    private async void TestNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _backgroundUpdateTaskService.SendTestNotificationAsync();
            BackgroundTaskStatusTextBlock.Text = "Test notification sent.";
        }
        catch (Exception ex)
        {
            BackgroundTaskStatusTextBlock.Text = $"Test notification could not be sent: {ex.Message}";
        }
    }

    private void OpenApplicationDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibraryManager");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folder}\"",
            UseShellExecute = true
        });
    }

    private void OpenFirstLaunchGuideButton_Click(object sender, RoutedEventArgs e) => FirstLaunchGuideRequested?.Invoke();

    private void LoadBuiltInBackgrounds()
    {
        BuiltInBackgroundComboBox.Items.Clear();
        BuiltInBackgroundComboBox.Items.Add("None");
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder)
                         .Where(file => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                BuiltInBackgroundComboBox.Items.Add(Path.GetFileName(file));
        }
        BuiltInBackgroundComboBox.SelectedItem = BuiltInBackgroundComboBox.Items.Contains(_settings.BuiltInBackground) ? _settings.BuiltInBackground : "None";
    }

    private void BuiltInBackgroundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BuiltInBackgroundComboBox.SelectedItem is not string selected) return;
        BackgroundImageTextBox.Text = selected == "None"
            ? string.Empty
            : Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", selected);
    }

    private void BrowseBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a background image", Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*" };
        if (dialog.ShowDialog() == true) BackgroundImageTextBox.Text = dialog.FileName;
    }

    private void SettingsSectionButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = (sender as FrameworkElement)?.Tag as string;
        SourceSection.Visibility = selected == nameof(SourceSection) ? Visibility.Visible : Visibility.Collapsed;
        OnlineSection.Visibility = selected == nameof(OnlineSection) ? Visibility.Visible : Visibility.Collapsed;
        ThemeSection.Visibility = selected == nameof(ThemeSection) ? Visibility.Visible : Visibility.Collapsed;
        StartupSection.Visibility = selected == nameof(StartupSection) ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = selected == nameof(AboutSection) ? Visibility.Visible : Visibility.Collapsed;
    }

    private BackgroundUpdateFrequency SelectedBackgroundUpdateFrequency =>
        BackgroundFrequencyComboBox.SelectedIndex == 1
            ? BackgroundUpdateFrequency.Weekly
            : BackgroundUpdateFrequency.Daily;

    private void BackgroundFrequencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateWeeklyDayVisibility();

    private void UpdateWeeklyDayVisibility()
    {
        if (WeeklyDayPanel is not null)
            WeeklyDayPanel.Visibility = SelectedBackgroundUpdateFrequency == BackgroundUpdateFrequency.Weekly
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static int DayOfWeekToIndex(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static DayOfWeek IndexToDayOfWeek(int index) => index switch
    {
        0 => DayOfWeek.Monday,
        1 => DayOfWeek.Tuesday,
        2 => DayOfWeek.Wednesday,
        3 => DayOfWeek.Thursday,
        4 => DayOfWeek.Friday,
        5 => DayOfWeek.Saturday,
        6 => DayOfWeek.Sunday,
        _ => DayOfWeek.Monday
    };

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCustomColourVisibility();

    private static int ThemeToIndex(AppThemeMode theme) => theme switch
    {
        AppThemeMode.System => 0,
        AppThemeMode.Light => 1,
        AppThemeMode.Dark => 2,
        AppThemeMode.Custom => 3,
        _ => 0
    };

    private static AppThemeMode IndexToTheme(int index) => index switch
    {
        1 => AppThemeMode.Light,
        2 => AppThemeMode.Dark,
        3 => AppThemeMode.Custom,
        _ => AppThemeMode.System
    };

    private void UpdateCustomColourVisibility() { if (CustomColoursPanel is not null) CustomColoursPanel.Visibility = IndexToTheme(ThemeComboBox.SelectedIndex) == AppThemeMode.Custom ? Visibility.Visible : Visibility.Collapsed; }
    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    private async void CheckApplicationUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusTextBlock.Text = "Checking for updates...";
        try
        {
            _availableUpdate = await _selfUpdateService.CheckAsync();
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
            UpdateStatusTextBlock.Text = _availableUpdate is null ? "Library Manager is up to date." : $"Version {_availableUpdate.Version} is available.";
        }
        catch (Exception ex) { UpdateStatusTextBlock.Text = $"Update check failed: {ex.Message}"; }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        InstallUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(value => UpdateStatusTextBlock.Text = $"Downloading update: {value}%");
            await _selfUpdateService.DownloadAndRestartAsync(_availableUpdate, progress);
        }
        catch (Exception ex) { InstallUpdateButton.IsEnabled = true; UpdateStatusTextBlock.Text = $"Update failed: {ex.Message}"; }
    }

    private async void ReportIncidentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.GitHubAuthentication.Account.IsConnected)
        {
            ThemedMessageBox.Show("Connect a GitHub account in Online services before reporting an incident.", "Report incident", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new IncidentReportWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var incident = await new OnlineCatalogService().SubmitIncidentAsync(dialog.Summary, dialog.Details);
            ThemedMessageBox.Show($"Incident #{incident.Number} reported successfully.\n\n{incident.Url}", "Report incident", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Incident report failed: {ex.Message}", "Report incident", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

