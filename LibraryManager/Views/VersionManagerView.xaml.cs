using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LibraryManager.Models;
using LibraryManager.Services;

namespace LibraryManager.Views;

public partial class VersionManagerView : UserControl
{
    private readonly PackageService _packageService = new();
    private readonly Software _software;
    private readonly string _repositoryPath;

    public VersionManagerView() : this(new Software(), string.Empty) { }

    public VersionManagerView(Software software, string repositoryPath)
    {
        InitializeComponent();
        _software = software;
        _repositoryPath = repositoryPath;
        DataContext = this;
        Refresh();
    }

    public ObservableCollection<StoredPackageInfo> Packages { get; } = [];

    private void Refresh()
    {
        Packages.Clear();
        foreach (var package in _packageService.GetStoredPackages(_software, _repositoryPath)) Packages.Add(package);
    }

    private void ViewContentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (PackagesDataGrid.SelectedItem is not StoredPackageInfo package) return;
        var contents = _packageService.GetPackageContents(package.FullPath);
        ThemedMessageBox.Show(contents.Count == 0 ? "The package is empty." : string.Join(Environment.NewLine, contents), package.FileName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PackagesDataGrid.SelectedItem is not StoredPackageInfo package) return;
        if (ThemedMessageBox.Show($"Delete {package.FileName}? This cannot be undone.", "Delete package", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _packageService.DeletePackage(package.FullPath);
        Refresh();
    }
}

