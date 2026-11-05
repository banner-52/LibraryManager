// Service for LibraryManager.
// Load/save the local catalog and handle online catalog/profile operations.
// Keep this file focused on this single responsibility.
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibraryManager.Helpers;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class CatalogService
{
    private readonly string? _catalogPathOverride;

    public CatalogService(string? catalogPathOverride = null)
    {
        _catalogPathOverride = catalogPathOverride;
    }
    private const string CatalogFileName = "catalog.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ObservableCollection<Software> SoftwareItems { get; } = [];
    public string CatalogPath => _catalogPathOverride ?? FileHelper.GetApplicationDataPath(CatalogFileName);

    public async Task LoadAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            SoftwareItems.Clear();
            return;
        }

        if (repositoryPath.StartsWith(@"\\", StringComparison.Ordinal) && !Directory.Exists(repositoryPath))
            throw new IOException($"The network repository '{repositoryPath}' is not currently available.");

        var catalogPath = CatalogPath;
        if (!File.Exists(catalogPath))
        {
            SoftwareItems.Clear();
            await SaveAsync(repositoryPath, cancellationToken);
            return;
        }

        List<Software> software;
        try
        {
            await using var stream = File.OpenRead(catalogPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            software = DeserializeCatalog(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The catalog file '{catalogPath}' contains invalid or unsupported JSON. The file was not modified.", ex);
        }

        SoftwareItems.Clear();
        foreach (var item in software.Where(item => item is not null)) SoftwareItems.Add(item);
    }

    public async Task SaveAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath)) throw new ArgumentException("A repository folder is required.", nameof(repositoryPath));
        Directory.CreateDirectory(repositoryPath);
        var target = CatalogPath;
        var catalogDirectory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(catalogDirectory);
        var temporary = Path.Combine(catalogDirectory, $".{CatalogFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Persist the complete legacy-compatible profile contract, never UI/check state.
                await JsonSerializer.SerializeAsync(stream, SoftwareItems.Select(CatalogProfileMapper.ToProfile).ToList(), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await SaveAsync(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), cancellationToken);
        File.Copy(CatalogPath, destinationPath, true);
    }

    public async Task ImportAsync(string sourcePath, string repositoryPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(sourcePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var imported = DeserializeCatalog(document.RootElement);
        SoftwareItems.Clear();
        foreach (var item in imported) SoftwareItems.Add(item);
        await SaveAsync(repositoryPath, cancellationToken);
        RefreshStoredVersions(repositoryPath, cancellationToken);
    }

    public async Task RebuildFromRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath)) throw new ArgumentException("A repository folder is required.", nameof(repositoryPath));
        if (!Directory.Exists(repositoryPath)) throw new DirectoryNotFoundException($"The package repository '{repositoryPath}' was not found.");

        var detected = new List<Software>();
        foreach (var directory in Directory.EnumerateDirectories(repositoryPath).OrderBy(Path.GetFileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var packages = Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Version = VersionHelper.GetVersionFromPackageName(path) })
                .Where(item => VersionHelper.TryParse(item.Version, out _))
                .OrderByDescending(item => ParseVersion(item.Version!))
                .ToList();

            detected.Add(new Software
            {
                Name = name,
                Source = new SoftwareSource { Kind = SoftwareSourceKind.None },
                LocalVersion = packages.FirstOrDefault()?.Version,
                PackageFileName = packages.FirstOrDefault() is { } latest ? Path.GetFileName(latest.Path) : null
            });
        }

        SoftwareItems.Clear();
        foreach (var software in detected) SoftwareItems.Add(software);
        await SaveAsync(repositoryPath, cancellationToken);
    }

    public void RefreshStoredVersions(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(repositoryPath)) return;

        foreach (var software in SoftwareItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            software.LocalVersion = null;
            software.PackageFileName = null;
            if (!RepositoryFolder.IsSafeFolderName(software.Name)) continue;
            var folder = RepositoryFolder.GetPath(repositoryPath, software.Name);
            if (!Directory.Exists(folder)) continue;

            var latest = Directory.EnumerateFiles(folder, "*.zip")
                .Select(path => new { Path = path, Version = VersionHelper.GetVersionFromPackageName(path) })
                .Where(item => VersionHelper.TryParse(item.Version, out _))
                .OrderByDescending(item => ParseVersion(item.Version!))
                .FirstOrDefault();
            if (latest is null) continue;

            software.LocalVersion = latest.Version;
            software.PackageFileName = Path.GetFileName(latest.Path);
        }
    }

    private static List<Software> DeserializeCatalog(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return [];
            var first = root[0];
            if (TryGetPropertyIgnoreCase(first, "configuration", out _))
                return (root.Deserialize<List<CatalogProfile>>(JsonOptions) ?? []).Select(CatalogProfileMapper.ToSoftware).ToList();
            return root.Deserialize<List<Software>>(JsonOptions) ?? [];
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            // Legacy catalogs used an object wrapper instead of a bare array.
            foreach (var propertyName in new[] { "SoftwareItems", "Software", "Applications", "Apps", "Items" })
            {
                if (TryGetPropertyIgnoreCase(root, propertyName, out var items) && items.ValueKind == JsonValueKind.Array)
                    return DeserializeCatalog(items);
            }
        }

        throw new JsonException("The catalog root must be a software array or a supported legacy catalog object.");
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static Version ParseVersion(string value) => VersionHelper.TryParse(value, out var version) ? version : new Version(0, 0);
}
