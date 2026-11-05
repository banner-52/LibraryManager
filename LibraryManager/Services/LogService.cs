// Service for LibraryManager.
// Centralize activity, error, and operation logging.
// Keep this file focused on this single responsibility.
using System.Globalization;
using System.IO;
using System.Text;
using LibraryManager.Helpers;

namespace LibraryManager.Services;

public sealed class LogService
{
    private readonly string _logPath = FileHelper.GetApplicationDataPath("library-manager.log");

    public async Task WriteAsync(string message, Exception? exception = null)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        if (exception is not null) line += $"{Environment.NewLine}{exception}";
        await File.AppendAllTextAsync(_logPath, line + Environment.NewLine);
    }

    public async Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0 || !File.Exists(_logPath)) return;

        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        var lines = await File.ReadAllLinesAsync(_logPath, cancellationToken);
        var kept = new StringBuilder();
        var keepCurrentEntry = true;

        foreach (var line in lines)
        {
            if (TryGetEntryTimestamp(line, out var timestamp))
                keepCurrentEntry = timestamp >= cutoff;

            if (keepCurrentEntry)
                kept.AppendLine(line);
        }

        await File.WriteAllTextAsync(_logPath, kept.ToString(), cancellationToken);
    }

    private static bool TryGetEntryTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var separator = line.IndexOf(' ');
        if (separator <= 0) return false;

        return DateTimeOffset.TryParse(
            line[..separator],
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);
    }
}
