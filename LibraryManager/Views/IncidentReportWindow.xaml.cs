using System.Windows;

namespace LibraryManager.Views;

public partial class IncidentReportWindow : Window
{
    public string Summary { get; private set; } = string.Empty;
    public string Details { get; private set; } = string.Empty;

    public IncidentReportWindow()
    {
        InitializeComponent();
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryTextBox.Text))
        {
            ErrorTextBlock.Text = "Enter a short incident summary.";
            return;
        }

        Summary = SummaryTextBox.Text.Trim();
        Details = DetailsTextBox.Text.Trim();
        DialogResult = true;
    }
}