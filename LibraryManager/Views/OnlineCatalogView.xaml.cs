using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using LibraryManager.Models;
using LibraryManager.Services;

namespace LibraryManager.Views;

public partial class OnlineCatalogView : UserControl
{
    private static readonly BitmapImage DefaultIcon = new(new Uri(Software.DefaultIconPath));
    private readonly OnlineCatalogService _catalogueService = new();
    private readonly SettingsService _settingsService = new();
    private readonly ObservableCollection<OnlineCatalogGroup> _groups = [];
    private CancellationTokenSource? _loadCancellation;

    public OnlineCatalogView()
    {
        InitializeComponent();
        CatalogueItems.ItemsSource = _groups;
        Loaded += OnlineCatalogView_Loaded;
    }

    public event Action<IReadOnlyList<Software>>? AddRequested;
    public event Action<Software>? CompareRequested;
    public Func<Software, bool>? LocalProfileExists { get; set; }

    private void SoftwareIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image) image.Source = DefaultIcon;
        e.Handled = true;
    }

    private async void OnlineCatalogView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_groups.Count == 0) await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RequestSoftwareButton_Click(object sender, RoutedEventArgs e)
    {
        if ((await _settingsService.LoadAsync()).UseCustomOnlineCatalogue)
        {
            ThemedMessageBox.Show("Software requests are unavailable when using a custom online catalogue.", "Request software", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AppServices.GitHubAuthentication.Account.IsConnected)
        {
            ThemedMessageBox.Show("Connect a GitHub account in Settings before requesting software.", "Request software", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SoftwareRequestWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var result = await _catalogueService.SubmitSoftwareRequestAsync(dialog.SoftwareName, dialog.SourceUrl, dialog.Details);
            ThemedMessageBox.Show($"Software request #{result.Number} submitted successfully.\n\n{result.Url}", "Request software", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Software request failed: {ex.Message}", "Request software", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        StatusTextBlock.Text = "Loading online catalogue...";
        try
        {
            var settings = await _settingsService.LoadAsync(_loadCancellation.Token);
            RequestSoftwareButton.Visibility = settings.UseCustomOnlineCatalogue ? Visibility.Collapsed : Visibility.Visible;
            if (!settings.EnableOnlineCatalogue)
            {
                _groups.Clear();
                StatusTextBlock.Text = "The online catalogue is disabled in Settings.";
                return;
            }
            var profiles = await _catalogueService.LoadAsync(settings.EffectiveOnlineCatalogueUrl, _loadCancellation.Token);
            _groups.Clear();
            foreach (var group in profiles.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
                _groups.Add(new OnlineCatalogGroup(
                    group.Key,
                    group.FirstOrDefault()?.IconPath,
                    group.Select(profile => new OnlineCatalogOption(profile)),
                    group.Any(profile => LocalProfileExists?.Invoke(profile) == true)));
            StatusTextBlock.Text = $"{profiles.Count} profile method(s) available.";
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _groups.Clear();
            StatusTextBlock.Text = $"Unable to load the online catalogue: {ex.Message}";
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchTextBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_groups).Filter = item => item is OnlineCatalogGroup group &&
            (string.IsNullOrWhiteSpace(search) || group.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || group.Options.Any(option => option.Label.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OnlineCatalogGroup group }) return;

        if (group.Options.Count == 1)
        {
            CompareRequested?.Invoke(group.Options[0].Profile.Copy());
            return;
        }

        var selected = group.Options.Where(option => option.IsSelected).ToList();
        if (selected.Count != 1)
        {
            ThemedMessageBox.Show("Select exactly one profile method for this software before comparing it.", "Compare profiles", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CompareRequested?.Invoke(selected[0].Profile.Copy());
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _groups.SelectMany(group => group.Options).Where(option => option.IsSelected).Select(option => option.Profile.Copy()).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageBox.Show("Select at least one profile method.", "Online Catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddRequested?.Invoke(selected);
        StatusTextBlock.Text = $"{selected.Count} profile(s) added to your list.";
    }
}

public sealed class OnlineCatalogGroup
{
    public OnlineCatalogGroup(string name, string? iconPath, IEnumerable<OnlineCatalogOption> options, bool hasLocalProfile)
    {
        Name = name;
        Icon = string.IsNullOrWhiteSpace(iconPath) ? Software.DefaultIconPath : iconPath;
        Options = new ObservableCollection<OnlineCatalogOption>(options);
        HasLocalProfile = hasLocalProfile;
    }
    public string Name { get; }
    public string Icon { get; }
    public ObservableCollection<OnlineCatalogOption> Options { get; }
    public bool HasLocalProfile { get; }
    public string ActionLabel => HasLocalProfile ? "Compare" : "See Profile";
}

public sealed class OnlineCatalogOption
{
    public OnlineCatalogOption(Software profile) => Profile = profile;
    public Software Profile { get; }
    public bool IsSelected { get; set; }
    public string Label => Profile.Source.ToString();
    public string Details => Profile.Source.Kind == SoftwareSourceKind.WinGet ? Profile.Source.PackageId ?? string.Empty : Profile.Source.Url ?? string.Empty;
}

