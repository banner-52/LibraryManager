using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LibraryManager.Models;

namespace LibraryManager.Views.Controls;

public partial class SoftwareList : UserControl
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    private CancellationTokenSource? _pendingSelection;
    private static readonly BitmapImage DefaultIcon = new(new Uri(Software.DefaultIconPath));
    public SoftwareList() => InitializeComponent();
    public event Action<Software>? EditRequested;
    public event Action<Software>? UpdateRequested;
    public event Action<Software>? DeleteRequested;
    public event Action<Software>? ManageVersionsRequested;
    public event Action<Software>? FavoriteChanged;
    public event Action<Software>? NotificationChanged;

    private void SoftwareIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image) image.Source = DefaultIcon;
        e.Handled = true;
    }

    private void DataGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        // Resolve the row from the DataGrid itself instead of walking OriginalSource.
        // DataGrid cells can expose ContentElement sources that are unsafe to traverse
        // during a double-click.
        var row = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row?.DataContext is not Software software) return;

        var source = e.OriginalSource as DependencyObject;
        if (IsInsideInteractiveControl(source)) return;

        if (e.ClickCount == 2)
        {
            CancelPendingSelection();
            e.Handled = true;

            // Opening the profile replaces part of the visual tree. Defer it until
            // WPF has completely finished routing the current double-click event.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => EditRequested?.Invoke(software)));
            return;
        }

        BeginSelection(software);
    }

    private void BeginSelection(Software software)
    {
        CancelPendingSelection();
        var cancellation = _pendingSelection = new CancellationTokenSource();
        _ = SelectAfterDoubleClickWindowAsync(software, cancellation.Token);
    }

    private async Task SelectAfterDoubleClickWindowAsync(Software software, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay((int)GetDoubleClickTime(), cancellationToken);
            if (!cancellationToken.IsCancellationRequested) software.IsSelected = !software.IsSelected;
        }
        catch (OperationCanceledException) { }
    }

    private void CancelPendingSelection()
    {
        _pendingSelection?.Cancel();
        _pendingSelection?.Dispose();
        _pendingSelection = null;
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        if (source is null) return false;
        return FindVisualAncestor<Button>(source) is not null ||
               FindVisualAncestor<CheckBox>(source) is not null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        if (source is not System.Windows.Media.Visual && source is not System.Windows.Media.Media3D.Visual3D)
            return null;

        for (DependencyObject? current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;

        return null;
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is not Software software) return;
        software.IsFavorite = !software.IsFavorite;
        FavoriteChanged?.Invoke(software);
    }

    private void NotificationButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is not Software software) return;
        software.IsUpdateNotificationEnabled = !software.IsUpdateNotificationEnabled;
        NotificationChanged?.Invoke(software);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is Software software) UpdateRequested?.Invoke(software);
    }

    private void ConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is Software software) EditRequested?.Invoke(software);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.Tag is not Software software) return;
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Configure profile" };
        edit.Click += (_, _) => EditRequested?.Invoke(software);
        var delete = new MenuItem { Header = "Delete profile" };
        delete.Click += (_, _) => DeleteRequested?.Invoke(software);
        var versions = new MenuItem { Header = "Manage stored versions" };
        versions.Click += (_, _) => ManageVersionsRequested?.Invoke(software);
        menu.Items.Add(edit);
        menu.Items.Add(versions);
        menu.Items.Add(delete);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
