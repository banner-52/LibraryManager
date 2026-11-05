using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.IO;
using LibraryManager.Models;
using LibraryManager.Services;
using Microsoft.Win32;

namespace LibraryManager.Views;

public partial class FirstLaunchGuideView : UserControl
{
    private readonly SettingsService _settingsService = new();
    private int _pageIndex;

    public FirstLaunchGuideView()
    {
        InitializeComponent();
        Loaded += FirstLaunchGuideView_Loaded;
        RenderPage();
    }

    public event Action? CloseRequested;
    public event Action<bool>? StartupPreferenceChanged;
    public event Action<AppSettings>? SettingsChanged;

    private string? _appearanceSelectionMessage;
    private string _repositoryPath = string.Empty;
    private string? _repositoryPathMessage;

    private async void FirstLaunchGuideView_Loaded(object sender, RoutedEventArgs e)
    {
        _repositoryPath = (await _settingsService.LoadAsync()).RepositoryPath;
        if (_pageIndex == 0) RenderPage();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex == 0) return;
        _pageIndex--;
        RenderPage();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex < 3)
        {
            _pageIndex++;
            RenderPage();
            return;
        }

        FinishAsync();
    }

    private async void FinishAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.ShowFirstLaunchGuide = DontShowAgainCheckBox.IsChecked != true;
        await _settingsService.SaveAsync(settings);
        StartupPreferenceChanged?.Invoke(settings.ShowFirstLaunchGuide);
        CloseRequested?.Invoke();
    }

    private void RenderPage()
    {
        GuideContentPanel.Children.Clear();
        GuideScrollViewer.ScrollToTop();
        UpdateProgressDots();
        BackButton.IsEnabled = _pageIndex > 0;
        NextButton.Content = _pageIndex == 3 ? "Finish" : "Next";

        switch (_pageIndex)
        {
            case 0:
                Title("Welcome to Library Manager", "Library Manager keeps your software installers and profile configurations in one place. Use it to check for new releases, create versioned packages and maintain a local software catalogue.");
                Section("Start here", "Choose an appearance before you begin. You can configure your package repository on the next page or change either setting later from Settings.");
                ThemePreviews();
                Note("You can reopen this guide at any time from Settings.");
                break;
            case 1:
                Title("Configure your library", "Before saving or updating software, choose where Library Manager should store your packages.");
                FolderConfiguration();
                Section("You can change it", "Open Settings, choose Source folder, then set the Package repository path. This folder can be on your computer or on a network share.");
                Screenshot("settings-source-folder.png", "Settings: Source folder");
                Section("Folder location", "Each managed application receives its own folder under this location. Library Manager stores versioned ZIP packages inside those software folders.");
                Note("Changing the folder location opens a different repository. Existing packages are not moved automatically.");
                break;
            case 2:
                Title("Add your first software", "You can build a local profile yourself or start from a shared profile in the Online Catalogue.");
                Section("New Profile", "Choose New Profile to describe the software, select a source method and test version detection before saving. This is the right choice when the software is not yet available online or needs a custom configuration.");
                Screenshot("github-release-source-configuration.png", "Example source configuration");
                Section("Online Catalogue", "Open the Online Catalogue to browse approved profiles. Select one or more source methods, then add the selected profiles to your local catalogue.");
                Screenshot("online-catalogue-list.png", "Online Catalogue");
                break;
            default:
                Title("Use the Online Catalogue", "The Online Catalogue helps you review and reuse community-maintained profiles without changing your local configuration unexpectedly.");
                Section("See Profile", "Use See Profile when there is no matching local profile. It opens a preview so you can inspect the online configuration before adding it.");
                Section("Compare", "Use Compare when the selected online profile already exists locally. It highlights differences between the two configurations before you decide whether to replace your local profile.");
                Screenshot("online-catalogue-compare-profiles.png", "Compare local and online profiles");
                Section("Add or replace", "Add selected profiles creates new local profiles. From Compare, choose Replace local profile only after reviewing the differences. Stored package ZIP files are kept when a profile is replaced.");
                break;
        }
    }

    private void UpdateProgressDots()
    {
        var accentBrush = (Brush)Application.Current.FindResource("AccentBrush");
        var dots = new[] { ProgressDot1, ProgressDot2, ProgressDot3, ProgressDot4 };
        for (var index = 0; index < dots.Length; index++)
        {
            var isCurrent = index == _pageIndex;
            dots[index].Width = isCurrent ? 16 : 10;
            dots[index].Height = 10;
            dots[index].Fill = isCurrent ? accentBrush : Brushes.Transparent;
            dots[index].Stroke = accentBrush;
            dots[index].StrokeThickness = 2;
        }
    }

    private void Title(string title, string intro)
    {
        GuideContentPanel.Children.Add(Text(title, 24, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
        GuideContentPanel.Children.Add(Text(intro, 14, FontWeights.Normal, new Thickness(0, 0, 0, 20)));
    }

    private void Section(string heading, string body)
    {
        GuideContentPanel.Children.Add(Text(heading, 16, FontWeights.SemiBold, new Thickness(0, 6, 0, 5)));
        GuideContentPanel.Children.Add(Text(body, 14, FontWeights.Normal, new Thickness(0, 0, 0, 12)));
    }

    private void Note(string body)
    {
        GuideContentPanel.Children.Add(new Border
        {
            Background = (Brush)Application.Current.FindResource("PanelBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4, 0, 12),
            Child = Text($"Tip: {body}", 14, FontWeights.Normal, new Thickness())
        });
    }

    private void ThemePreviews()
    {
        GuideContentPanel.Children.Add(Text("Try the themes", 16, FontWeights.SemiBold, new Thickness(0, 6, 0, 5)));
        GuideContentPanel.Children.Add(Text("Choose Light or Dark in Settings → Theme to match your workspace.", 14, FontWeights.Normal, new Thickness(0, 0, 0, 8)));

        var previews = new StackPanel { Orientation = Orientation.Horizontal };
        previews.Children.Add(ThemePreview("Classic Light design", "theme-light-preview.png", "Classic Light design preview", "Use Classic Light design", AppDesign.Classic, LibraryManager.Models.ThemeMode.Light, false));
        previews.Children.Add(ThemePreview("Modern Dark design", "theme-dark-preview.png", "Modern Dark design preview", "Use Modern Dark design", AppDesign.Modern, LibraryManager.Models.ThemeMode.Dark, true));
        GuideContentPanel.Children.Add(previews);
        if (!string.IsNullOrWhiteSpace(_appearanceSelectionMessage))
            GuideContentPanel.Children.Add(Text(_appearanceSelectionMessage, 14, FontWeights.SemiBold, new Thickness(0, 0, 0, 12)));
    }

    private StackPanel ThemePreview(string label, string assetName, string description, string buttonLabel, AppDesign design, LibraryManager.Models.ThemeMode theme, bool isPrimary)
    {
        var preview = new StackPanel { Width = 340, Margin = new Thickness(0, 0, 12, 0) };
        preview.Children.Add(Text(label, 14, FontWeights.SemiBold, new Thickness(0, 0, 0, 4)));
        preview.Children.Add(HelpScreenshot.Create(assetName, description, ShowScreenshotPreview, 328));
        var chooseButton = new Button
        {
            Content = buttonLabel,
            Style = (Style)Application.Current.FindResource(isPrimary ? "PrimaryButtonStyle" : "StandardButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(12, 6, 12, 6)
        };
        chooseButton.Click += (_, _) => ApplyAppearanceAsync(design, theme, label);
        preview.Children.Add(chooseButton);
        return preview;
    }

    private async void ApplyAppearanceAsync(AppDesign design, LibraryManager.Models.ThemeMode theme, string label)
    {
        var settings = await _settingsService.LoadAsync();
        settings.Design = design;
        settings.Theme = theme;
        await _settingsService.SaveAsync(settings);
        _appearanceSelectionMessage = $"{label} selected.";
        SettingsChanged?.Invoke(settings);
        RenderPage();
    }

    private void FolderConfiguration()
    {
        GuideContentPanel.Children.Add(Text("Choose your package repository", 16, FontWeights.SemiBold, new Thickness(0, 6, 0, 5)));
        GuideContentPanel.Children.Add(Text("Library Manager stores each software package in this local folder or network share.", 14, FontWeights.Normal, new Thickness(0, 0, 0, 8)));

        var pathTextBox = new TextBox
        {
            Text = _repositoryPath,
            Style = (Style)Application.Current.FindResource("StandardTextBoxStyle"),
            MinWidth = 420
        };
        var browseButton = new Button
        {
            Content = "Browse...",
            Style = (Style)Application.Current.FindResource("StandardButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        browseButton.Click += (_, _) => BrowseRepositoryFolder(pathTextBox);

        var pathPanel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathPanel.Children.Add(pathTextBox);
        Grid.SetColumn(browseButton, 1);
        pathPanel.Children.Add(browseButton);
        GuideContentPanel.Children.Add(pathPanel);

        var saveButton = new Button
        {
            Content = "Save folder",
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 6, 12, 6)
        };
        saveButton.Click += (_, _) => SaveRepositoryPathAsync(pathTextBox.Text);
        GuideContentPanel.Children.Add(saveButton);

        if (!string.IsNullOrWhiteSpace(_repositoryPathMessage))
            GuideContentPanel.Children.Add(Text(_repositoryPathMessage, 14, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
    }

    private void BrowseRepositoryFolder(TextBox pathTextBox)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the package repository",
            InitialDirectory = Directory.Exists(pathTextBox.Text) ? pathTextBox.Text : null
        };
        if (dialog.ShowDialog() == true) pathTextBox.Text = dialog.FolderName;
    }

    private async void SaveRepositoryPathAsync(string repositoryPath)
    {
        var trimmedPath = repositoryPath.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedPath) && trimmedPath.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
        {
            _repositoryPathMessage = "The repository path contains invalid characters.";
            RenderPage();
            return;
        }

        var settings = await _settingsService.LoadAsync();
        settings.RepositoryPath = trimmedPath;
        await _settingsService.SaveAsync(settings);
        _repositoryPath = trimmedPath;
        _repositoryPathMessage = string.IsNullOrWhiteSpace(trimmedPath) ? "Folder location cleared." : "Folder location saved.";
        SettingsChanged?.Invoke(settings);
        RenderPage();
    }

    private void Screenshot(string assetName, string description) =>
        GuideContentPanel.Children.Add(HelpScreenshot.Create(assetName, description, ShowScreenshotPreview));

    private static TextBlock Text(string value, double fontSize, FontWeight fontWeight, Thickness margin) => new()
    {
        Text = value,
        FontSize = fontSize,
        FontWeight = fontWeight,
        Foreground = (Brush)Application.Current.FindResource("PrimaryTextBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = margin
    };

    private void ShowScreenshotPreview(ImageSource source, string description)
    {
        ScreenshotPreviewImage.Source = source;
        ScreenshotPreviewTitleTextBlock.Text = description;
        ScreenshotPreviewOverlay.Visibility = Visibility.Visible;
    }

    private void CloseScreenshotPreview_Click(object sender, RoutedEventArgs e)
    {
        ScreenshotPreviewOverlay.Visibility = Visibility.Collapsed;
        ScreenshotPreviewImage.Source = null;
    }
}
