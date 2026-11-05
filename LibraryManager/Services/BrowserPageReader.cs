using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace LibraryManager.Services;

public static class BrowserPageReader
{
    public static Task<string> ReadHtmlAsync(Uri address, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            Window? host = null;
            try
            {
                host = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0 };
                var browser = new WebView2();
                host.Content = browser;
                host.Show();
                await browser.EnsureCoreWebView2Async();
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                browser.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new InvalidDataException("The browser could not load the source page.")); };
                browser.CoreWebView2.Navigate(address.AbsoluteUri);
                await loaded.Task.WaitAsync(cancellationToken);
                // Many download pages populate their links after NavigationCompleted.
                // Poll the live DOM until it stops changing instead of assuming a fixed delay.
                string html = string.Empty;
                var stableSamples = 0;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    await Task.Delay(500, cancellationToken);
                    var json = await browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                    var snapshot = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                    if (snapshot == html && snapshot.Length > 0)
                        stableSamples++;
                    else
                        stableSamples = 0;
                    html = snapshot;
                    if (stableSamples >= 3) break;
                }
                completion.TrySetResult(html);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { host?.Close(); }
        });
        return completion.Task;
    }
}