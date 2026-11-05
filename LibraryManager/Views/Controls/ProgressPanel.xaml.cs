// UI behavior for ProgressPanel.
// Receive and display progress; perform no business operations.
// Keep application/business operations in Services.
namespace LibraryManager.Views.Controls;
public partial class ProgressPanel : System.Windows.Controls.UserControl
{
    public ProgressPanel() { InitializeComponent(); }
    public event EventHandler? CancelRequested;
    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
