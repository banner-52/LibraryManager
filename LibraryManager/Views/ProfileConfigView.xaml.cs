using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LibraryManager.Models;
using LibraryManager.Services;
using LibraryManager.Services.Detection;

namespace LibraryManager.Views;

public partial class ProfileConfigView : UserControl
{
    private readonly ProfileValidationService _validation = new();
    private readonly SilentInstallService _silentInstall = new();
    private readonly Software _workingCopy;
    private readonly bool _isNew;

    public ProfileConfigView() : this(new Software(), true) { }

    public ProfileConfigView(Software software, bool isNew = false)
    {
        InitializeComponent();
        _workingCopy = software.Copy();
        _isNew = isNew;
        TitleTextBlock.Text = isNew ? "New Profile" : "Profile Config";
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        AdditionalFiles = new ObservableCollection<AdditionalFile>(_workingCopy.AdditionalFiles.Select(file => file.Copy()));
        DataContext = this;
        PopulateFields();
        Loaded += async (_, _) =>
        {
            var settings = await new SettingsService().LoadAsync();
            SubmitToOnlineCatalogueButton.Visibility = settings.UseCustomOnlineCatalogue ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    public event Action<Software>? SaveRequested;
    public event Action<Software>? TestRequested;
    public event Action<Software>? DeleteRequested;
    public event Action? CancelRequested;
    public ObservableCollection<AdditionalFile> AdditionalFiles { get; }

    private void PopulateFields()
    {
        NameTextBox.Text = _workingCopy.Name;
        PublisherTextBox.Text = _workingCopy.Publisher ?? string.Empty;
        DescriptionTextBox.Text = _workingCopy.Description ?? string.Empty;
        IconTextBox.Text = _workingCopy.IconPath ?? string.Empty;
        SourceUrlTextBox.Text = _workingCopy.Source.Url ?? string.Empty;
        PackageIdTextBox.Text = _workingCopy.Source.PackageId ?? string.Empty;
        VersionPatternTextBox.Text = _workingCopy.Source.VersionPattern ?? string.Empty;
        DownloadPatternTextBox.Text = _workingCopy.Source.DownloadPattern ?? string.Empty;
        GitHubAssetPatternTextBox.Text = _workingCopy.Source.DownloadPattern ?? string.Empty;
        InstallCommandTextBox.Text = _workingCopy.SilentInstallCommand ?? string.Empty;
        UninstallCommandTextBox.Text = _workingCopy.SilentUninstallCommand ?? string.Empty;
        InstallerFileNameTextBox.Text = _workingCopy.InstallerFileName ?? string.Empty;
        UpdateNotificationCheckBox.IsChecked = _workingCopy.IsUpdateNotificationEnabled;
        MethodComboBox.SelectedIndex = _workingCopy.Source.Kind switch { SoftwareSourceKind.GitHubReleases => 1, SoftwareSourceKind.WinGet => 2, SoftwareSourceKind.None => 3, _ => 0 };
        UpdateSourceFieldVisibility();
    }

    private Software BuildProfile()
    {
        _workingCopy.Name = NameTextBox.Text.Trim();
        _workingCopy.Publisher = NullIfEmpty(PublisherTextBox.Text);
        _workingCopy.Description = NullIfEmpty(DescriptionTextBox.Text);
        _workingCopy.IconPath = NullIfEmpty(IconTextBox.Text);
        var kind = GetSourceKind();
        var source = _workingCopy.Source.Copy();
        source.Kind = kind;
        source.Url = NullIfEmpty(SourceUrlTextBox.Text);
        source.PackageId = NullIfEmpty(PackageIdTextBox.Text);
        source.VersionPattern = NullIfEmpty(VersionPatternTextBox.Text);
        source.DownloadPattern = NullIfEmpty(kind == SoftwareSourceKind.GitHubReleases ? GitHubAssetPatternTextBox.Text : DownloadPatternTextBox.Text);
        _workingCopy.Source = source;
        _silentInstall.SetCommands(_workingCopy, NullIfEmpty(InstallCommandTextBox.Text), NullIfEmpty(UninstallCommandTextBox.Text));
        _workingCopy.InstallerFileName = NullIfEmpty(InstallerFileNameTextBox.Text);
        _workingCopy.IsUpdateNotificationEnabled = UpdateNotificationCheckBox.IsChecked == true;
        _workingCopy.Source.InstallerEntry = _workingCopy.InstallerFileName;
        _workingCopy.AdditionalFiles = AdditionalFiles.Select(file => file.Copy()).ToList();
        return _workingCopy;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        var errors = _validation.Validate(profile);
        ValidationTextBlock.Text = string.Join(Environment.NewLine, errors);
        if (errors.Count == 0) SaveRequested?.Invoke(profile);
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        var errors = _validation.Validate(profile);
        ValidationTextBlock.Text = string.Join(Environment.NewLine, errors);
        if (errors.Count == 0) TestRequested?.Invoke(profile);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(_workingCopy);
    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = await new SettingsService().LoadAsync();
        if (settings.UseCustomOnlineCatalogue)
        {
            ValidationTextBlock.Text = "Submissions are disabled when using a custom online catalogue.";
            return;
        }
        var profile = BuildProfile();
        var errors = _validation.Validate(profile);
        ValidationTextBlock.Text = string.Join(Environment.NewLine, errors);
        if (errors.Count > 0) return;
        try
        {
            var service = new OnlineCatalogService();
            var plan = await service.PrepareSubmissionAsync(profile);
            if (ThemedMessageBox.Show(plan.Preview, "Confirm online catalogue submission", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            var result = await service.SubmitAsync(plan);
            ValidationTextBlock.Text = result.UpdatedExistingIssue ? $"Updated GitHub issue #{result.IssueNumber}." : $"Created GitHub issue #{result.IssueNumber}.";
        }
        catch (Exception ex) { ValidationTextBlock.Text = $"Online catalogue submission failed: {ex.Message}"; }
    }
    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private async void DetectFaviconButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(SourceUrlTextBox.Text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            ValidationTextBlock.Text = "Enter a valid HTTPS source URL before detecting a favicon.";
            return;
        }
        try { IconTextBox.Text = await new FaviconService().FindAsync(uri.AbsoluteUri) ?? string.Empty; }
        catch (Exception ex) { ValidationTextBlock.Text = $"Favicon detection failed: {ex.Message}"; }
    }
    private async void FindCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { SilentCommandsStatusTextBlock.Text = "Enter the software name first."; return; }
        FindCommandsButton.IsEnabled = false;
        SilentCommandsStatusTextBlock.Text = "Searching SilentInstallHQ and reading the best matching article...";
        try
        {
            var result = await new SilentInstallHqService().FindAsync(name);
            InstallCommandTextBox.Text = result.InstallCommand;
            UninstallCommandTextBox.Text = result.UninstallCommand;
            SilentCommandsStatusTextBlock.Text = $"Commands found in: {result.ArticleTitle}. Review them before saving or using them.";
        }
        catch (Exception ex) { SilentCommandsStatusTextBlock.Text = $"Command lookup failed: {ex.Message}"; }
        finally { FindCommandsButton.IsEnabled = true; }
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e) => AdditionalFiles.Add(new AdditionalFile { Name = "Additional files", Required = false });

    private void GenerateAdditionalButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdditionalFilesDataGrid.SelectedItem is not AdditionalFile selected)
        {
            ValidationTextBlock.Text = "Select an additional file source first.";
            return;
        }

        var dialog = new RegexGeneratorWindow(string.Empty, selected.FilePattern) { Owner = Window.GetWindow(this) };
        if (!string.IsNullOrWhiteSpace(selected.SampleDownloadUrl))
            dialog.SetSampleUrl(selected.SampleDownloadUrl);
        if (dialog.ShowDialog() != true) return;

        selected.FilePattern = dialog.DownloadPattern;
        selected.SampleDownloadUrl = dialog.SampleUrl;
        selected.LinkTemplate = dialog.LinkTemplate;
        AdditionalFilesDataGrid.Items.Refresh();
    }
    private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdditionalFilesDataGrid.SelectedItem is AdditionalFile file) AdditionalFiles.Remove(file);
    }
    private void RegexGeneratorButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RegexGeneratorWindow(VersionPatternTextBox.Text, DownloadPatternTextBox.Text) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        VersionPatternTextBox.Text = dialog.VersionPattern;
        DownloadPatternTextBox.Text = dialog.DownloadPattern;
    }

    private void DirectLinkButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DirectLinkWindow(SourceUrlTextBox.Text.Trim()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        SourceUrlTextBox.Text = dialog.DirectUrl;
        DownloadPatternTextBox.Text = $@"(?<url>{Regex.Escape(dialog.DirectUrl)})";
        VersionPatternTextBox.Text = $@"(?<version>{Regex.Escape(dialog.ManualVersion)})";
        _workingCopy.AvailableVersion = dialog.ManualVersion;
        ValidationTextBlock.Text = $"Direct installer link configured with manual version {dialog.ManualVersion}. No automatic version detection will be performed.";
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AutoDetectWizardWindow(NameTextBox.Text.Trim(), SourceUrlTextBox.Text.Trim(), _workingCopy.LocalVersion) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var result = dialog.Result;
        SourceUrlTextBox.Text = result.SourceUrl;
        _workingCopy.AvailableVersion = result.Version;
        if (result.Method == SoftwareSourceKind.GitHubReleases)
        {
            MethodComboBox.SelectedIndex = 1;
            GitHubAssetPatternTextBox.Text = result.DownloadPattern ?? string.Empty;
        }
        else if (result.Method == SoftwareSourceKind.WinGet)
        {
            MethodComboBox.SelectedIndex = 2;
            PackageIdTextBox.Text = result.PackageId ?? string.Empty;
        }
        else
        {
            MethodComboBox.SelectedIndex = 0;
            VersionPatternTextBox.Text = result.VersionPattern ?? string.Empty;
            DownloadPatternTextBox.Text = result.DownloadPattern ?? string.Empty;
        }
        UpdateSourceFieldVisibility();
        ValidationTextBlock.Text = $"Auto Detect selected {result.Method} with {result.Confidence}% confidence. {result.Summary}";
    }

    private void DetectWebsiteLinksButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WebsiteDetectionWindow(NameTextBox.Text.Trim(), SourceUrlTextBox.Text.Trim()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedUrl)) return;
        SourceUrlTextBox.Text = string.IsNullOrWhiteSpace(SourceUrlTextBox.Text) ? dialog.SelectedUrl : SourceUrlTextBox.Text;
        var generator = new RegexGeneratorService();
        DownloadPatternTextBox.Text = string.IsNullOrWhiteSpace(dialog.SelectedVersion)
            ? generator.GenerateDownloadPattern(dialog.SelectedUrl)
            : generator.GenerateDownloadPattern(dialog.SelectedUrl, dialog.SelectedVersion);
        if (!string.IsNullOrWhiteSpace(dialog.SelectedVersion))
        {
            // A detected version can come from text near the download link even when the
            // installer URL itself is stable. In that case an URL-derived pattern would
            // incorrectly match the installer filename instead of the page version.
            VersionPatternTextBox.Text = dialog.SelectedUrl.Contains(dialog.SelectedVersion, StringComparison.OrdinalIgnoreCase)
                ? generator.GenerateVersionPatternFromUrl(dialog.SelectedUrl, dialog.SelectedVersion)
                : generator.GenerateNearbyVersionPattern();
        }
    }

    private void SearchWinGetButton_Click(object sender, RoutedEventArgs e)
    {
        var query = string.IsNullOrWhiteSpace(PackageIdTextBox.Text) ? NameTextBox.Text : PackageIdTextBox.Text;
        var dialog = new WinGetSearchWindow(query) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPackageId))
        {
            PackageIdTextBox.Text = dialog.SelectedPackageId;
            // WinGet's display name belongs to the package source, not to the Library Manager
            // software identity. Keep the profile name stable so alternate detection methods
            // (Website/GitHub/WinGet) continue to map to the same online catalogue software.
            ValidationTextBlock.Text = string.IsNullOrWhiteSpace(dialog.SelectedPackageVersion)
                ? $"WinGet package selected: {dialog.SelectedPackageName}."
                : $"WinGet package selected: {dialog.SelectedPackageName}. Current version: {dialog.SelectedPackageVersion}";
        }
    }

    private async void DetectGitHubAssetsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var release = await new GitHubDetection(AppServices.GitHubAuthentication, AppServices.Requests).GetLatestReleaseAsync(SourceUrlTextBox.Text.Trim());
            if (release.Assets.Count == 0) { ValidationTextBlock.Text = "No suitable installer or archive asset was found in the latest release."; return; }
            var asset = release.Assets.Count == 1
                ? release.Assets[0]
                : new GitHubAssetSelectionWindow(release.Assets) { Owner = Window.GetWindow(this) }.SelectAsset();
            if (asset is null) return;
            GitHubAssetPatternTextBox.Text = GitHubDetection.CreateAssetPattern(asset.FileName, release.Version);
            GitHubPatternExampleTextBlock.Text = $"Example: {GitHubAssetPatternTextBox.Text}";
            ValidationTextBlock.Text = $"Selected {asset.FileName} from release {release.Version}.";
        }
        catch (Exception ex) { ValidationTextBlock.Text = $"GitHub asset detection failed: {ex.Message}"; }
    }
    private void MethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSourceFieldVisibility();

    private void UpdateSourceFieldVisibility()
    {
        if (WebsiteFieldsPanel is null) return;
        var kind = GetSourceKind();
        WebsiteFieldsPanel.Visibility = kind == SoftwareSourceKind.Website ? Visibility.Visible : Visibility.Collapsed;
        GitHubFieldsPanel.Visibility = kind == SoftwareSourceKind.GitHubReleases ? Visibility.Visible : Visibility.Collapsed;
        WingetFieldsPanel.Visibility = kind == SoftwareSourceKind.WinGet ? Visibility.Visible : Visibility.Collapsed;
        NoneSourceHint.Visibility = kind == SoftwareSourceKind.None ? Visibility.Visible : Visibility.Collapsed;
    }

    private SoftwareSourceKind GetSourceKind() => MethodComboBox.SelectedIndex switch { 1 => SoftwareSourceKind.GitHubReleases, 2 => SoftwareSourceKind.WinGet, 3 => SoftwareSourceKind.None, _ => SoftwareSourceKind.Website };
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
