using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LibraryManager.Views;

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var result = MessageBoxResult.None;
        var window = new ThemedWindow
        {
            Title = caption,
            Width = 520,
            MinWidth = 380,
            MaxWidth = 720,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsActive);
        if (owner is not null && owner != window) window.Owner = owner;

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (image != MessageBoxImage.None)
        {
            var icon = new TextBlock
            {
                Text = GetIcon(image),
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                Width = 42,
                VerticalAlignment = VerticalAlignment.Top,
                TextAlignment = TextAlignment.Center,
                Foreground = GetIconBrush(window, image)
            };
            content.Children.Add(icon);
        }

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = image == MessageBoxImage.None ? new Thickness(0) : new Thickness(10, 4, 0, 0)
        };
        Grid.SetColumn(messageBlock, 1);
        content.Children.Add(messageBlock);
        layout.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var option in GetOptions(buttons))
        {
            var button = new Button
            {
                Content = option.Label,
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = option.Result is MessageBoxResult.OK or MessageBoxResult.Yes,
                IsCancel = option.Result is MessageBoxResult.Cancel || buttons == MessageBoxButton.YesNo && option.Result == MessageBoxResult.No
            };
            button.Click += (_, _) => { result = option.Result; window.Close(); };
            actions.Children.Add(button);
        }
        Grid.SetRow(actions, 1);
        layout.Children.Add(actions);
        window.Content = layout;
        window.ShowDialog();
        return result == MessageBoxResult.None ? GetCloseResult(buttons) : result;
    }

    private static IReadOnlyList<(string Label, MessageBoxResult Result)> GetOptions(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)],
        MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
        MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
        _ => [("OK", MessageBoxResult.OK)]
    };

    private static MessageBoxResult GetCloseResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.OK => MessageBoxResult.OK,
        _ => MessageBoxResult.Cancel
    };

    private static string GetIcon(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => "×",
        MessageBoxImage.Warning => "!",
        MessageBoxImage.Question => "?",
        _ => "i"
    };

    private static Brush GetIconBrush(FrameworkElement element, MessageBoxImage image)
    {
        var resource = image switch
        {
            MessageBoxImage.Error => "ErrorBrush",
            MessageBoxImage.Warning => "WarningBrush",
            MessageBoxImage.Question => "AccentBrush",
            _ => "AccentBrush"
        };
        return element.TryFindResource(resource) as Brush ?? Brushes.Gray;
    }
}
