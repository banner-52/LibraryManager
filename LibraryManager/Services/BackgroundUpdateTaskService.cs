using System.Diagnostics;
using System.IO;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class BackgroundUpdateTaskService
{
    public const string TaskName = "LibraryManager Update Check";
    private const string LegacyTaskName = "LibraryManager Daily Update Check";

    public string ApplicationPath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "LibraryManager.exe");

    public async Task EnsureCreatedAsync(
        string checkTime,
        BackgroundUpdateFrequency frequency,
        DayOfWeek weeklyDay,
        CancellationToken cancellationToken = default)
    {
        if (!TimeOnly.TryParseExact(checkTime, "HH:mm", out _))
            throw new ArgumentException("The update check time must use HH:mm format.", nameof(checkTime));
        if (!File.Exists(ApplicationPath))
            throw new FileNotFoundException("LibraryManager.exe could not be located.", ApplicationPath);

        await DeleteTaskIfPresentAsync(LegacyTaskName, cancellationToken);

        var arguments = new List<string>
        {
            "/Create", "/TN", TaskName,
            "/TR", $"\"{ApplicationPath}\" {BackgroundUpdateNotificationService.CheckArgument}",
            "/SC", frequency == BackgroundUpdateFrequency.Weekly ? "WEEKLY" : "DAILY",
            "/ST", checkTime,
            "/F", "/RL", "LIMITED", "/IT"
        };

        if (frequency == BackgroundUpdateFrequency.Weekly)
        {
            arguments.Add("/D");
            arguments.Add(ToSchtasksDay(weeklyDay));
        }

        var result = await RunSchtasksAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        await DeleteTaskIfPresentAsync(TaskName, cancellationToken);
        await DeleteTaskIfPresentAsync(LegacyTaskName, cancellationToken);
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSchtasksAsync(["/Query", "/TN", TaskName, "/FO", "LIST"], cancellationToken);
        return result.ExitCode == 0
            ? "Background update task is installed."
            : "Background update task is not installed.";
    }

    public async Task RunNowAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSchtasksAsync(["/Run", "/TN", TaskName], cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    public Task SendTestNotificationAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ApplicationPath,
            Arguments = BackgroundUpdateNotificationService.TestArgument,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        return Task.CompletedTask;
    }

    private static async Task DeleteTaskIfPresentAsync(string taskName, CancellationToken cancellationToken)
    {
        var result = await RunSchtasksAsync(["/Delete", "/TN", taskName, "/F"], cancellationToken);
        if (result.ExitCode != 0 &&
            !result.Combined.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&
            !result.Combined.Contains("introuvable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private static string ToSchtasksDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        DayOfWeek.Sunday => "SUN",
        _ => throw new ArgumentOutOfRangeException(nameof(day))
    };

    private static async Task<ProcessResult> RunSchtasksAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error)
    {
        public string Combined => Output + Environment.NewLine + Error;
    }
}
