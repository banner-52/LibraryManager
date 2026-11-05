using System.Diagnostics;

namespace LibraryManager.Updater;

internal static class Program
{
    private const string UpdateLogName = "librarymanager-update.log";

    [STAThread]
    private static int Main(string[] args)
    {
        Dictionary<string, string>? values = null;
        string? backup = null;
        try
        {
            values = ReadArguments(args);
            var source = Path.GetFullPath(values["--source"]);
            var target = Path.GetFullPath(values["--target"]);
            var restart = Path.GetFullPath(values["--restart"]);
            var staging = Directory.GetParent(source)?.FullName;
            ValidateLocations(source, target, restart);

            WriteLog(target, "Updater started.");
            WaitForApplication(values["--wait-pid"]);
            WriteLog(target, "Application stopped; creating rollback backup.");
            backup = CreateBackup(target);
            try
            {
                WriteLog(target, "Backup completed; applying staged update.");
                CopyDirectory(source, target);

                if (!File.Exists(restart))
                    throw new FileNotFoundException("Updated application executable was not found.", restart);

                var restarted = StartApplication(restart, target);
                Thread.Sleep(1500);
                if (restarted.HasExited)
                    throw new InvalidDataException($"The updated application stopped immediately (exit code {restarted.ExitCode}).");

                TryDeleteDirectory(backup);
                backup = null;
                WriteLog(target, "Update completed; rollback backup removed.");
                ScheduleStagingCleanup(staging);
                return 0;
            }
            catch
            {
                WriteLog(target, "Update failed; restoring the previous installation.");
                if (backup is not null)
                {
                    RestoreBackup(backup, target);
                    backup = null;
                }
                if (File.Exists(restart))
                {
                    try { StartApplication(restart, target); }
                    catch (Exception restartError) { WriteLog(target, "Rollback succeeded but restart failed: " + restartError); }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            var target = values is not null && values.TryGetValue("--target", out var path) ? path : Path.GetTempPath();
            WriteLog(target, "Update failed: " + ex);
            if (backup is not null) WriteLog(target, "Rollback backup retained at: " + backup);
            return 1;
        }
    }

    private static Process StartApplication(string executable, string workingDirectory) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidDataException("Could not restart the application.");

    private static void WriteLog(string target, string message)
    {
        try
        {
            Directory.CreateDirectory(target);
            File.AppendAllText(Path.Combine(target, UpdateLogName),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never prevent a recovery attempt.
        }
    }

    private static Dictionary<string, string> ReadArguments(string[] args)
    {
        if (args.Length != 8) throw new InvalidDataException("Invalid updater arguments.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new InvalidDataException("Invalid updater arguments.");
            values.Add(args[i], args[i + 1]);
        }

        foreach (var name in new[] { "--wait-pid", "--source", "--target", "--restart" })
            if (!values.ContainsKey(name)) throw new InvalidDataException("Missing updater argument: " + name);
        return values;
    }

    private static void WaitForApplication(string processId)
    {
        if (!int.TryParse(processId, out var id) || id <= 0) throw new InvalidDataException("Invalid application process ID.");
        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.WaitForExit(30_000)) throw new IOException("The application did not close in time. Cancel the update or close it forcibly, then retry.");
        }
        catch (ArgumentException)
        {
            // The application has already stopped.
        }
    }

    private static void ValidateLocations(string source, string target, string restart)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Staged update files were not found.");
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The application installation folder was not found.");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update staging and installation folders must be separate.");
        if (!restart.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The restart executable must be inside the installation folder.");
    }

    private static string CreateBackup(string target)
    {
        var backup = Path.Combine(Path.GetTempPath(), "LibraryManager", "backups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        try
        {
            CopyDirectory(target, backup);
            return backup;
        }
        catch
        {
            TryDeleteDirectory(backup);
            throw;
        }
    }

    private static void RestoreBackup(string backup, string target)
    {
        var previousFiles = Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(backup, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(target, file);
            if (!previousFiles.Contains(relative) && !relative.Equals(UpdateLogName, StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
        CopyDirectory(backup, target);
        TryDeleteDirectory(backup);
    }

    private static void CopyDirectory(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Files to copy were not found.");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Copy source and target cannot be the same.");

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void ScheduleStagingCleanup(string? staging)
    {
        if (string.IsNullOrWhiteSpace(staging) || !Directory.Exists(staging)) return;
        try
        {
            var command = $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{staging}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = command,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // Temporary update files are non-critical and can be cleaned on the next run.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
