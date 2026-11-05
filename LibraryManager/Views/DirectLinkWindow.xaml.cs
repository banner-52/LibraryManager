using System.Windows;

namespace LibraryManager.Views;

public partial class DirectLinkWindow : ThemedWindow
{
    public DirectLinkWindow(string? existingUrl = null)
    {
        InitializeComponent();
        UrlTextBox.Text = existingUrl ?? string.Empty;
    }

    public string DirectUrl => UrlTextBox.Text.Trim();
    public string ManualVersion => string.IsNullOrWhiteSpace(VersionTextBox.Text) ? "1.0" : VersionTextBox.Text.Trim();

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(DirectUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            StatusTextBlock.Text = "Enter a complete HTTPS installer URL.";
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}