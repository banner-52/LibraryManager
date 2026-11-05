// Service for LibraryManager.
// Create ZIPs, copy to repository, enumerate/delete stored versions, and preserve required package files.
// Keep this file focused on this single responsibility.
using System.IO;
using System.IO.Compression;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class PackageService
{
    public IReadOnlyList<StoredPackageInfo> GetStoredPackages(Software software, string repositoryPath)
    {
        var folder = GetRepositoryFolder(software, repositoryPath);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new StoredPackageInfo
            {
                Version = Path.GetFileNameWithoutExtension(file.Name),
                FileName = file.Name,
                FullPath = file.FullName,
                Modified = file.LastWriteTime,
                Size = FormatSize(file.Length)
            }).ToList();
    }

    public IReadOnlyList<string> GetPackageContents(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToList();
    }

    public void DeletePackage(string packagePath)
    {
        if (File.Exists(packagePath)) File.Delete(packagePath);
    }

    public async Task<string> CreatePackageAsync(Software software, string installerPath, string repositoryPath, IEnumerable<PackageFile>? additionalFiles = null, IProgress<PackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(software.AvailableVersion)) throw new InvalidOperationException("An available version is required before creating a package.");
        var stagingFolder = Path.Combine(Path.GetTempPath(), "LibraryManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingFolder);
        try
        {
            progress?.Report(new PackageProgress("Preparing package in the temporary folder", 0));
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryFolder = GetRepositoryFolder(software, repositoryPath);
            Directory.CreateDirectory(repositoryFolder);
            var contentsFolder = Path.Combine(stagingFolder, "contents");
            Directory.CreateDirectory(contentsFolder);
            var previousPackage = Directory.EnumerateFiles(repositoryFolder, "*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (previousPackage is not null)
            {
                var localPreviousPackage = Path.Combine(stagingFolder, "previous-package.zip");
                progress?.Report(new PackageProgress("Copying previous package to the temporary folder", 5));
                await CopyFileWithProgressAsync(previousPackage, localPreviousPackage, 5, 10, "Copying previous package to the temporary folder", progress, cancellationToken);
                progress?.Report(new PackageProgress("Extracting previous package in the temporary folder", 15));
                await Task.Run(() => ZipFile.ExtractToDirectory(localPreviousPackage, contentsFolder, true), cancellationToken);
                foreach (var oldInstaller in Directory.EnumerateFiles(contentsFolder, "*.*", SearchOption.AllDirectories)
                             .Where(path => string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(path), ".msi", StringComparison.OrdinalIgnoreCase)))
                    File.Delete(oldInstaller);
            }
            progress?.Report(new PackageProgress("Preparing package contents in the temporary folder", 20));

            File.Copy(installerPath, Path.Combine(contentsFolder, GetInstallerFileName(software, installerPath)), true);
            foreach (var file in additionalFiles ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = string.IsNullOrWhiteSpace(file.DestinationPath)
                    ? GetAdditionalFileName(file)
                    : Path.Combine(file.DestinationPath, GetAdditionalFileName(file));
                var target = Path.Combine(contentsFolder, relativePath);
                var contentsRoot = Path.GetFullPath(contentsFolder) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(target).StartsWith(contentsRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An additional file destination points outside the package.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.SourcePath, target, true);
            }
            progress?.Report(new PackageProgress("Package contents ready in the temporary folder", 30));

            var packagePath = Path.Combine(stagingFolder, $"{software.AvailableVersion}.zip");
            await CreateArchiveAsync(contentsFolder, packagePath, progress, cancellationToken);

            var finalPath = Path.Combine(repositoryFolder, Path.GetFileName(packagePath));
            var partialPath = Path.Combine(repositoryFolder, $".{Path.GetFileName(packagePath)}.{Guid.NewGuid():N}.partial");
            try
            {
                progress?.Report(new PackageProgress("Copying completed package to the source folder", 75));
                await CopyFileWithProgressAsync(packagePath, partialPath, 75, 24, "Copying completed package to the source folder", progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partialPath, finalPath, true);
            }
            finally
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
            }
            progress?.Report(new PackageProgress("Package copied to the source folder", 100));
            return finalPath;
        }
        finally
        {
            try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task CreateArchiveAsync(string contentsFolder, string packagePath, IProgress<PackageProgress>? progress, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(contentsFolder, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).ToList();
        var totalBytes = Math.Max(1, files.Sum(file => file.Length));
        long processedBytes = 0;
        await using var packageStream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(contentsFolder, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = entry.Open();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                processedBytes += read;
                progress?.Report(new PackageProgress("Compressing package in the temporary folder", 30 + (int)(processedBytes * 45 / totalBytes), processedBytes, totalBytes));
            }
        }
    }

    private static async Task CopyFileWithProgressAsync(string sourcePath, string destinationPath, int start, int range, string message, IProgress<PackageProgress>? progress, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new PackageProgress(message, start + (int)(copied * range / Math.Max(1, source.Length)), copied, source.Length));
        }
        await destination.FlushAsync(cancellationToken);
    }

    private static string GetRepositoryFolder(Software software, string repositoryPath) =>
        RepositoryFolder.GetPath(repositoryPath, software.Name);

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1} MB"
        : $"{Math.Max(1, bytes / 1024d):F0} KB";

    private static string GetInstallerFileName(Software software, string installerPath)
    {
        var original = Path.GetFileName(installerPath);
        var extension = Path.GetExtension(original);
        var configured = software.InstallerFileName?
            .Replace("{name}", software.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", software.AvailableVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", extension, StringComparison.OrdinalIgnoreCase);
        var fileName = string.IsNullOrWhiteSpace(configured) ? original : configured;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName))) fileName += extension;
        return SanitizeFileName(fileName);
    }

    private static string GetAdditionalFileName(PackageFile file) => string.IsNullOrWhiteSpace(file.DestinationFileName)
        ? Path.GetFileName(file.SourcePath)
        : SanitizeFileName(file.DestinationFileName);

    private static string SanitizeFileName(string fileName)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(character, '_');
        return fileName;
    }
}

public sealed class StoredPackageInfo
{
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public DateTime Modified { get; init; }
    public string Size { get; init; } = string.Empty;
}

public sealed record PackageFile(string SourcePath, string? DestinationPath, string? DestinationFileName = null);
