using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibraryManager.Models;

namespace LibraryManager.Views;

public partial class ProfileComparisonView : UserControl
{
    public ProfileComparisonView(Software? localProfile, Software onlineProfile)
    {
        InitializeComponent();
        ComparisonGrid.ItemsSource = BuildRows(localProfile, onlineProfile);
    }

    public event Action? CancelRequested;
    public event Action? ReplaceRequested;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void ReplaceButton_Click(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke();

    private static IReadOnlyList<ProfileComparisonRow> BuildRows(Software? local, Software online)
    {
        var rows = new List<ProfileComparisonRow>();
        Add(rows, "Name", local?.Name, online.Name);
        Add(rows, "Software ID", local?.SoftwareId, online.SoftwareId);
        Add(rows, "Publisher", local?.Publisher, online.Publisher);
        Add(rows, "Description", local?.Description, online.Description);
        Add(rows, "Icon URL", local?.IconPath, online.IconPath);
        Add(rows, "Detection method", local?.Source.Kind.ToString(), online.Source.Kind.ToString());
        Add(rows, "Source URL", local?.Source.Url, online.Source.Url);
        Add(rows, "WinGet package ID", local?.Source.PackageId, online.Source.PackageId);
        Add(rows, "Version pattern", local?.Source.VersionPattern, online.Source.VersionPattern);
        Add(rows, "Asset pattern", local?.Source.DownloadPattern, online.Source.DownloadPattern);
        Add(rows, "Download method", local?.Source.DownloadMethod.ToString(), online.Source.DownloadMethod.ToString());
        Add(rows, "Download source URL", local?.Source.DownloadSourceUrl, online.Source.DownloadSourceUrl);
        Add(rows, "Download asset pattern", local?.Source.DownloadAssetPattern, online.Source.DownloadAssetPattern);
        Add(rows, "Download version pattern", local?.Source.DownloadVersionPattern, online.Source.DownloadVersionPattern);
        Add(rows, "Delivery URL template", local?.Source.DeliveryUrlTemplate, online.Source.DeliveryUrlTemplate);
        Add(rows, "Delivery filename template", local?.Source.DeliveryFileNameTemplate, online.Source.DeliveryFileNameTemplate);
        Add(rows, "Download URL template", local?.Source.DownloadUrlTemplate, online.Source.DownloadUrlTemplate);
        Add(rows, "Download filename template", local?.Source.DownloadFileNameTemplate, online.Source.DownloadFileNameTemplate);
        Add(rows, "Download WinGet package ID", local?.Source.DownloadWinGetPackageId, online.Source.DownloadWinGetPackageId);
        Add(rows, "Installer entry", local?.InstallerFileName ?? local?.Source.InstallerEntry, online.InstallerFileName ?? online.Source.InstallerEntry);
        Add(rows, "Silent install", local?.SilentInstallCommand, online.SilentInstallCommand);
        Add(rows, "Silent uninstall", local?.SilentUninstallCommand, online.SilentUninstallCommand);
        Add(rows, "Additional Files", SerializeAdditionalFiles(local?.AdditionalFiles ?? []), SerializeAdditionalFiles(online.AdditionalFiles));
        return rows;
    }

    private static void Add(List<ProfileComparisonRow> rows, string setting, string? local, string? online) =>
        rows.Add(new ProfileComparisonRow(setting, local ?? string.Empty, online ?? string.Empty));

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string SerializeAdditionalFiles(IEnumerable<AdditionalFile> files) =>
        string.Join(Environment.NewLine, files.Select(file => JsonSerializer.Serialize(file)));

    public sealed class ProfileComparisonRow
    {
        public ProfileComparisonRow(string setting, string localValue, string onlineValue)
        {
            Setting = setting;
            LocalValue = localValue;
            OnlineValue = onlineValue;
            IsDifferent = !string.Equals(localValue, onlineValue, StringComparison.Ordinal);
        }

        public string Setting { get; }
        public string LocalValue { get; }
        public string OnlineValue { get; }
        public bool IsDifferent { get; }
        public FontWeight DifferenceWeight => IsDifferent ? FontWeights.Bold : FontWeights.Normal;
    }
}