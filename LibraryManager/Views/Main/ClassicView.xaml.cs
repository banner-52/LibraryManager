using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LibraryManager.Models;
using LibraryManager.Services;
using LibraryManager.Services.Detection;
using LibraryManager.Views.Controls;

namespace LibraryManager.Views.Main;

public partial class ClassicView : UserControl
{
    private readonly CatalogService _catalogService = new();
    private readonly SettingsService _settingsService = new();
    private readonly UpdateService _updateService;
    private CancellationTokenSource? _operationCancellation;
    private string _repositoryPath = string.Empty;
    private bool _catalogLoadFailed;

    public ClassicView()
    {
        InitializeComponent();
        _updateService = new UpdateService(new GitHubDetection(AppServices.GitHubAuthentication, AppServices.Requests), new WebsiteDetection(renderedPageReader: BrowserPageReader.ReadHtmlAsync), new WingetDetection(), new DownloadService(), new PackageService());
        DataContext = this;
        Loaded += ClassicView_Loaded;
        ShowList();
    }

    public ObservableCollection<Software> SoftwareItems => _catalogService.SoftwareItems;
    private async void ClassicView_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
        var settings = await _settingsService.LoadAsync();
        if (settings.CheckForUpdatesAtStartup && SoftwareItems.Count > 0) await RunCheckAsync(SoftwareItems.ToList());
    }
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        var settings = await _settingsService.LoadAsync();
        _repositoryPath = settings.RepositoryPath;
        try
        {
            await _catalogService.LoadAsync(_repositoryPath);
            await Task.Run(() => _catalogService.RefreshStoredVersions(_repositoryPath));
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
    }

    private void ShowList()
    {
        var list = new SoftwareList { DataContext = this };
        list.EditRequested += software => ShowProfileDialog(software, false);
        list.UpdateRequested += software => _ = RunUpdateAsync([software]);
        list.DeleteRequested += DeleteSoftwareAsync;
        list.ManageVersionsRequested += ShowVersionsDialog;
        list.FavoriteChanged += PersistProfilePreferenceAsync;
        list.NotificationChanged += PersistProfilePreferenceAsync;
        SoftwareListContent.Content = list;
    }

    private async void PersistProfilePreferenceAsync(Software software)
    {
        if (!string.IsNullOrWhiteSpace(_repositoryPath) && !_catalogLoadFailed)
            await _catalogService.SaveAsync(_repositoryPath);
    }

    private void AddSoftwareButton_Click(object sender, RoutedEventArgs e) => ShowProfileDialog(new Software(), true);

    private void ShowVersionsDialog(Software software)
    {
        new ThemedWindow
        {
            Title = $"Stored versions — {software.DisplayName}", Owner = Window.GetWindow(this),
            Content = new VersionManagerView(software, _repositoryPath), Width = 760, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"), Padding = new Thickness(16)
        }.ShowDialog();
    }

    private void ShowProfileDialog(Software software, bool isNew)
    {
        var profile = new ProfileConfigView(software, isNew);
        var window = new ThemedWindow
        {
            Title = isNew ? "New Profile" : $"Configure {software.DisplayName}",
            Owner = Window.GetWindow(this),
            Content = profile,
            Width = 720,
            Height = 760,
            MinWidth = 620,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"),
            Padding = new Thickness(16)
        };
        profile.CancelRequested += window.Close;
        profile.TestRequested += candidate => _ = TestProfileAsync(candidate);
        profile.DeleteRequested += _ => { window.Close(); DeleteSoftwareAsync(software); };
        profile.SaveRequested += candidate => _ = SaveProfileAsync(software, candidate, isNew, window);
        window.ShowDialog();
    }

    private async Task SaveProfileAsync(Software original, Software profile, bool isNew, Window window)
    {
        if (!EnsureRepositorySelected()) return;
        if (!EnsureCatalogCanBeSaved()) return;
        if (isNew) SoftwareItems.Add(profile); else { UpdateService.InvalidateCheckCache(original); original.ApplyFrom(profile); }
        await _catalogService.SaveAsync(_repositoryPath);
        window.Close();
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
    }

    private void OnlineCatalogueButton_Click(object sender, RoutedEventArgs e)
    {
        var catalogue = new OnlineCatalogView();
        catalogue.AddRequested += AddOnlineProfilesAsync;
        new ThemedWindow
        {
            Title = "Online Catalogue", Owner = Window.GetWindow(this), Content = catalogue,
            Width = 760, Height = 720, MinWidth = 620, MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"), Padding = new Thickness(16)
        }.ShowDialog();
    }

    private async void AddOnlineProfilesAsync(IReadOnlyList<Software> profiles)
    {
        if (!EnsureRepositorySelected()) return;
        if (!EnsureCatalogCanBeSaved()) return;
        var added = 0;
        foreach (var profile in profiles.Where(profile => SoftwareItems.All(existing =>
                     !string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase) ||
                     existing.Source.Kind != profile.Source.Kind)))
        {
            SoftwareItems.Add(profile.Copy());
            added++;
        }
        await _catalogService.SaveAsync(_repositoryPath);
        ThemedMessageBox.Show($"{added} profile(s) added. {profiles.Count - added} duplicate(s) skipped.", "Online Catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (SearchTextBox is null || StatusFilterComboBox is null) return;
        var search = SearchTextBox.Text.Trim();
        var selectedStatus = (StatusFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        CollectionViewSource.GetDefaultView(SoftwareItems).Filter = item => item is Software software &&
            (string.IsNullOrWhiteSpace(search) || software.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (selectedStatus switch { "Up to date" => software.Status == SoftwareStatus.UpToDate, "Update available" => software.Status == SoftwareStatus.UpdateAvailable, "Error" => software.Status == SoftwareStatus.Error, _ => true });
    }

    private void CheckButton_Click(object sender, RoutedEventArgs e) => OpenScopeMenu(CheckButton, items => _ = RunCheckAsync(items));
    private void UpdateButton_Click(object sender, RoutedEventArgs e) => OpenScopeMenu(UpdateButton, items => _ = RunUpdateAsync(items));

    private void OpenScopeMenu(Button owner, Action<IReadOnlyList<Software>> action)
    {
        var menu = new ContextMenu();
        var all = new MenuItem { Header = "All software" };
        all.Click += (_, _) => action(SoftwareItems.ToList());
        var selected = new MenuItem { Header = "Selected software" };
        selected.Click += (_, _) => action(SoftwareItems.Where(item => item.IsSelected).ToList());
        menu.Items.Add(all); menu.Items.Add(selected);
        owner.ContextMenu = menu; menu.IsOpen = true;
    }

    private async Task RunCheckAsync(IReadOnlyList<Software> items)
    {
        if (items.Count == 0) { ShowNothingSelected(); return; }
        await RunParallelCheckAsync(items);
        if (!string.IsNullOrWhiteSpace(_repositoryPath)) await _catalogService.SaveAsync(_repositoryPath);
    }

    private async Task RunParallelCheckAsync(IReadOnlyList<Software> items)
    {
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        var state = new OperationProgress { Message = $"Checking {items.Count} software profiles", CanCancel = true };
        var panel = new ProgressPanel { DataContext = state };
        panel.CancelRequested += (_, _) => _operationCancellation.Cancel();
        ProgressContent.Content = panel;
        CheckButton.IsEnabled = UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<UpdateService.CheckProgress>(step =>
            {
                state.Message = $"Checked {step.SoftwareName} ({step.Completed}/{step.Total})";
                state.Percentage = step.Completed * 100 / step.Total;
            });
            await _updateService.CheckManyAsync(items, progress, cancellationToken: _operationCancellation.Token);
        }
        catch (OperationCanceledException) { state.Message = "Operation cancelled"; }
        catch (Exception ex) { state.Message = "Operation failed"; ThemedMessageBox.Show(ex.Message, "Library Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            state.Percentage = 100;
            state.CanCancel = false;
            CheckButton.IsEnabled = UpdateButton.IsEnabled = true;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private async Task RunUpdateAsync(IReadOnlyList<Software> items)
    {
        if (!EnsureRepositorySelected()) return;
        if (items.Count == 0) { ShowNothingSelected(); return; }
        await RunUpdateOperationAsync(items);
        await _catalogService.SaveAsync(_repositoryPath);
    }

    private async Task RunUpdateOperationAsync(IReadOnlyList<Software> items)
    {
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        var state = new OperationProgress { Message = "Preparing updates", CanCancel = true };
        var detailsWindow = new UpdateProgressWindow(Window.GetWindow(this), () => _operationCancellation?.Cancel()) { DataContext = state };
        CheckButton.IsEnabled = UpdateButton.IsEnabled = false;
        detailsWindow.Show();
        try
        {
            var failedUpdates = new List<string>();
            for (var index = 0; index < items.Count; index++)
            {
                var itemIndex = index;
                state.CurrentSoftware = items[index].DisplayName;
                AddProgressLog(state, $"Starting {state.CurrentSoftware} ({index + 1}/{items.Count})");
                var lastMessage = string.Empty;
                var progress = new Progress<OperationProgress>(step =>
                {
                    state.Message = step.Message;
                    state.Percentage = (itemIndex * 100 + step.Percentage) / items.Count;
                    state.CanCancel = step.CanCancel;
                    state.IsIndeterminate = step.IsIndeterminate;
                    if (!string.Equals(lastMessage, step.Message, StringComparison.Ordinal))
                    {
                        lastMessage = step.Message;
                        AddProgressLog(state, step.Message);
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
                    AddProgressLog(state, $"Failed to update {items[index].DisplayName}: {ex.Message}");
                }
            }
            detailsWindow.Complete(failedUpdates.Count == 0
                ? "Operation completed successfully."
                : $"Operation completed with {failedUpdates.Count} failed update(s). See the log for details.");
        }
        catch (OperationCanceledException) { AddProgressLog(state, "Operation cancelled"); detailsWindow.Complete("Operation cancelled."); }
        catch (Exception ex) { AddProgressLog(state, ex.Message); detailsWindow.Complete("Operation failed."); ThemedMessageBox.Show(ex.Message, "Library Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            state.IsIndeterminate = false;
            state.CanCancel = false;
            CheckButton.IsEnabled = UpdateButton.IsEnabled = true;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private static void AddProgressLog(OperationProgress state, string message) =>
        state.LogEntries.Add($"{DateTime.Now:T} {message}");

    private async Task RunOperationAsync(IReadOnlyList<Software> items, Func<Software, IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        var state = new OperationProgress { Message = "Starting", CanCancel = true };
        var panel = new ProgressPanel { DataContext = state };
        panel.CancelRequested += (_, _) => _operationCancellation.Cancel();
        ProgressContent.Content = panel;
        CheckButton.IsEnabled = UpdateButton.IsEnabled = false;
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var itemIndex = index;
                var progress = new Progress<OperationProgress>(step => { state.Message = step.Message; state.Percentage = (itemIndex * 100 + step.Percentage) / items.Count; state.CanCancel = step.CanCancel; });
                await operation(items[index], progress, _operationCancellation.Token);
            }
        }
        catch (OperationCanceledException) { state.Message = "Operation cancelled"; }
        catch (Exception ex) { state.Message = "Operation failed"; ThemedMessageBox.Show(ex.Message, "Library Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            state.Percentage = 100; state.CanCancel = false;
            CheckButton.IsEnabled = UpdateButton.IsEnabled = true;
            _operationCancellation.Dispose(); _operationCancellation = null;
        }
    }

    private bool EnsureRepositorySelected()
    {
        if (!string.IsNullOrWhiteSpace(_repositoryPath)) return true;
        ThemedMessageBox.Show("Choose a repository folder in Settings first.", "Library Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private bool EnsureCatalogCanBeSaved()
    {
        if (!_catalogLoadFailed) return true;
        ThemedMessageBox.Show("The existing catalog.json could not be loaded. Fix or move that file before saving profiles so it is not overwritten.", "Local catalogue", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void ShowNothingSelected() => ThemedMessageBox.Show("No software profiles are selected.", "Library Manager", MessageBoxButton.OK, MessageBoxImage.Information);
}

