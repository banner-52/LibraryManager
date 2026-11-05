// Context-aware version detection for website download links.
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace LibraryManager.Services.Detection;

internal sealed record DetectedLinkVersion(string Version, string Source);

internal static class DownloadLinkContext
{
    private static readonly Regex AnchorStartRegex = new(@"<a\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex AnchorEndRegex = new(@"</a\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex TagRegex = new(@"<(?<closing>/)?(?<name>[a-zA-Z][a-zA-Z0-9:-]*)\b[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex VersionRegex = new(@"(?<![0-9])(?:\bversion\s+)?[vV]?(?<version>[0-9]+(?:[._-][0-9]+){1,7}(?:-(?:alpha|beta|rc|preview|nightly|dev|canary)[0-9A-Za-z.-]*)?)(?![0-9A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex InstallerBuildRegex = new(@"\b(?:windows\s+)?installers?\s*[\[(]\s*(?<version>[0-9]{3,6})\s*[\])]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex NumericCandidateRegex = new(@"(?<![0-9])(?<version>[0-9]{2,6})(?![0-9])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase) { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    public static IReadOnlyList<DetectedLinkVersion> FindVersionCandidates(string page, int linkAttributeIndex, int linkAttributeLength, string url, string linkText)
    {
        if (string.IsNullOrWhiteSpace(page)) return [];

        var anchor = FindAnchorRange(page, linkAttributeIndex, linkAttributeLength);
        var containers = FindContainingElements(page, anchor.Start, anchor.Length);
        var regions = new List<(string Text, string Source)>
        {
            (TryGetFileName(url), "Filename"),
            (url, "URL"),
            (linkText, "Link label"),
            (GetText(page, anchor.Start, anchor.Length), "Link element")
        };
        if (containers.Count > 0) regions.Add((GetText(page, containers[0].Start, containers[0].Length), "Link container"));

        var nearbyStart = Math.Max(0, anchor.Start - 500);
        var nearbyEnd = Math.Min(page.Length, anchor.Start + anchor.Length + 500);
        regions.Add((GetText(page, nearbyStart, nearbyEnd - nearbyStart), "Nearby text"));
        if (containers.Count > 1) regions.Add((GetText(page, containers[1].Start, containers[1].Length), "Parent block"));

        var results = new List<DetectedLinkVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in regions)
        {
            foreach (var match in VersionRegex.Matches(region.Text).Cast<Match>()
                         .Concat(InstallerBuildRegex.Matches(region.Text).Cast<Match>())
                         .Concat(NumericCandidateRegex.Matches(region.Text).Cast<Match>()))
            {
                var version = GetVersion(match);
                if (!string.IsNullOrWhiteSpace(version) && seen.Add(version))
                    results.Add(new(version, region.Source));
            }
        }
        return results;
    }

    public static DetectedLinkVersion? FindVersion(string page, int linkAttributeIndex, int linkAttributeLength, string url, string linkText, string? versionPattern = null)
    {
        if (string.IsNullOrWhiteSpace(page)) return null;

        var fileName = Path.GetFileNameWithoutExtension(TryGetFileName(url));
        if (FindInText(fileName, versionPattern) is { } filenameVersion) return new(filenameVersion, "Filename");
        if (FindInText(url, versionPattern) is { } urlVersion) return new(urlVersion, "URL");
        if (FindInText(linkText, versionPattern) is { } labelVersion) return new(labelVersion, "Link label");
        if (FindNumericParentPathSegment(url) is { } pathVersion) return new(pathVersion, "URL path");

        var anchor = FindAnchorRange(page, linkAttributeIndex, linkAttributeLength);
        if (FindInText(GetText(page, anchor.Start, anchor.Length), versionPattern) is { } elementVersion)
            return new(elementVersion, "Link element");

        var containers = FindContainingElements(page, anchor.Start, anchor.Length);
        if (containers.Count > 0 && FindInText(GetText(page, containers[0].Start, containers[0].Length), versionPattern) is { } containerVersion)
            return new(containerVersion, "Link container");

        var nearbyStart = Math.Max(0, anchor.Start - 500);
        var nearbyEnd = Math.Min(page.Length, anchor.Start + anchor.Length + 500);
        if (FindClosestInText(GetText(page, nearbyStart, nearbyEnd - nearbyStart), anchor.Start - nearbyStart, versionPattern) is { } nearbyVersion)
            return new(nearbyVersion, "Nearby text");

        if (containers.Count > 1 && FindInText(GetText(page, containers[1].Start, containers[1].Length), versionPattern) is { } parentVersion)
            return new(parentVersion, "Parent block");

        return null;
    }

    private static string? FindInText(string text, string? versionPattern)
    {
        if (!string.IsNullOrWhiteSpace(versionPattern))
        {
            var configuredMatch = Regex.Match(text, versionPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            if (configuredMatch.Success && GetConfiguredVersion(configuredMatch) is { } configuredVersion) return configuredVersion;
        }

        var fallbackMatch = VersionRegex.Match(text);
        if (fallbackMatch.Success) return GetVersion(fallbackMatch);

        // Some vendors publish a single numeric release/build next to the installer
        // heading (for example "Windows Installers [1016]"). Restrict this fallback
        // to that strong label so years, Windows versions and architecture numbers
        // are not mistaken for application versions.
        var installerBuildMatch = InstallerBuildRegex.Match(text);
        return installerBuildMatch.Success ? GetVersion(installerBuildMatch) : null;
    }

    private static string? FindNumericParentPathSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var parent = segments[^2];
        return Regex.IsMatch(parent, @"^\d{3,6}$", RegexOptions.CultureInvariant) ? parent : null;
    }

    internal static string? GetContentDispositionFileName(ContentDispositionHeaderValue? contentDisposition)
    {
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var normalized = Uri.UnescapeDataString(fileName.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(normalized) ? null : Path.GetFileName(normalized);
    }

    internal static string DetectArchitecture(string fileName, string evidence)
    {
        if (Regex.IsMatch(fileName, @"(?:arm64|aarch64)", RegexOptions.IgnoreCase)) return "ARM64";
        if (Regex.IsMatch(fileName, @"(?:x64|amd64|x86_64|win64)", RegexOptions.IgnoreCase)) return "x64";
        if (Regex.IsMatch(fileName, @"(?:x86|i386|i686|win32|32bit)", RegexOptions.IgnoreCase)) return "x86";
        if (Regex.IsMatch(evidence, @"(?<![0-9])(?:32[- ]?bit|x86|i386)(?![0-9])", RegexOptions.IgnoreCase)) return "x86";
        if (Regex.IsMatch(evidence, @"(?:arm64|aarch64)", RegexOptions.IgnoreCase)) return "ARM64";
        if (Regex.IsMatch(evidence, @"(?:x64|amd64|64[- ]?bit)", RegexOptions.IgnoreCase)) return "x64";
        return "Unknown";
    }

    private static string? FindClosestInText(string text, int referenceIndex, string? versionPattern)
    {
        var configuredMatches = string.IsNullOrWhiteSpace(versionPattern)
            ? []
            : Regex.Matches(text, versionPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)).Cast<Match>()
                .Select(match => (Match: match, Version: GetConfiguredVersion(match)))
                .Where(item => item.Version is not null)
                .ToList();
        if (configuredMatches.Count > 0)
            return configuredMatches.OrderBy(item => Math.Abs(item.Match.Index - referenceIndex)).First().Version;

        var fallbackMatches = VersionRegex.Matches(text).Cast<Match>()
            .Concat(InstallerBuildRegex.Matches(text).Cast<Match>())
            .OrderBy(item => Math.Abs(item.Index - referenceIndex))
            .ToList();
        return fallbackMatches.Count == 0 ? null : GetVersion(fallbackMatches[0]);
    }

    private static string? GetConfiguredVersion(Match match)
    {
        if (match.Groups["version"].Success) return NormalizeVersion(match.Groups["version"].Value);
        if (match.Groups.Count > 1) return NormalizeVersion(match.Groups[1].Value);
        var embeddedVersion = VersionRegex.Match(match.Value);
        return embeddedVersion.Success ? GetVersion(embeddedVersion) : null;
    }

    private static string GetVersion(Match match)
    {
        var value = match.Groups["version"].Success ? match.Groups["version"].Value : match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
        return NormalizeVersion(value);
    }

    private static string NormalizeVersion(string value) => Regex.Replace(value.Trim().TrimStart('v', 'V').Replace('_', '.'), @"(?<=\d)-(?=\d)", ".");

    private static (int Start, int Length) FindAnchorRange(string page, int attributeIndex, int attributeLength)
    {
        var starts = AnchorStartRegex.Matches(page).Cast<Match>().Where(match => match.Index <= attributeIndex).ToList();
        var start = starts.Count == 0 ? attributeIndex : starts[^1].Index;
        var endMatch = AnchorEndRegex.Match(page, Math.Min(page.Length, attributeIndex + attributeLength));
        var end = endMatch.Success ? endMatch.Index + endMatch.Length : Math.Min(page.Length, attributeIndex + attributeLength);
        return (start, Math.Max(0, end - start));
    }

    private static List<(int Start, int Length)> FindContainingElements(string page, int targetStart, int targetLength)
    {
        var open = new Stack<Element>();
        var complete = new List<Element>();
        foreach (Match tag in TagRegex.Matches(page))
        {
            var name = tag.Groups["name"].Value;
            if (tag.Groups["closing"].Success)
            {
                var matching = open.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (matching is null) continue;
                while (open.Count > 0)
                {
                    var item = open.Pop();
                    if (item == matching) { item.End = tag.Index + tag.Length; complete.Add(item); break; }
                }
            }
            else if (!VoidTags.Contains(name) && !tag.Value.EndsWith("/>", StringComparison.Ordinal)) open.Push(new Element(name, tag.Index));
        }

        return complete.Where(item => item.Start <= targetStart && item.End >= targetStart + targetLength && !item.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.End - item.Start).Select(item => (item.Start, item.End - item.Start)).ToList();
    }

    private static string GetText(string page, int start, int length) => WebUtility.HtmlDecode(Regex.Replace(page.Substring(start, length), @"<[^>]+>", " "));

    private static string TryGetFileName(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)) : string.Empty;

    private sealed class Element(string name, int start)
    {
        public string Name { get; } = name;
        public int Start { get; } = start;
        public int End { get; set; }
    }
}
