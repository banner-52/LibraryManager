using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LibraryManager.Models;
using LibraryManager.Services;
using LibraryManager.Services.Detection;
using LibraryManager.Views.Controls;
using Microsoft.Win32;

namespace LibraryManager.Views.Main;

public partial class ModernView : UserControl, INotifyPropertyChanged
{
    private readonly CatalogService _catalogService = new();
    private readonly SettingsService _settingsService = new();
    private readonly UpdateService _updateService;
    private readonly LogService _logService = new();
    private CancellationTokenSource? _operationCancellation;
    private GridLength _previousRightPanelWidth = new(1, GridUnitType.Star);
    private string _searchText = string.Empty;
    private string _repositoryPath = string.Empty;
    private bool _catalogLoadFailed;
    private bool _initialLoadCompleted;

    public ModernView()
    {
        InitializeComponent();
        _updateService = CreateUpdateService();
        DataContext = this;
        Loaded += ModernView_Loaded;
        ShowTiles();
    }

    public ObservableCollection<Software> SoftwareItems => _catalogService.SoftwareItems;
    public int SoftwareCount => SoftwareItems.Count;
    public int UpdateAvailableCount => SoftwareItems.Count(item => item.Status == SoftwareStatus.UpdateAvailable);
    public event PropertyChangedEventHandler? PropertyChanged;

    private static UpdateService CreateUpdateService()
    {
        return new UpdateService(new GitHubDetection(AppServices.GitHubAuthentication, AppServices.Requests), new WebsiteDetection(renderedPageReader: BrowserPageReader.ReadHtmlAsync), new WingetDetection(), new DownloadService(), new PackageService());
    }

