using System.Windows;
using LibraryManager.Services.Detection;

namespace LibraryManager.Views;

public partial class GitHubAssetSelectionWindow : ThemedWindow
{
    public GitHubAssetSelectionWindow(IReadOnlyList<GitHubReleaseAsset> assets) { InitializeComponent(); AssetsDataGrid.ItemsSource = assets; }
    public GitHubReleaseAsset? SelectedAsset { get; private set; }
    public GitHubReleaseAsset? SelectAsset() => ShowDialog() == true ? SelectedAsset : null;
    private void UseButton_Click(object sender, RoutedEventArgs e) { if (AssetsDataGrid.SelectedItem is not GitHubReleaseAsset asset) return; SelectedAsset = asset; DialogResult = true; }
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
