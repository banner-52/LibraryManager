// Model for LibraryManager.
// Describe one stored package version.
// Keep this file focused on this single responsibility.
namespace LibraryManager.Models;

public sealed class SoftwareVersion
{
    public string Version { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public string? SelectedAssetFileName { get; set; }
    public string? DetectionMethod { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
