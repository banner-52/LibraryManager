// Model for LibraryManager.
// Describe one configured detection/download source.
// Keep this file focused on this single responsibility.
namespace LibraryManager.Models;

public sealed class SoftwareSource
{
    public SoftwareSourceKind Kind { get; set; } = SoftwareSourceKind.None;
    public string? Url { get; set; }
    public string? PackageId { get; set; }
    public string? VersionPattern { get; set; }
    public string? DownloadPattern { get; set; }
    // Legacy rich-profile fields. They are deliberately retained because a check source
    // and its delivery source are not always the same service.
    public DownloadMethod DownloadMethod { get; set; } = DownloadMethod.SameAsCheck;
    public string? DownloadSourceUrl { get; set; }
    public string? DownloadAssetPattern { get; set; }
    public string? DownloadVersionPattern { get; set; }
    public string? DeliveryUrlTemplate { get; set; }
    public string? DeliveryFileNameTemplate { get; set; }
    public bool DeliveryStableSourceConfirmed { get; set; }
    public string? DownloadWinGetPackageId { get; set; }
    public string? DownloadUrlTemplate { get; set; }
    public string? DownloadFileNameTemplate { get; set; }
    public string? ExpectedSha256 { get; set; }
    public bool StableSourceConfirmed { get; set; }
    public string? InstallerEntry { get; set; }

    public SoftwareSource Copy() => new()
    {
        Kind = Kind, Url = Url, PackageId = PackageId,
        VersionPattern = VersionPattern, DownloadPattern = DownloadPattern,
        DownloadMethod = DownloadMethod, DownloadSourceUrl = DownloadSourceUrl,
        DownloadAssetPattern = DownloadAssetPattern, DownloadVersionPattern = DownloadVersionPattern,
        DeliveryUrlTemplate = DeliveryUrlTemplate, DeliveryFileNameTemplate = DeliveryFileNameTemplate,
        DeliveryStableSourceConfirmed = DeliveryStableSourceConfirmed, DownloadWinGetPackageId = DownloadWinGetPackageId,
        DownloadUrlTemplate = DownloadUrlTemplate, DownloadFileNameTemplate = DownloadFileNameTemplate, ExpectedSha256 = ExpectedSha256,
        StableSourceConfirmed = StableSourceConfirmed, InstallerEntry = InstallerEntry
    };

    public override string ToString() => Kind switch
    {
        SoftwareSourceKind.GitHubReleases => "GitHub Releases",
        SoftwareSourceKind.WinGet => "WinGet",
        SoftwareSourceKind.Website => "Website",
        _ => "Manual"
    };
}

public enum SoftwareSourceKind
{
    None,
    Website,
    GitHubRelease,
    WinGet,
    // Legacy catalogue name. WebRegex uses the same website detection engine.
    WebRegex = Website,
    GitHubReleases = GitHubRelease
}
public enum DownloadMethod { SameAsCheck, GitHubRelease, Website, WinGet }
