using System.Windows;

namespace LibraryManager.Views;

public partial class SoftwareRequestWindow : Window
{
    public string SoftwareName { get; private set; } = string.Empty;
    public string SourceUrl { get; private set; } = string.Empty;
    public string Details { get; private set; } = string.Empty;

    public SoftwareRequestWindow() => InitializeComponent();

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var name = SoftwareNameTextBox.Text.Trim();
        var url = SourceUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorTextBlock.Text = "Enter the software name.";
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            ErrorTextBlock.Text = "Enter a public HTTPS website or download page.";
            return;
        }

        SoftwareName = name;
        SourceUrl = url;
        Details = DetailsTextBox.Text.Trim();
        DialogResult = true;
    }
}