    private async void ModernView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialLoadCompleted) return;
        _initialLoadCompleted = true;

        await RunRefreshAsync();
        var settings = await _settingsService.LoadAsync();
        if (settings.CheckForUpdatesAtStartup && SoftwareItems.Count > 0) await RunCheckAsync(SoftwareItems.ToList());
    }

    public void ApplySettings(AppSettings settings)
    {
        var repositoryChanged = !string.Equals(_repositoryPath, settings.RepositoryPath, StringComparison.OrdinalIgnoreCase);
        _repositoryPath = settings.RepositoryPath;
        if (repositoryChanged)
            _ = RunRefreshAsync();
    }

    private async Task ReloadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync();
        _repositoryPath = settings.RepositoryPath;
        try
        {
            await _catalogService.LoadAsync(_repositoryPath, cancellationToken);
            _catalogLoadFailed = false;
        }
        catch (InvalidDataException ex)
        {
            _catalogLoadFailed = true;
            ThemedMessageBox.Show(ex.Message, "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _catalogLoadFailed = true;
            ThemedMessageBox.Show($"The local catalogue could not be accessed: {ex.Message}", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        CollectionViewSource.GetDefaultView(SoftwareItems).Refresh();
        NotifyCount();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RunRefreshAsync();

    private Task RunRefreshAsync() => RunInlineOperationAsync("Refreshing local catalogue", async (state, token) =>
    {
        state.IsIndeterminate = true;
        await ReloadCatalogAsync(token);
        state.Message = "Scanning package folders";
        await Task.Run(() => _catalogService.RefreshStoredVersions(_repositoryPath, token), token);
        CollectionViewSource.GetDefaultView(SoftwareItems).Refresh();
        state.IsIndeterminate = false;
        state.Percentage = 100;
        state.Message = "Local catalogue refreshed";
    });

    private async void ImportCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import local catalogue", Filter = "JSON catalogue|*.json|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _catalogService.ImportAsync(dialog.FileName, _repositoryPath);
            CollectionViewSource.GetDefaultView(SoftwareItems).Refresh();
            NotifyCount();
            ThemedMessageBox.Show($"{SoftwareItems.Count} profile(s) imported.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ThemedMessageBox.Show($"Import failed: {ex.Message}", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ExportCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export local catalogue", FileName = "catalog.json", DefaultExt = ".json", Filter = "JSON catalogue|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _catalogService.ExportAsync(dialog.FileName);
            ThemedMessageBox.Show("Catalogue exported successfully.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ThemedMessageBox.Show($"Export failed: {ex.Message}", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void ShowListButton_Click(object sender, RoutedEventArgs e) => ShowList();
    private void ShowTilesButton_Click(object sender, RoutedEventArgs e) => ShowTiles();

    private void ShowList()
    {
        var view = new SoftwareList { DataContext = this };
        WireSoftwareActions(view);
        SoftwareContent.Content = view;
    }

    private void ShowTiles()
    {
        var view = new SoftwareTiles { DataContext = this };
        WireSoftwareActions(view);
        SoftwareContent.Content = view;
    }

    private void WireSoftwareActions(SoftwareList view)
    {
        view.EditRequested += ShowProfile;
        view.UpdateRequested += software => _ = RunUpdateAsync([software]);
        view.DeleteRequested += DeleteSoftwareAsync;
        view.ManageVersionsRequested += ShowVersions;
        view.FavoriteChanged += FavoriteChangedAsync;
        view.NotificationChanged += NotificationChangedAsync;
    }

    private void WireSoftwareActions(SoftwareTiles view)
    {
        view.EditRequested += ShowProfile;
        view.UpdateRequested += software => _ = RunUpdateAsync([software]);
        view.DeleteRequested += DeleteSoftwareAsync;
        view.ManageVersionsRequested += ShowVersions;
        view.FavoriteChanged += FavoriteChangedAsync;
        view.NotificationChanged += NotificationChangedAsync;
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) { _searchText = SearchTextBox.Text.Trim(); ApplyFilter(); }
    private void ViewFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        // SelectionChanged can fire while InitializeComponent is still constructing
        // the ComboBoxes, before all named controls are assigned.
        if (!IsInitialized || ViewFilterComboBox is null)
            return;

        var view = CollectionViewSource.GetDefaultView(SoftwareItems);
        view.Filter = item => item is Software software &&
            (string.IsNullOrWhiteSpace(_searchText) || software.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)) &&
            MatchesSelectedStatus(software) && MatchesMethodFilter(software) && MatchesFavoriteFilter(software);
        view.SortDescriptions.Clear();
        var sort = (ViewFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (sort == "A → Z") view.SortDescriptions.Add(new SortDescription(nameof(Software.DisplayName), ListSortDirection.Ascending));
        else if (sort == "Z → A") view.SortDescriptions.Add(new SortDescription(nameof(Software.DisplayName), ListSortDirection.Descending));
    }

    private bool MatchesMethodFilter(Software software)
    {
        var selection = (ViewFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return selection switch
        {
            "Method: Website" => software.Source.Kind == SoftwareSourceKind.Website,
            "Method: GitHub Releases" => software.Source.Kind == SoftwareSourceKind.GitHubReleases,
            "Method: WinGet" => software.Source.Kind == SoftwareSourceKind.WinGet,
            "Method: Manual" => software.Source.Kind == SoftwareSourceKind.None,
            _ => true
        };
    }

    private bool MatchesFavoriteFilter(Software software) =>
        (ViewFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() != "Favorites" || software.IsFavorite;

    private async void FavoriteChangedAsync(Software software)
    {
        ApplyFilter();
        if (!string.IsNullOrWhiteSpace(_repositoryPath) && !_catalogLoadFailed)
            await _catalogService.SaveAsync(_repositoryPath);
    }

    private async void NotificationChangedAsync(Software software)
    {
        if (!string.IsNullOrWhiteSpace(_repositoryPath) && !_catalogLoadFailed)
            await _catalogService.SaveAsync(_repositoryPath);
    }

    private bool MatchesSelectedStatus(Software software)
    {
        var selection = (ViewFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return selection switch { "Up to date" => software.Status == SoftwareStatus.UpToDate, "Update available" => software.Status == SoftwareStatus.UpdateAvailable, "Error" => software.Status == SoftwareStatus.Error, _ => true };
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e) => ShowProfile(new Software(), true);

    private void ShowProfile(Software software) => ShowProfile(software, false);
    private void ShowVersions(Software software) => RightPanelContent.Content = new VersionManagerView(software, _repositoryPath);

    private void ShowProfile(Software software, bool isNew)
    {
        var view = new ProfileConfigView(software, isNew);
        view.SaveRequested += profile => _ = SaveProfileAsync(software, profile, isNew);
        view.TestRequested += profile => _ = TestProfileAsync(profile);
        view.DeleteRequested += _ => DeleteSoftwareAsync(software);
        view.CancelRequested += ShowPanelPlaceholder;
        RightPanelContent.Content = view;
    }

    private async Task SaveProfileAsync(Software original, Software profile, bool isNew)
    {
        if (!EnsureRepositorySelected()) return;
        if (!EnsureCatalogCanBeSaved()) return;
        if (isNew) SoftwareItems.Add(profile);
        else
        {
            UpdateService.InvalidateCheckCache(original);
            original.ApplyFrom(profile);
        }
        await _catalogService.SaveAsync(_repositoryPath);
        NotifyCount();
        ShowProfile(isNew ? profile : original);
    }

    private async Task TestProfileAsync(Software profile)
    {
        try
        {
            if (profile.Source.Kind == SoftwareSourceKind.None)
            {
                ThemedMessageBox.Show("Method: None\nThis profile is managed manually and has no detection source.", "Source test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var result = await _updateService.CheckAsync(profile);
            var text = result is null
                ? $"Method: {profile.Source}\nNo version was detected. Review the configured source and patterns."
                : profile.Source.Kind == SoftwareSourceKind.WinGet
                    ? $"Method: {result.DetectionMethod ?? "WinGet"}\nDetected version: {result.Version}\nPackage: {profile.Source.PackageId}\nDownload: Managed by WinGet\n\nSource test successful."
                    : profile.Source.Kind == SoftwareSourceKind.Website
                        ? $"Method: {result.DetectionMethod ?? "Website"}\nDetected version: {result.Version}\nDownload URL: {result.DownloadUrl ?? "not detected"}\n\nSource test successful."
                        : $"Method: {result.DetectionMethod ?? profile.Source.ToString()}\nDetected version: {result.Version}\nSelected asset: {result.SelectedAssetFileName ?? "not specified"}\nDownload URL: {result.DownloadUrl ?? "not provided by this method"}";
            ThemedMessageBox.Show(text, "Source test", MessageBoxButton.OK, result is null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "Source test failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void DeleteSoftwareAsync(Software software)
    {
        if (!EnsureCatalogCanBeSaved()) return;
        if (ThemedMessageBox.Show($"Delete the profile '{software.DisplayName}'? Package ZIP files will be kept.", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SoftwareItems.Remove(software);
        if (!string.IsNullOrWhiteSpace(_repositoryPath)) await _catalogService.SaveAsync(_repositoryPath);
        NotifyCount();
        ShowPanelPlaceholder();
    }

    private void OnlineCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new OnlineCatalogView
        {
            LocalProfileExists = onlineProfile => FindMatchingLocalProfile(onlineProfile) is not null
        };
        view.AddRequested += AddOnlineProfilesAsync;
        view.CompareRequested += CompareOnlineProfile;
        RightPanelContent.Content = view;
    }

    private void CompareOnlineProfile(Software onlineProfile)
    {
        var localProfile = FindMatchingLocalProfile(onlineProfile);
        var comparison = new ProfileComparisonView(localProfile, onlineProfile);
        var window = new ThemedWindow
        {
            Title = $"Compare {onlineProfile.DisplayName}",
            Owner = Window.GetWindow(this),
            Content = comparison,
            Width = 980,
            Height = 720,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"),
            Padding = new Thickness(16)
        };

        comparison.CancelRequested += window.Close;
        comparison.ReplaceRequested += async () =>
        {
            if (localProfile is null)
            {
                ThemedMessageBox.Show("This software is not in the local catalogue. Use Add selected profiles to add it.", "Compare profiles", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!EnsureCatalogCanBeSaved()) return;
            var localVersion = localProfile.LocalVersion;
            var packageFileName = localProfile.PackageFileName;
            var isFavorite = localProfile.IsFavorite;
            var isSelected = localProfile.IsSelected;
            var updateNotificationEnabled = localProfile.IsUpdateNotificationEnabled;

            UpdateService.InvalidateCheckCache(localProfile);
            localProfile.ApplyFrom(onlineProfile);
            localProfile.LocalVersion = localVersion;
            localProfile.PackageFileName = packageFileName;
            localProfile.IsFavorite = isFavorite;
            localProfile.IsSelected = isSelected;
            localProfile.IsUpdateNotificationEnabled = updateNotificationEnabled;

            await _catalogService.SaveAsync(_repositoryPath);
            CollectionViewSource.GetDefaultView(SoftwareItems).Refresh();
            window.Close();
            ThemedMessageBox.Show("The local profile was replaced with the online configuration. Stored package ZIP files were not changed.", "Compare profiles", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        window.ShowDialog();
    }

    private Software? FindMatchingLocalProfile(Software onlineProfile)
    {
        if (!string.IsNullOrWhiteSpace(onlineProfile.SoftwareId))
        {
            var byId = SoftwareItems.FirstOrDefault(local =>
                string.Equals(local.SoftwareId, onlineProfile.SoftwareId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }

        return SoftwareItems.FirstOrDefault(local =>
            string.Equals(local.Name, onlineProfile.Name, StringComparison.OrdinalIgnoreCase));
    }

    private async void AddOnlineProfilesAsync(IReadOnlyList<Software> profiles)
    {
        if (!EnsureRepositorySelected()) return;
        if (!EnsureCatalogCanBeSaved()) return;
        var added = 0;
        foreach (var profile in profiles.Where(profile => SoftwareItems.All(existing => !SameProfile(existing, profile))))
        {
            SoftwareItems.Add(profile.Copy());
            added++;
        }
        await _catalogService.SaveAsync(_repositoryPath);
        NotifyCount();
        ThemedMessageBox.Show($"{added} profile(s) added. {profiles.Count - added} duplicate(s) skipped.", "Online Catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool SameProfile(Software left, Software right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        left.Source.Kind == right.Source.Kind;

    private void CheckButton_Click(object sender, RoutedEventArgs e) => OpenScopeMenu(CheckButton, items => _ = RunCheckAsync(items));
    private void UpdateButton_Click(object sender, RoutedEventArgs e) => OpenScopeMenu(UpdateButton, items => _ = RunUpdateAsync(items));

    private void OpenScopeMenu(Button owner, Action<IReadOnlyList<Software>> action)
    {
        var menu = new ContextMenu();
        var all = new MenuItem { Header = "All software" };
        all.Click += (_, _) => action(SoftwareItems.ToList());
        var selected = new MenuItem { Header = "Selected software" };
        selected.Click += (_, _) => action(SoftwareItems.Where(item => item.IsSelected).ToList());
        menu.Items.Add(all);
        menu.Items.Add(selected);
        owner.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async Task RunCheckAsync(IReadOnlyList<Software> items)
    {
        if (items.Count == 0) { ShowNothingSelected(); return; }
        await RunInlineOperationAsync("Checking software", async (state, token) =>
        {
            state.Message = $"Checking {items.Count} software profiles";
            state.IsIndeterminate = false;
            var progress = new Progress<UpdateService.CheckProgress>(step =>
            {
                state.Message = $"Checked {step.SoftwareName} ({step.Completed}/{step.Total})";
                state.Percentage = step.Completed * 100 / step.Total;
            });
            await _updateService.CheckManyAsync(items, progress, cancellationToken: token);
        });
        if (!string.IsNullOrWhiteSpace(_repositoryPath)) await _catalogService.SaveAsync(_repositoryPath);
        NotifyCount();
    }

    private async Task RunUpdateAsync(IReadOnlyList<Software> items)
    {
        if (!EnsureRepositorySelected()) return;
        if (items.Count == 0) { ShowNothingSelected(); return; }
        await RunUpdateOperationAsync(items);
        await _catalogService.SaveAsync(_repositoryPath);
    }

    private async Task RunInlineOperationAsync(string message, Func<OperationProgress, CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        var state = new OperationProgress { Message = message, Percentage = 0, CanCancel = true };
        var panel = new ProgressPanel { DataContext = state };
        panel.CancelRequested += (_, _) => _operationCancellation.Cancel();
        ProgressContent.Content = panel;
        ProgressContent.Visibility = Visibility.Visible;
        CheckButton.IsEnabled = UpdateButton.IsEnabled = RefreshButton.IsEnabled = false;
        try
        {
            await operation(state, _operationCancellation.Token);
        }
        catch (OperationCanceledException) { state.Message = "Operation cancelled"; }
        catch (Exception ex) { state.Message = "Operation failed"; _ = _logService.WriteAsync("Operation failed", ex); ThemedMessageBox.Show(ex.Message, "Library Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            state.IsIndeterminate = false;
            state.CanCancel = false;
            CheckButton.IsEnabled = UpdateButton.IsEnabled = RefreshButton.IsEnabled = true;
            ProgressContent.Visibility = Visibility.Collapsed;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private async Task RunUpdateOperationAsync(IReadOnlyList<Software> items)
    {
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        var state = new OperationProgress { Message = "Preparing updates", Percentage = 0, CanCancel = true };
        var detailsWindow = new UpdateProgressWindow(Window.GetWindow(this), () => _operationCancellation?.Cancel()) { DataContext = state };
        CheckButton.IsEnabled = UpdateButton.IsEnabled = RefreshButton.IsEnabled = false;
        detailsWindow.Show();
        try
        {
            var failedUpdates = new List<string>();
            for (var index = 0; index < items.Count; index++)
            {
                var itemIndex = index;
                state.CurrentSoftware = items[index].DisplayName;
                AddProgressLog(state, $"Starting {state.CurrentSoftware} ({index + 1}/{items.Count})");
                var currentStep = string.Empty;
                var currentLogIndex = -1;
                var lastPersistedProgress = string.Empty;
                var progress = new Progress<OperationProgress>(step =>
                {
                    state.Message = step.Message;
                    state.Percentage = (itemIndex * 100 + step.Percentage) / items.Count;
                    state.CanCancel = step.CanCancel;
                    state.IsIndeterminate = step.IsIndeterminate;

                    // Transfer progress can report hundreds of byte/MB updates. Keep one
                    // dynamic line per update step instead of appending one log line per report.
                    var stepKey = GetProgressStepKey(step.Message);
                    var logLine = $"{DateTime.Now:T} {step.Message}";
                    if (!string.Equals(currentStep, stepKey, StringComparison.Ordinal) || currentLogIndex < 0)
                    {
                        currentStep = stepKey;
                        state.LogEntries.Add(logLine);
                        currentLogIndex = state.LogEntries.Count - 1;
                    }
                    else
                    {
                        state.LogEntries[currentLogIndex] = logLine;
                    }

                    if (ShouldPersistProgressMessage(step.Message) &&
                        !string.Equals(lastPersistedProgress, step.Message, StringComparison.Ordinal))
                    {
                        lastPersistedProgress = step.Message;
                        _ = _logService.WriteAsync($"{state.CurrentSoftware} — {step.Message}");
                    }
                });
                try
                {
                    await _updateService.UpdateWithProgressAsync(items[index], _repositoryPath, progress, _operationCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    items[index].Status = SoftwareStatus.Error;
                    failedUpdates.Add(items[index].DisplayName);
                    var failure = $"Failed to update {items[index].DisplayName}: {ex.Message}";
                    AddProgressLog(state, failure);
                    _ = _logService.WriteAsync(failure, ex);
                }
            }
            detailsWindow.Complete(failedUpdates.Count == 0
                ? "Operation completed successfully."
                : $"Operation completed with {failedUpdates.Count} failed update(s). See the log for details.");
        }
        catch (OperationCanceledException) { AddProgressLog(state, "Operation cancelled"); detailsWindow.Complete("Operation cancelled."); }
        catch (Exception ex) { AddProgressLog(state, ex.Message); _ = _logService.WriteAsync("Operation failed", ex); detailsWindow.Complete("Operation failed."); ThemedMessageBox.Show(ex.Message, "Library Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            state.IsIndeterminate = false;
            state.CanCancel = false;
            CheckButton.IsEnabled = UpdateButton.IsEnabled = RefreshButton.IsEnabled = true;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private bool EnsureRepositorySelected()
    {
        if (!string.IsNullOrWhiteSpace(_repositoryPath)) return true;
        ThemedMessageBox.Show("Choose a repository folder in Settings before saving or updating profiles.", "Library Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private bool EnsureCatalogCanBeSaved()
    {
        if (!_catalogLoadFailed) return true;
        ThemedMessageBox.Show("The local catalogue could not be loaded. Fix or import a valid catalogue before saving profiles so it is not overwritten.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void ShowNothingSelected() => ThemedMessageBox.Show("No software profiles are selected.", "Library Manager", MessageBoxButton.OK, MessageBoxImage.Information);
    private static string GetProgressStepKey(string message)
    {
        var separator = message.IndexOf(" — ", StringComparison.Ordinal);
        if (separator <= 0 || !message.StartsWith("Step ", StringComparison.Ordinal))
            return message;

        var step = message[..separator];

        // Step 3 can download several Additional Files. Keep one dynamic line for
        // each file while its byte counter changes instead of merging all files.
        if (step.StartsWith("Step 3/", StringComparison.Ordinal))
        {
            const string downloadingMarker = " — Downloading ";
            var downloading = message.IndexOf(downloadingMarker, StringComparison.Ordinal);
            if (downloading >= 0)
            {
                var fileStart = downloading + downloadingMarker.Length;
                var fileEnd = message.IndexOf(" — ", fileStart, StringComparison.Ordinal);
                var fileName = fileEnd > fileStart ? message[fileStart..fileEnd] : message[fileStart..];
                if (!string.IsNullOrWhiteSpace(fileName))
                    return $"{step}|{fileName}";
            }
        }

        return step;
    }

    private static bool ShouldPersistProgressMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        return message.Contains("Connecting to ", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Server response received", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("First data received", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporary folder", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Compressing", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("source folder", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Updated ", StringComparison.OrdinalIgnoreCase);
    }

    private void AddProgressLog(OperationProgress state, string message)
    {
        var entry = $"{DateTime.Now:T} {message}";
        state.LogEntries.Add(entry);
        _ = _logService.WriteAsync(message);
    }
    private void NotifyCount()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SoftwareCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateAvailableCount)));
    }
    private void ShowPanelPlaceholder() => RightPanelContent.Content = new TextBlock { Text = "Select a software profile or create a new one.", TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private void HidePanelButton_Click(object sender, RoutedEventArgs e) { _previousRightPanelWidth = RightPanelColumn.Width; RightPanelColumn.MinWidth = 0; RightPanelColumn.Width = new GridLength(0); ShowPanelButton.Visibility = Visibility.Visible; }
    private void ShowPanelButton_Click(object sender, RoutedEventArgs e) { RightPanelColumn.MinWidth = 390; RightPanelColumn.Width = _previousRightPanelWidth; ShowPanelButton.Visibility = Visibility.Collapsed; }
}

