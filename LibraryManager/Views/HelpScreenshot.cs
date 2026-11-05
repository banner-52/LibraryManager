using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LibraryManager.Views;

internal static class HelpScreenshot
{
    public static Border Create(string assetName, string description, Action<ImageSource, string> showPreview, double? displayWidth = null)
    {
        var source = new BitmapImage(new Uri($"pack://application:,,,/Assets/Help/{assetName}", UriKind.Absolute));
        var image = new Image
        {
            Source = source,
            Width = displayWidth ?? source.PixelWidth * .75d,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = $"{description}\nClick to enlarge.",
            Cursor = Cursors.Hand
        };
        image.MouseLeftButtonUp += (_, _) => showPreview(source, description);

        return new Border
        {
            Background = (Brush)Application.Current.FindResource("PanelBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 4, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = image
        };
    }
}
