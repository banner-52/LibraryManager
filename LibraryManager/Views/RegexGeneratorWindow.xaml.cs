using System.Text.RegularExpressions;
using System.Windows;
using LibraryManager.Services;

namespace LibraryManager.Views;

public partial class RegexGeneratorWindow : ThemedWindow
{
    private readonly RegexGeneratorService _generator = new();
    private readonly List<(int Start, int Length, string Token)> _marks = [];

    public RegexGeneratorWindow(string? versionPattern, string? downloadPattern)
    {
        InitializeComponent();
    }

    public string VersionPattern { get; private set; } = string.Empty;
    public string DownloadPattern { get; private set; } = string.Empty;
    public string SampleUrl => SampleUrlTextBox.Text.Trim();
    public string LinkTemplate { get; private set; } = string.Empty;

    public void SetSampleUrl(string? value) => SampleUrlTextBox.Text = value ?? string.Empty;

    private void MarkVersionButton_Click(object sender, RoutedEventArgs e) => Mark("{version}");
    private void MarkVariableButton_Click(object sender, RoutedEventArgs e) => Mark("{variable}");

    private void Mark(string token)
    {
        if (SampleUrlTextBox.SelectionLength == 0)
        {
            StatusTextBlock.Text = "Select text in the example URL first.";
            return;
        }

        _marks.RemoveAll(x => x.Start == SampleUrlTextBox.SelectionStart && x.Length == SampleUrlTextBox.SelectionLength);
        _marks.Add((SampleUrlTextBox.SelectionStart, SampleUrlTextBox.SelectionLength, token));
        StatusTextBlock.Text = token == "{version}" ? "Version marked." : "Variable marked.";
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = SampleUrlTextBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                StatusTextBlock.Text = "Enter a complete HTTPS download URL.";
                return;
            }

            var template = url;
            foreach (var mark in _marks.OrderByDescending(x => x.Start))
                template = template.Remove(mark.Start, mark.Length).Insert(mark.Start, mark.Token);

            var version = ExtractMarkedVersion(url, template) ?? _generator.DetectVersion(url);
            if (string.IsNullOrWhiteSpace(version))
            {
                StatusTextBlock.Text = "No version was detected. Select it in the URL and mark it as the version.";
                return;
            }

            if (!template.Contains("{version}", StringComparison.Ordinal))
                template = template.Replace(version, "{version}", StringComparison.Ordinal);

            LinkTemplate = template;
            VersionPattern = _generator.GenerateVersionPatternFromUrl(url, version);
            DownloadPattern = _generator.GenerateDownloadPattern(template);
            _ = new Regex(DownloadPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = ex.Message;
        }
    }

    private static string? ExtractMarkedVersion(string sample, string template)
    {
        if (!template.Contains("{version}", StringComparison.Ordinal)) return null;
        var escaped = Regex.Escape(template)
            .Replace(Regex.Escape("{version}"), "(?<version>.+?)", StringComparison.Ordinal)
            .Replace(Regex.Escape("{variable}"), ".+?", StringComparison.Ordinal);
        var match = Regex.Match(sample, "\\A" + escaped + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["version"].Value : null;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}