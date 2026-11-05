using Microsoft.Toolkit.Uwp.Notifications;
using LibraryManager.Models;
using LibraryManager.Services.Detection;

namespace LibraryManager.Services;

public sealed class BackgroundUpdateNotificationService
{
    public const string CheckArgument = "--background-update-check";
    public const string TestArgument = "--test-update-notification";
    private const string CheckSemaphoreName = @"Local\LibraryManager.BackgroundUpdateCheck";

    public static bool IsBackgroundInvocation(IReadOnlyList<string> arguments) =>
        arguments.Contains(CheckArgument, StringComparer.OrdinalIgnoreCase) ||
        arguments.Contains(TestArgument, StringComparer.OrdinalIgnoreCase);

    public async Task HandleArgumentsAsync(IReadOnlyList<string> arguments)
    {
        if (arguments.Contains(TestArgument, StringComparer.OrdinalIgnoreCase))
        {
            ShowTestToast();
            return;
        }

        if (arguments.Contains(CheckArgument, StringComparer.OrdinalIgnoreCase))
            await CheckForUpdatesAsync();
    }

    public async Task CheckForUpdatesAsync()
    {
        using var checkSemaphore = new Semaphore(1, 1, CheckSemaphoreName);
        if (!checkSemaphore.WaitOne(0))
        {
            var settings = await new SettingsService().LoadAsync();
            if (settings.EnableLogging)
                await new LogService().WriteAsync("Background update check skipped: another check is already running.");
            return;
        }

        try
        {
            await CheckForUpdatesCoreAsync();
        }
        finally
        {
            checkSemaphore.Release();
        }
    }

    private async Task CheckForUpdatesCoreAsync()
    {
        var settings = await new SettingsService().LoadAsync();
        var logger = new LogService();

        async Task LogAsync(string message)
        {
            if (settings.EnableLogging)
                await logger.WriteAsync(message);
        }

        await LogAsync("Background update check started.");

        if (!settings.EnableBackgroundUpdateNotifications)
        {
            await LogAsync("Background update check stopped: notifications are disabled in Settings.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.RepositoryPath))
        {
            await LogAsync("Background update check stopped: the package repository path is empty.");
            return;
        }

        var catalog = new CatalogService();
        await catalog.LoadAsync(settings.RepositoryPath);
        catalog.RefreshStoredVersions(settings.RepositoryPath);

        var monitored = catalog.SoftwareItems
            .Where(item => item.IsUpdateNotificationEnabled && item.Source.Kind != SoftwareSourceKind.None)
            .ToList();

        await LogAsync($"Background update check loaded {catalog.SoftwareItems.Count} software profile(s); {monitored.Count} are monitored.");

        if (monitored.Count == 0)
        {
            await LogAsync("Background update check stopped: no software profile has notifications enabled.");
            return;
        }

        var updateService = new UpdateService(
            new GitHubDetection(AppServices.GitHubAuthentication, AppServices.Requests),
            new WebsiteDetection(),
            new WingetDetection(),
            new DownloadService(),
            new PackageService());

        await updateService.CheckManyAsync(monitored, maxDegreeOfParallelism: 4);

        foreach (var software in monitored)
        {
            await LogAsync(
                $"Background update result: {software.DisplayName}; status={software.Status}; " +
                $"local={software.LocalVersion ?? "unknown"}; available={software.AvailableVersion ?? "unknown"}.");
        }

        var currentUpdates = monitored
            .Where(item => item.Status == SoftwareStatus.UpdateAvailable && !string.IsNullOrWhiteSpace(item.AvailableVersion))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currentUpdates.Count > 0)
        {
            ShowUpdateToast(currentUpdates);
            await LogAsync($"Background update notification sent with {currentUpdates.Count} software update(s).");
        }

        await LogAsync($"Background update check completed; {currentUpdates.Count} update(s) detected and {(currentUpdates.Count > 0 ? 1 : 0)} notification(s) sent.");
    }

    public static void ShowTestToast()
    {
        new ToastContentBuilder()
            .AddText("Library Manager notifications are working")
            .AddText("Daily software update notifications can be displayed on this computer.")
            .Show();
    }

    private static void ShowUpdateToast(IReadOnlyList<Software> updates)
    {
        var updateList = string.Join(
            Environment.NewLine,
            updates.Select(software =>
                $"{software.DisplayName}: {software.LocalVersion ?? "unknown"} → {software.AvailableVersion}"));

        new ToastContentBuilder()
            .AddText($"{updates.Count} software update{(updates.Count == 1 ? string.Empty : "s")} available")
            .AddText(updateList)
            .Show(toast =>
            {
                toast.Tag = "available-updates";
                toast.Group = "software-updates";
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(23);
            });
    }
}
