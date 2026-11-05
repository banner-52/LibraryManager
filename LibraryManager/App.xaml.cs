// Global C# entry point for LibraryManager.
// Routes normal UI startup and silent background notification invocations.
using System.Windows;
using LibraryManager.Services;

namespace LibraryManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (BackgroundUpdateNotificationService.IsBackgroundInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await new BackgroundUpdateNotificationService().HandleArgumentsAsync(e.Args);
            }
            catch (Exception ex)
            {
                await new LogService().WriteAsync("Background update notification check failed", ex);
            }
            finally
            {
                Shutdown();
            }
            return;
        }

        new MainWindow().Show();
    }
}
