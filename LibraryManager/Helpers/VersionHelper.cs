// Helper for LibraryManager.
// Generic version parsing/comparison only.
// Keep this file focused on this single responsibility.
using System.IO;

namespace LibraryManager.Helpers;

public static class VersionHelper
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim().TrimStart('v', 'V');
        // Some publishers, such as 7-Zip, use a single numeric release number.
        // System.Version requires at least a major and minor component.
        if (!normalized.Contains('.', StringComparison.Ordinal)) normalized += ".0";
        if (!Version.TryParse(normalized, out var parsed)) return false;
        version = parsed!;
        return true;
    }

    public static string? GetVersionFromPackageName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(?<!\d)(\d+(?:\.\d+){0,3})(?!\d)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
