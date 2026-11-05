using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LibraryManager.Helpers;
using LibraryManager.Services;

namespace LibraryManager.Views;

public partial class AutoDetectWizardWindow : ThemedWindow
{
    private CancellationTokenSource? _cancellation;
    private AutoDetectResult? _result;
    private IReadOnlyList<AutoDetectResult> _discovered = [];
    private string _sortColumn = "Version";
    private bool _sortDescending = true;
    public AutoDetectResult? Result => _result;

    public AutoDetectWizardWindow(string name, string sourceUrl, string? knownVersion = null)
    {
        InitializeComponent();
        NameTextBox.Text = name;
        UrlTextBox.Text = sourceUrl;
        KnownVersionTextBox.Text = knownVersion ?? string.Empty;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        ScanButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ClearResult();
        try
        {
            var progress = new Progress<string>(message => StatusTextBlock.Text = message);
            var results = await new AutoDetectService().DetectAllAsync(NameTextBox.Text.Trim(), UrlTextBox.Text.Trim(), KnownVersionTextBox.Text.Trim(), progress, _cancellation.Token);
            _discovered = results;
            UpdateResults();
            ResultsList.SelectedIndex = 0;
            StatusTextBlock.Text = $"{results.Count} solution(s) found. Select the method you want to keep.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Detection cancelled or timed out."; }
        catch (Exception ex) { StatusTextBlock.Text = $"Auto detection failed: {ex.Message}"; }
        finally { ProgressBar.Visibility = Visibility.Collapsed; ScanButton.IsEnabled = true; }
    }

    private sealed record DetectionOption(AutoDetectResult Result)
    {
        public string Method => Result.Method.ToString();
        public string FileType => GetFileType(Result);
        public string Architecture => Result.Architecture;
        public string Version => Result.Version ?? "Unknown";
        public string Confidence => $"{Result.Confidence}%";
        public string Source => Result.PackageId ?? Result.DownloadUrl ?? Result.SourceUrl;
    }

    private static string GetFileType(AutoDetectResult result)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadUrl)) return "Unknown";
        if (!Uri.TryCreate(result.DownloadUrl, UriKind.Absolute, out var uri)) return "Unknown";
        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToUpperInvariant();
        if (extension.Length == 0) return "Unknown";
        return extension switch
        {
            "EXE" or "MSI" or "MSIX" or "MSIXBUNDLE" or "APPX" or "APPXBUNDLE"
                or "ZIP" or "7Z" or "TAR" or "GZ" or "DEB" or "RPM"
                or "APPIMAGE" or "DMG" or "PKG" => extension,
            _ => "Other"
        };
    }

    private void UpdateResults()
    {
        var type = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var architecture = (ArchitectureFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var options = _discovered
            .Where(item => type == "All" || GetFileType(item) == type)
            .Where(item => architecture == "All" || item.Architecture == architecture)
            .Select(item => new DetectionOption(item));
        var selected = (ResultsList.SelectedItem as DetectionOption)?.Result ?? _result;
        ResultsList.ItemsSource = SortResults(options).ToList();
        var matching = (ResultsList.ItemsSource as IEnumerable<DetectionOption>)?
            .FirstOrDefault(item => ReferenceEquals(item.Result, selected));
        if (matching is not null)
            ResultsList.SelectedItem = matching;
        else
        {
            _result = null;
            GenerateButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
        }
    }

    private IEnumerable<DetectionOption> SortResults(IEnumerable<DetectionOption> options) => _sortColumn switch
    {
        "Version" => SortVersions(options),
        "Confidence" => _sortDescending ? options.OrderByDescending(item => item.Result.Confidence) : options.OrderBy(item => item.Result.Confidence),
        "Method" => SortText(options, item => item.Method),
        "FileType" => SortText(options, item => item.FileType),
        "Architecture" => SortText(options, item => item.Architecture),
        "Source" => SortText(options, item => item.Source),
        _ => options
    };

    private IEnumerable<DetectionOption> SortVersions(IEnumerable<DetectionOption> options)
    {
        var parsed = options.Select(item => new { Item = item, Parsed = TryParseVersion(item.Version, out var version), Version = version });
        return _sortDescending
            ? parsed.OrderByDescending(item => item.Parsed).ThenByDescending(item => item.Version).ThenBy(item => item.Item.Version, StringComparer.OrdinalIgnoreCase).Select(item => item.Item)
            : parsed.OrderByDescending(item => item.Parsed).ThenBy(item => item.Version).ThenBy(item => item.Item.Version, StringComparer.OrdinalIgnoreCase).Select(item => item.Item);
    }

    private IEnumerable<DetectionOption> SortText(IEnumerable<DetectionOption> options, Func<DetectionOption, string> selector) =>
        _sortDescending ? options.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase) : options.OrderBy(selector, StringComparer.OrdinalIgnoreCase);

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        var match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        return match.Success && VersionHelper.TryParse(match.Value, out version);
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList is not null && TypeFilter is not null && ArchitectureFilter is not null)
            UpdateResults();
    }

    private void ResultsHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string column }) return;
        _sortDescending = _sortColumn == column ? !_sortDescending : column == "Version";
        _sortColumn = column;
        UpdateResults();
        if (ResultsList.View is GridView view)
        {
            foreach (var gridColumn in view.Columns)
            {
                if (gridColumn.Header is not GridViewColumnHeader header || header.Tag is not string key) continue;
                var label = key switch
                {
                    "FileType" => "File type",
                    "Source" => "Download URL",
                    _ => key
                };
                header.Content = label + (key == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : "");
            }
        }
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _result = (ResultsList.SelectedItem as DetectionOption)?.Result;
        ApplyButton.IsEnabled = false;
        GenerateButton.IsEnabled = _result is not null;
        if (_result is not null) ShowResult(_result);
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        try
        {
            _result = new AutoDetectService().GenerateSelectedRule(_result);
            ShowResult(_result);
            ApplyButton.IsEnabled = true;
            StatusTextBlock.Text = "Rule generated and checked against the selected download. Review before applying.";
        }
        catch (Exception ex)
        {
            ApplyButton.IsEnabled = false;
            StatusTextBlock.Text = $"Rule generation failed: {ex.Message}";
        }
    }

    private void ShowResult(AutoDetectResult result)
    {
        MethodText.Text = result.Method.ToString();
        VersionText.Text = result.Version ?? "Not detected";
        ConfidenceText.Text = $"{result.Confidence}%";
        DownloadUrlText.Text = result.DownloadUrl ?? string.Empty;
        VersionPatternText.Text = result.VersionPattern ?? string.Empty;
        DownloadPatternText.Text = result.DownloadPattern ?? string.Empty;
        SummaryTextBlock.Text = result.Summary;
    }

    private void ClearResult()
    {
        _discovered = [];
        ResultsList.ItemsSource = null;
        GenerateButton.IsEnabled = false;
        _result = null; MethodText.Text = ""; VersionText.Text = ""; ConfidenceText.Text = "";
        DownloadUrlText.Text = ""; VersionPatternText.Text = ""; DownloadPatternText.Text = ""; SummaryTextBlock.Text = "";
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void ApplyButton_Click(object sender, RoutedEventArgs e) { if (_result is null) return; DialogResult = true; }
}
