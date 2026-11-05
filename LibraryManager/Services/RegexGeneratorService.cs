using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LibraryManager.Services;

public sealed class RegexGeneratorService
{
    // Exact rule logic from the legacy LibraryManager SourceRuleGenerator.
    private const string VersionToken = @"[vV]?[0-9]+(?:[._-][0-9]+){0,7}(?:[a-zA-Z][a-zA-Z0-9]*|-(?:[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*))?";

    public string? DetectVersion(string sampleUrl)
    {
        if (string.IsNullOrWhiteSpace(sampleUrl)) return null;
        if (!Uri.TryCreate(sampleUrl.Trim(), UriKind.Absolute, out var sample)) return null;
        return Regex.Matches(Uri.UnescapeDataString(sample.AbsoluteUri), VersionToken)
            .Cast<Match>().Select(x => x.Value)
            .Where(IsValidVersion)
            .OrderByDescending(x => x.Length)
            .FirstOrDefault();
    }

    public string GenerateTemplate(string sampleUrl, string version, string? markedVariable = null)
    {
        var template = sampleUrl.Trim();
        if (!template.Contains("{version}", StringComparison.Ordinal))
            template = template.Replace(version, "{version}", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(markedVariable))
            template = template.Replace(markedVariable, "{variable}", StringComparison.Ordinal);
        return template;
    }

    public string GenerateVersionPatternFromUrl(string sampleUrl, string version)
    {
        var uri = new Uri(sampleUrl);
        var fileName = Path.GetFileName(uri.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            var fileTemplate = fileName.Replace(version, "{version}", StringComparison.OrdinalIgnoreCase);
            return Regex.Escape(fileTemplate).Replace(Regex.Escape("{version}"), "(?<version>" + VersionToken + ")", StringComparison.Ordinal);
        }

        // Stable download endpoints may identify a release in query parameters while
        // redirecting to the actual installer. Match that reusable endpoint when no
        // filename carries the version so WebsiteDetection can find it in the page.
        return GenerateVersionPattern(uri.AbsoluteUri, version);
    }

    public string GenerateVersionPattern(string sampleText, string version)
    {
        // This helper is also used for version text visible in HTML, not only URLs.
        var genericVersion = @"(?<version>" + VersionToken + ")";
        if (string.IsNullOrWhiteSpace(sampleText) || string.IsNullOrWhiteSpace(version)) return genericVersion;
        var escapedSample = Regex.Escape(sampleText.Trim());
        var escapedVersion = Regex.Escape(version.Trim());
        return escapedSample.Contains(escapedVersion, StringComparison.Ordinal)
            ? escapedSample.Replace(escapedVersion, genericVersion, StringComparison.Ordinal)
            : genericVersion;
    }

    public string GenerateNearbyVersionPattern()
    {
        // Used when website detection found the release number in the HTML around a
        // download link rather than in the installer URL. Keep this aligned with the
        // context-aware detector so future releases continue to match.
        return @"(?:(?:(?i:\bversion)\s+[vV]?)(?<version>[0-9]+(?:[._-][0-9]+){1,7})(?![0-9._-])|(?:(?i:\b(?:windows\s+)?installers?)\s*[\[(]\s*)(?<version>[0-9]{3,6})(?![0-9])\s*[\])])";
    }

    public string GenerateDownloadPattern(string sampleUrl, string? markedVersion = null, string? markedVariable = null)
    {
        var template = sampleUrl.Trim();
        var version = markedVersion;
        if (string.IsNullOrWhiteSpace(version) && !template.Contains("{version}", StringComparison.Ordinal))
            version = DetectVersion(sampleUrl);
        if (string.IsNullOrWhiteSpace(markedVariable) && !string.IsNullOrWhiteSpace(version))
            template = GeneralizeVersionBranch(template, version);
        if (!string.IsNullOrWhiteSpace(version) && !template.Contains("{version}", StringComparison.Ordinal))
            template = template.Replace(version, "{version}", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(markedVariable) && !template.Contains("{variable}", StringComparison.Ordinal))
            template = template.Replace(markedVariable, "{variable}", StringComparison.Ordinal);
        return "(?<url>" + TemplateToRegex(template) + ")";
    }

    private static string GeneralizeVersionBranch(string template, string version)
    {
        var parts = Regex.Split(version.Trim().TrimStart('v', 'V'), "[._-]");
        if (parts.Length < 3 || parts.Any(part => !int.TryParse(part, out _))) return template;

        var branch = string.Join('.', parts[..^1]);
        return Regex.Replace(
            template,
            $@"(?<=/)[vV]?{Regex.Escape(branch)}(?=/)",
            "{variable}",
            RegexOptions.CultureInvariant);
    }

    private static string TemplateToRegex(string template)
    {
        var output = new StringBuilder();
        for (var index = 0; index < template.Length;)
        {
            if (template.IndexOf("{version}", index, StringComparison.Ordinal) == index)
            {
                output.Append("(?<version>" + VersionToken + ")");
                index += "{version}".Length;
            }
            else if (template.IndexOf("{variable}", index, StringComparison.Ordinal) == index)
            {
                output.Append("[A-Za-z0-9._~%=-]+");
                index += "{variable}".Length;
            }
            else
            {
                output.Append(Regex.Escape(template[index].ToString()));
                index++;
            }
        }
        return output.ToString();
    }

    private static bool IsValidVersion(string value)
    {
        var clean = value.Trim().TrimStart('v', 'V').Replace('_', '.').Replace('-', '.');
        return Version.TryParse(clean, out _) || Regex.IsMatch(clean, @"^\d+(?:\.\d+)*(?:[a-zA-Z][a-zA-Z0-9]*|\.[a-zA-Z0-9]+)*$");
    }
}
