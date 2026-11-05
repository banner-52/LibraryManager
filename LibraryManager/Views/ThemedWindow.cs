using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LibraryManager.Views;

public class ThemedWindow : Window
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SourceInitialized += (_, _) => ApplyNativeTheme();
        Activated += (_, _) => ApplyNativeTheme();
    }

    private void ApplyNativeTheme()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var background = (TryFindResource("WindowBackgroundBrush") as SolidColorBrush)?.Color ?? Colors.White;
        var foreground = (TryFindResource("PrimaryTextBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        var dark = IsDark(background) ? 1 : 0;
        var caption = ToColorRef(background);
        var text = ToColorRef(foreground);
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
    }

    private static bool IsDark(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) < 128;
    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
