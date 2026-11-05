// Detection service for LibraryManager.
// Winget search, version detection, and source operations.
// Keep this file focused on this single responsibility.
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO;
using LibraryManager.Models;
using LibraryManager.Services;

namespace LibraryManager.Services.Detection;

public sealed class WingetDetection
{
    public async Task<SoftwareVersion?> DetectAsync(Software software, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(software.Source.PackageId)) return null;
        var startInfo = new ProcessStartInfo("winget", $"show --id \"{software.Source.PackageId}\" --exact --source winget --versions --accept-source-agreements --disable-interactivity")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return null;
        var version = ParseAvailableVersions(output).FirstOrDefault();
        return version is null ? null : new SoftwareVersion { Version = version, DetectionMethod = "WinGet" };
    }

    public async Task<IReadOnlyList<WinGetPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var searchTerm = query.Trim();
        var startInfo = new ProcessStartInfo("winget", $"search --query \"{searchTerm.Replace("\"", "") }\" --source winget --accept-source-agreements --disable-interactivity")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start WinGet.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "WinGet search failed." : error.Trim());
        return FilterAndRankSearchResults(ParseSearchResults(output), searchTerm);
    }

    public static IReadOnlyList<WinGetPackage> FilterAndRankSearchResults(IEnumerable<WinGetPackage> packages, string query)
    {
        var searchTerm = query.Trim();
        if (searchTerm.Length == 0) return [];

        return packages
            .Select(package => new
            {
                Package = package,
                Rank = GetSearchRank(package, searchTerm)
            })
            .Where(match => match.Rank is not null)
            .OrderBy(match => match.Rank)
            .Select(match => match.Package)
            .ToList();
    }

    private static int? GetSearchRank(WinGetPackage package, string searchTerm)
    {
        if (package.Id.Equals(searchTerm, StringComparison.OrdinalIgnoreCase)) return 0;
        if (package.Id.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase)) return 1;
        if (package.Name.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase)) return 2;
        if (package.Id.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) return 3;
        if (package.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) return 4;
        return null;
    }

    public static IReadOnlyList<WinGetPackage> ParseSearchResults(string output)
    {
        var results = new List<WinGetPackage>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var separatorIndex = Array.FindIndex(lines, line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 5 && trimmed.All(character => character == '-' || char.IsWhiteSpace(character));
        });
        if (separatorIndex < 0) return [];

        // WinGet columns are fixed-width. The separator row itself is a continuous
        // sequence of dashes, so it does not expose those widths. Read the starts of
        // header columns instead; this remains independent of their localized names.
        var headerIndex = separatorIndex - 1;
        if (headerIndex < 0) return [];
        var columnStarts = Regex.Matches(lines[headerIndex], @"\S(?:.*?\S)?(?=\s{2,}|$)")
            .Select(match => match.Index)
            .ToArray();
        if (columnStarts.Length < 3) return [];

        foreach (var raw in lines.Skip(separatorIndex + 1))
        {
            var line = raw.TrimEnd();
            string Slice(int column)
            {
                if (column >= columnStarts.Length || columnStarts[column] >= line.Length) return "";
                var start = columnStarts[column];
                var end = column + 1 < columnStarts.Length ? Math.Min(columnStarts[column + 1], line.Length) : line.Length;
                return line[start..end].Trim();
            }

            var name = Slice(0);
            var id = Slice(1);
            var version = Slice(2);
            if (name.Length == 0 || id.Length == 0 || version.Length == 0) continue;
            results.Add(new WinGetPackage(name, id, version));
        }

        return results.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).Take(300).ToList();
    }

    public static IReadOnlyList<string> ParseAvailableVersions(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var separatorIndex = Array.FindIndex(lines, line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 2 && trimmed.All(character => character == '-');
        });
        if (separatorIndex < 0) return [];

        return lines.Skip(separatorIndex + 1)
            .Select(line => line.Trim())
            .Where(version => version.Length > 0)
            .ToList();
    }

    public async Task<string> DownloadAsync(Software software, string version, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(software.Source.PackageId)) throw new InvalidOperationException("A WinGet package ID is required.");
        var folder = Path.Combine(Path.GetTempPath(), "LibraryManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var arguments = $"download --id \"{software.Source.PackageId}\" --exact --version \"{version}\" --download-directory \"{folder}\" --accept-source-agreements --disable-interactivity";
        var startInfo = new ProcessStartInfo("winget", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start WinGet.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloadedBytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => { try { return new FileInfo(path).Length; } catch (IOException) { return 0L; } })
                .Sum();
            progress?.Report(new DownloadProgress(downloadedBytes, null, 0));
            await Task.Delay(250, cancellationToken);
        }
        await process.WaitForExitAsync(cancellationToken);
        await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "WinGet download failed." : error.Trim());
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path => new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("WinGet did not produce a supported installer file.");
    }
}

public sealed record WinGetPackage(string Name, string Id, string Version);
