using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using LibraryManager.Services.Detection;

namespace LibraryManager.Views;

public partial class WinGetSearchWindow : ThemedWindow
{
    private readonly WingetDetection _winget = new();
    private readonly ObservableCollection<WinGetPackage> _results = [];

    public WinGetSearchWindow(string initialQuery)
    {
        InitializeComponent();
        QueryTextBox.Text = initialQuery;
        ResultsDataGrid.ItemsSource = _results;
    }

    public string? SelectedPackageId { get; private set; }
    public string? SelectedPackageName { get; private set; }
    public string? SelectedPackageVersion { get; private set; }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Searching WinGet...";
        try
        {
            var packages = await _winget.SearchAsync(QueryTextBox.Text);
            _results.Clear();
            foreach (var package in packages) _results.Add(package);
            StatusTextBlock.Text = $"{packages.Count} package(s) found.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"WinGet search failed: {ex.Message}";
        }
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsDataGrid.SelectedItem is not WinGetPackage package)
        {
            StatusTextBlock.Text = "Select a package first.";
            return;
        }
        SelectedPackageId = package.Id;
        SelectedPackageName = package.Name;
        SelectedPackageVersion = package.Version;
        DialogResult = true;
    }

    private void ResultsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseButton_Click(sender, e);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
