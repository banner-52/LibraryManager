using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LibraryManager.Views;

public partial class HelpView : System.Windows.Controls.UserControl
{
    public HelpView() { InitializeComponent(); }

    public event Action? CancelRequested;

    private bool _useExpandableSections;
    private Expander? _currentHelpExpander;
    private Panel? _helpTargetPanel;

    private void HelpSectionButton_Click(object sender, RoutedEventArgs e)
    {
        var topic = (sender as FrameworkElement)?.Tag as string;
        RenderTopic(topic);
        HelpScrollViewer.ScrollToTop();
    }

    private void RenderTopic(string? topic)
    {
        HelpContentPanel.Children.Clear();
        _useExpandableSections = topic == "Add";
        _currentHelpExpander = null;
        _helpTargetPanel = null;
        switch (topic)
        {
            case "Basic":
                Title("Basic settings", "Configure the package repository and understand the folder structure expected by Library Manager.");
                Section("Source folder", "Choose the root folder where Library Manager stores all managed software packages. It can be a local path or a network share.");
                Example("Local repository", @"C:\Packages");
                Example("Network repository", @"\\server\software\Packages");
                Section("Required repository structure", "Each software has its own folder directly under the Source folder. Each stored release is a ZIP named with the version number: <Software folder>\\<version>.zip.");
                CodeBlock("Example", @"C:\Packages
├── KeePass
│   ├── 2.59.zip
│   ├── 2.60.zip
│   └── 2.61.zip
├── VLC
│   ├── 3.0.21.zip
│   └── 3.0.22.zip
└── Arduino IDE
    └── 2.3.6.zip");
                Section("Version detection", "Library Manager reads the stored version from the ZIP filename. The version ZIP should therefore be named only with the version, for example 2.61.zip. Do not name it KeePass-2.61.zip.");
                Section("Package contents", "The version ZIP contains the installer and any companion files required for that release. Additional ZIP sources are extracted before they are added, so their extracted files appear directly in their configured destination folder.");
                Note("The software folder name identifies the application; the ZIP filename identifies the stored version.");
                break;
            case "Interface":
                Title("Interface & buttons", "Understand the main workflow before managing packages.");
                Section("Refresh", "Scans the configured Source folder and rebuilds the local software/version view. It does not contact vendor websites.");
                Section("Check", "Checks configured online sources for newer versions. A recent valid result can be reused from the check cache.");
                Section("Update", "Downloads the detected version to the temporary folder, prepares the package, creates the ZIP, then copies it to the Source folder.");
                Section("List / Tiles", "Changes presentation only. A single click selects an item; double-click or the gear button opens Profile Config.");
                Section("Status", "Up to date — stored package is current.\nUpdate available — a newer version was detected.\nError — the source could not be checked.\nNot checked — no online result is available yet.");
                Note("Refresh = local scan. Check = online version detection. Update = download and package.");
                break;
            case "Add":
                Title("Add software", "Create a software profile in a few simple steps. Start with the software information, choose where Library Manager should look for updates, test the result, then save.");

                Section("1. Create the software profile", "Enter the software name and its official source. The name is what you will see in List and Tiles. Do not include the version number.");
                Section("Software name", "Use the normal product name, for example KeePass, VLC or Arduino IDE.");
                Section("Publisher and description (optional)", "These fields are only descriptive. They are useful when the profile is shared in the Online Catalogue.");
                Section("Source URL", "Enter the page or repository used to find updates. For a normal website, use the download page rather than the vendor home page when possible. For GitHub Releases, use the repository URL.");
                Section("Software icon (optional)", "You can enter an icon URL/path or click Detect favicon after entering the Source URL. The icon does not affect update detection.");

                Section("2. Choose how to check for updates", "Select the method that matches where the software is published. You only need one method.");
                Section("Website", "Use this when the vendor publishes the installer on a normal website or download page. Library Manager looks for the latest version and the Windows installer link.");
                Section("Version regex and download-link regex", "These fields are the rules used by Library Manager. Prefer generating them with the helper tools below. Edit them manually only if you know how to use regular expressions.");
                Screenshot("website-regex-fields.png", "Version regex and download-link regex fields");
                Section("Detect download links", "Use this first. Enter the Source URL, click Detect download links, select the correct Windows installer, and apply the result. This automatically fills the corresponding field. Make sure no fixed version or variable information remains, then run Test Source.");
                Screenshot("detect-download-links-dialog.png", "Detect download links dialog");
                Screenshot("detected-download-link-rules.png", "Detected download-link rules");
                Section("Regex Generator", "Use this when you know a real installer URL and part of that URL changes. Mark the release number as Version. Mark other changing values as Variable. This automatically fills the field with a regular expression.");
                Screenshot("regex-generator-dialog.png", "Regex Generator dialog");
                Section("Direct link", "Use this when you have a fixed installer URL but Library Manager cannot detect a version from the website or URL. The download works, but future versions cannot be discovered automatically from that link.");
                Section("GitHub Releases", "Use this when the official project publishes installers in the Releases section of a public GitHub repository. Library Manager reads the latest release and selects the correct installer asset.");
                Section("Detect assets", "Enter a GitHub repository URL and click Detect assets. This automatically fills the filename pattern used to detect and download the next version.");
                Screenshot("github-release-source-configuration.png", "GitHub Releases source configuration");
                Screenshot("github-asset-selection.png", "GitHub asset selection");
                Section("WinGet", "Use this when the application is available in the Windows Package Manager catalogue. Search for the application instead of guessing its package ID.");
                Screenshot("winget-search-dialog.png", "WinGet search dialog");
                Section("None", "Use this for software that should appear in the local catalogue but should not be checked online. This is useful for internal software or manually supplied installers.");

                Section("3. Test the source", "Click Test Source before saving. This is the quickest way to confirm that the profile is correctly configured.");
                Section("What should the test show?", "Check the detected version and the download URL. Make sure they correspond to the current stable Windows release and the architecture you want.");
                Section("If the result is wrong", "Return to the update method and make the source or pattern more specific. For example, exclude x86, ARM, Linux, macOS, source archives, beta or nightly files when they are not wanted.");

                Section("5. Package options (optional)", "These settings only change how the downloaded installer is stored inside the generated version ZIP.");
                Section("Installer filename template", "Leave this field empty to keep the original installer filename supplied by the vendor. Enter a template only when you want Library Manager to rename the installer before it is added to the ZIP. Available values include {name}, {version}, and {ext}. For example: {name}-{version}.{ext}.");

                Section("6. Silent install / uninstall (optional)", "These commands are deployment information stored with the profile. Library Manager does not run them.");
                Section("Find commands", "Click Find commands to look for known silent commands on SilentInstallHQ or enter custom commands. Always verify the result with the real installer before deployment.");

                Section("7. Additional files (optional)", "Use this only when the final package needs extra downloads such as language packs, plugins, configuration files or documentation.");
                Section("How to add an additional file source", "Add a row, enter the page containing the extra downloads, then generate or enter a rule that matches the files you want. The ZIP folder controls where the files are placed inside the final package. Required means the update must stop if the extra file cannot be found.");
                Screenshot("additional-files-configuration.png", "Additional files configuration");
                Screenshot("additional-file-regex-generator.png", "Additional-file regex generator");

                Section("8. Save the profile", "When Test Source returns the correct version and installer, save the profile. The software is then available in the local catalogue and can be checked and updated normally.");
                break;
            case "OnlineServices":
                Title("Online services", "Configure GitHub access and the shared Online Catalogue.");

                Section("GitHub account", "Settings → Online services shows the current GitHub connection and API request allowance. GitHub REST API primary rate limits are typically 60 requests/hour for anonymous access and 5,000 requests/hour for an authenticated personal account. Eligible GitHub Enterprise Cloud OAuth/GitHub App access can reach 15,000 requests/hour. Library Manager displays the remaining requests and reset time. Connecting an account therefore greatly reduces the risk of Check or Update being blocked by the anonymous limit, including GitHub Releases and WinGet operations that retrieve GitHub-hosted content. Connect with GitHub uses the browser OAuth flow. A Personal Access Token can also be supplied and is stored in Windows Credential Manager for the current Windows user. Refresh status updates the account and remaining GitHub API requests. Disconnect removes the active connection.");
                Note("API rate limits are GitHub-side limits, not a Library Manager quota. Connecting an account does not remove GitHub rate limiting completely, but it provides a much larger authenticated allowance and helps prevent GitHub and WinGet Check/Update operations from being blocked by the anonymous limit.");

                ExpandableSection("Online catalogue", "The Online Catalogue gives you access to profiles created by the community. Enable it in Settings to use it. The catalogue source is managed by Library Manager and cannot be changed in Settings. Select a profile and click Add selected profiles to save it to your local catalogue.");
                Section("Compare", "When an online profile also exists in your local catalogue, Compare opens the online and local configurations side by side and highlights their differences. You can review the changes before deciding whether to replace the local configuration.");
                Screenshot("online-catalogue-compare-profiles.png", "Compare profiles dialog");
                Section("See Profile", "When no matching local profile exists, See Profile opens a preview of the selected online profile. This lets you inspect its configuration before adding it to your local catalogue.");
                Section("Submit to Online Catalogue", "From Profile Config, submit a successfully tested profile to create the catalogue review request. A GitHub workflow checks the submitted profile against the published catalogue, and the profile is added after manual approval.");
                break;
            default:
                Title("Library Manager Help", "Select a topic on the left.");
                break;
        }
    }

    private void Title(string title, string intro)
    {
        HelpContentPanel.Children.Add(SelectableText(title, 24, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
        HelpContentPanel.Children.Add(Text(intro, 0, 0, 0, 20));
    }

    private void Section(string heading, string body)
    {
        if (_useExpandableSections && IsNumberedHelpSection(heading))
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(body, 0, 0, 0, 12));
            var expander = new Expander
            {
                Header = heading,
                Style = (Style)Application.Current.FindResource("SectionExpanderStyle"),
                IsExpanded = false,
                Content = panel,
                Margin = new Thickness(0, 2, 0, 2)
            };
            HelpContentPanel.Children.Add(expander);
            _currentHelpExpander = expander;
            _helpTargetPanel = panel;
            return;
        }

        var target = _useExpandableSections && _helpTargetPanel is not null ? _helpTargetPanel.Children : HelpContentPanel.Children;
        target.Add(SelectableText(heading, 16, FontWeights.SemiBold, new Thickness(0, 6, 0, 5)));
        target.Add(Text(body, 0, 0, 0, 12));
    }

    private static bool IsNumberedHelpSection(string heading) =>
        heading.Length > 2 && char.IsDigit(heading[0]) && heading[1] == '.';

    private void ExpandableSection(string heading, string body)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(body, 0, 0, 0, 12));
        var expander = new Expander
        {
            Header = heading,
            Style = (Style)Application.Current.FindResource("SectionExpanderStyle"),
            IsExpanded = false,
            Content = panel,
            Margin = new Thickness(0, 2, 0, 2)
        };
        HelpContentPanel.Children.Add(expander);
        _currentHelpExpander = expander;
        _helpTargetPanel = panel;
    }

    private void Screenshot(string assetName, string description)
    {
        TargetHelpChildren().Add(HelpScreenshot.Create(assetName, description, ShowScreenshotPreview));
    }

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

    private void Steps(params string[] steps)
    {
        Section("Steps", string.Join(Environment.NewLine, steps.Select((step, i) => $"{i + 1}. {step}")));
    }

    private void Example(string heading, string body) => Callout(heading, body, "PanelBackgroundBrush");
    private void Note(string body) => Callout("Tip", body, "PanelBackgroundBrush");
    private void Warning(string body) => Callout("Important", body, "PanelBackgroundBrush");

    private void CodeBlock(string heading, string body)
    {
        var code = ReadOnlyText(body, new Thickness(0, 6, 0, 0));
        code.FontFamily = new FontFamily("Consolas");
        code.FontSize = 13;
        code.TextWrapping = TextWrapping.NoWrap;
        code.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        code.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        code.MinHeight = Math.Max(28, body.Split('\n').Length * 19 + 10);

        var panel = new StackPanel();
        var headingText = ReadOnlyText(heading, new Thickness(0));
        headingText.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(headingText);
        panel.Children.Add(code);
        TargetHelpChildren().Add(new Border
        {
            Background = Brush("PanelBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 5, 0, 12),
            Child = panel
        });
    }

    private void Callout(string heading, string body, string resource) => Callout(heading, Text(body, 0, 6, 0, 0), resource);

    private void Callout(string heading, TextBox body, string resource)
    {
        var panel = new StackPanel();
        panel.Children.Add(SelectableText(heading, 14, FontWeights.SemiBold, new Thickness(0)));
        panel.Children.Add(body);
        TargetHelpChildren().Add(new Border
        {
            Background = Brush(resource),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 5, 0, 12),
            Child = panel
        });
    }

    private TextBox Text(string value, double left, double top, double right, double bottom) =>
        ReadOnlyText(value, new Thickness(left, top, right, bottom));

    private static TextBox SelectableText(string value, double fontSize, FontWeight fontWeight, Thickness margin)
    {
        var text = ReadOnlyText(value, margin);
        text.FontSize = fontSize;
        text.FontWeight = fontWeight;
        return text;
    }

    private static TextBox ReadOnlyText(string value, Thickness margin) => new()
    {
        Text = value,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = Brush("PrimaryTextBrush"),
        Padding = new Thickness(0),
        Margin = margin,
        Cursor = System.Windows.Input.Cursors.IBeam,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private UIElementCollection TargetHelpChildren() =>
        _useExpandableSections && _helpTargetPanel is not null ? _helpTargetPanel.Children : HelpContentPanel.Children;

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
