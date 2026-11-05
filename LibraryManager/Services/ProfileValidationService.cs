using System.IO;
using System.Text.RegularExpressions;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class ProfileValidationService
{
    public IReadOnlyList<string> Validate(Software software)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(software.Name)) errors.Add("Software name is required.");
        else if (!RepositoryFolder.IsSafeFolderName(software.Name)) errors.Add("Software name must be a single safe folder name.");

        var source = software.Source;
        if (source.Kind is SoftwareSourceKind.Website or SoftwareSourceKind.GitHubReleases)
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                errors.Add("The source URL must be a valid HTTPS address.");
        }

        if (source.Kind == SoftwareSourceKind.Website)
        {
            var directInstaller = Uri.TryCreate(source.Url, UriKind.Absolute, out var websiteUri) &&
                                  new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip" }.Contains(Path.GetExtension(websiteUri.AbsolutePath), StringComparer.OrdinalIgnoreCase);
            ValidateRegex(source.VersionPattern, "Version pattern", errors, !directInstaller);
            ValidateRegex(source.DownloadPattern, "Download pattern", errors, !directInstaller);
        }

        if (source.Kind == SoftwareSourceKind.GitHubReleases)
            ValidateRegex(source.DownloadPattern, "Asset filename pattern", errors, false);

        if (source.Kind == SoftwareSourceKind.WinGet && string.IsNullOrWhiteSpace(source.PackageId))
            errors.Add("A WinGet package ID is required.");

        if (!string.IsNullOrWhiteSpace(source.ExpectedSha256) && !IsSha256(source.ExpectedSha256))
            errors.Add("The expected SHA-256 must contain exactly 64 hexadecimal characters.");

        foreach (var file in software.AdditionalFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Name)) errors.Add("Each additional file requires a name.");
            if (!Uri.TryCreate(file.SourceUrl, UriKind.Absolute, out var fileUri) || fileUri.Scheme != Uri.UriSchemeHttps)
                errors.Add($"Additional file '{file.Name}' requires a valid HTTPS URL.");
            if (!string.IsNullOrWhiteSpace(file.DestinationPath) && (Path.IsPathRooted(file.DestinationPath) || file.DestinationPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")))
                errors.Add($"Additional file '{file.Name}' has an unsafe destination path.");
            if (!string.IsNullOrWhiteSpace(file.DestinationFileName) && file.DestinationFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                errors.Add($"Additional file '{file.Name}' has an invalid destination filename.");
        }
        if (!string.IsNullOrWhiteSpace(software.InstallerFileName) && software.InstallerFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add("The installer filename template contains invalid filename characters.");
        return errors;
    }

    private static void ValidateRegex(string? pattern, string label, ICollection<string> errors, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            if (required) errors.Add($"{label} is required.");
            return;
        }
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException) { errors.Add($"{label} is not a valid regular expression."); }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
