// Model for LibraryManager.
// Describe a LibraryManager self-update result/release.
// Keep this file focused on this single responsibility.
namespace LibraryManager.Models;

public sealed class ApplicationUpdate
{
    public string Version { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
}
