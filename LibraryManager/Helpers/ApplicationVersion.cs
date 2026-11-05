using System.Text.RegularExpressions;

namespace LibraryManager.Helpers;

public static partial class ApplicationVersion
{
    private static readonly Regex ReleaseVersionPattern = ReleaseVersionRegex();

    public static string ToDisplayString(Version version) =>
        $"{version.Major}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";

    public static bool IsReleaseVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ReleaseVersionPattern.IsMatch(value.Trim());

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionRegex();
}
