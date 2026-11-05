using System.Text.Json.Serialization;

namespace LibraryManager.Models;

// The persisted and published contract. Runtime-only state remains on Software and is
// intentionally absent here: package ZIP names are the authoritative local versions.
public sealed class CatalogProfile
{
    public string Id { get; set; } = "";
    public string? SoftwareId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Publisher { get; set; } = "";
    public bool Favorite { get; set; }
    public bool UpdateNotificationEnabled { get; set; }
    public ProfileConfiguration Configuration { get; set; } = new();
}

public sealed class ProfileConfiguration
{
    public string Folder { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string InitialVersion { get; set; } = "";
    public SoftwareSourceKind Source { get; set; }
    public string SourceUrl { get; set; } = "";
    public string WinGetPackageId { get; set; } = "";
    public DownloadMethod DownloadMethod { get; set; } = DownloadMethod.SameAsCheck;
    public string DownloadSourceUrl { get; set; } = "";
    public string DownloadAssetPattern { get; set; } = @"(?i)\.(exe|msi|msix|msixbundle|appx|zip)$";
    public string DownloadVersionPattern { get; set; } = "";
    public string DeliveryUrlTemplate { get; set; } = "";
    public string DeliveryFileNameTemplate { get; set; } = "";
    public bool DeliveryStableSourceConfirmed { get; set; }
    public string DownloadWinGetPackageId { get; set; } = "";
    public string AssetPattern { get; set; } = @"(?i)\.(exe|msi|msix|msixbundle|appx|zip)$";
    public string VersionPattern { get; set; } = "";
    public string DownloadUrlTemplate { get; set; } = "";
    public string DownloadFileNameTemplate { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public bool StableSourceConfirmed { get; set; }
    public string InstallerEntry { get; set; } = "";
    public bool PreserveInstallerName { get; set; }
    public string SilentInstallCommand { get; set; } = "";
    public string SilentUninstallCommand { get; set; } = "";
    public List<AdditionalFile> AdditionalFiles { get; set; } = [];
}

public static class CatalogProfileMapper
{
    public static CatalogProfile ToProfile(Software software) => new()
    {
        Id = software.Id.ToString(), SoftwareId = software.SoftwareId, Name = software.Name, Description = software.Description ?? "", Publisher = software.Publisher ?? "", Favorite = software.IsFavorite, UpdateNotificationEnabled = software.IsUpdateNotificationEnabled,
        Configuration = new ProfileConfiguration
        {
            Folder = software.Name, IconUrl = software.IconPath ?? "", Source = software.Source.Kind,
            SourceUrl = software.Source.Url ?? "", WinGetPackageId = software.Source.PackageId ?? "",
            DownloadMethod = software.Source.DownloadMethod, DownloadSourceUrl = software.Source.DownloadSourceUrl ?? "",
            DownloadAssetPattern = software.Source.DownloadAssetPattern ?? software.Source.DownloadPattern ?? "",
            DownloadVersionPattern = software.Source.DownloadVersionPattern ?? "",
            DeliveryUrlTemplate = software.Source.DeliveryUrlTemplate ?? "", DeliveryFileNameTemplate = software.Source.DeliveryFileNameTemplate ?? "",
            DeliveryStableSourceConfirmed = software.Source.DeliveryStableSourceConfirmed, DownloadWinGetPackageId = software.Source.DownloadWinGetPackageId ?? "",
            AssetPattern = software.Source.DownloadPattern ?? "", VersionPattern = software.Source.VersionPattern ?? "",
            DownloadUrlTemplate = software.Source.DownloadUrlTemplate ?? "", DownloadFileNameTemplate = software.Source.DownloadFileNameTemplate ?? "", ExpectedSha256 = software.Source.ExpectedSha256 ?? "",
            StableSourceConfirmed = software.Source.StableSourceConfirmed, InstallerEntry = software.InstallerFileName ?? software.Source.InstallerEntry ?? "",
            PreserveInstallerName = software.KeepInstallerFilename, SilentInstallCommand = software.SilentInstallCommand ?? "",
            SilentUninstallCommand = software.SilentUninstallCommand ?? "", AdditionalFiles = software.AdditionalFiles.Select(x => x.Copy()).ToList()
        }
    };

    public static Software ToSoftware(CatalogProfile profile)
    {
        var c = profile.Configuration ?? new ProfileConfiguration();
        return new Software
        {
            Id = Guid.TryParse(profile.Id, out var id) ? id : Guid.NewGuid(), SoftwareId = profile.SoftwareId, Description = profile.Description, Publisher = profile.Publisher,
            Name = string.IsNullOrWhiteSpace(c.Folder) ? profile.Name : c.Folder, IsFavorite = profile.Favorite, IsUpdateNotificationEnabled = profile.UpdateNotificationEnabled,
            IconPath = c.IconUrl, KeepInstallerFilename = c.PreserveInstallerName, InstallerFileName = c.InstallerEntry,
            SilentInstallCommand = c.SilentInstallCommand,
            SilentUninstallCommand = c.SilentUninstallCommand, AdditionalFiles = c.AdditionalFiles?.Select(x => x.Copy()).ToList() ?? [],
            Source = new SoftwareSource { Kind = c.Source, Url = c.SourceUrl, PackageId = c.WinGetPackageId, VersionPattern = c.VersionPattern,
                DownloadPattern = c.AssetPattern, DownloadMethod = c.DownloadMethod, DownloadSourceUrl = c.DownloadSourceUrl,
                DownloadAssetPattern = c.DownloadAssetPattern, DownloadVersionPattern = c.DownloadVersionPattern,
                DeliveryUrlTemplate = c.DeliveryUrlTemplate, DeliveryFileNameTemplate = c.DeliveryFileNameTemplate,
                DeliveryStableSourceConfirmed = c.DeliveryStableSourceConfirmed, DownloadWinGetPackageId = c.DownloadWinGetPackageId,
                DownloadUrlTemplate = c.DownloadUrlTemplate, DownloadFileNameTemplate = c.DownloadFileNameTemplate, ExpectedSha256 = c.ExpectedSha256,
                StableSourceConfirmed = c.StableSourceConfirmed, InstallerEntry = c.InstallerEntry }
        };
    }
}
