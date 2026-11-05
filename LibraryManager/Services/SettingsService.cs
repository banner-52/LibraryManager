// Service for LibraryManager.
// Load/save AppSettings and defaults.
// Keep this file focused on this single responsibility.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using LibraryManager.Helpers;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null) => _settingsPath = settingsPath ?? FileHelper.GetApplicationDataPath(SettingsFileName);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = _settingsPath;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var path = _settingsPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
