// Detection service for LibraryManager.
// Website/Regex version and installer-link detection.
// Keep this file focused on this single responsibility.
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Net;
using System.IO;
using LibraryManager.Models;

namespace LibraryManager.Services.Detection;

public sealed class WebsiteDetection
{
    private static readonly Regex LinkAttributeRegex = new(
        @"(?:href|src)\s*=\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(10));

    private readonly HttpClient _httpClient;
    private readonly Func<Uri, CancellationToken, Task<string>>? _renderedPageReader;

    public WebsiteDetection(
        HttpClient? httpClient = null,
        Func<Uri, CancellationToken, Task<string>>? renderedPageReader = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _renderedPageReader = renderedPageReader;
    }

    public async Task<SoftwareVersion?> DetectAsync(Software software, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(software.Source.Url) || string.IsNullOrWhiteSpace(software.Source.VersionPattern)) return null;
        var sourceUri = new Uri(software.Source.Url);
        var directInstaller = IsInstallerUri(sourceUri);
        var page = string.Empty;
        var pageUri = sourceUri;
        if (!directInstaller)
        {
            try
            {
                using var response = await _httpClient.GetAsync(sourceUri, cancellationToken);
                response.EnsureSuccessStatusCode();
                page = await response.Content.ReadAsStringAsync(cancellationToken);
                pageUri = response.RequestMessage?.RequestUri ?? sourceUri;
            }
            catch (HttpRequestException) when (_renderedPageReader is not null)
            {
                var fallbackPage = await _renderedPageReader(sourceUri, cancellationToken);
                return DetectFromPage(software, fallbackPage, sourceUri, false);
            }
        }
        var detected = DetectFromPage(software, page, pageUri, directInstaller);
        if (directInstaller || _renderedPageReader is null ||
            (detected is not null && (string.IsNullOrWhiteSpace(software.Source.DownloadPattern) || detected.DownloadUrl is not null)))
            return detected;

        var renderedPage = await _renderedPageReader(pageUri, cancellationToken);
        return DetectFromPage(software, renderedPage, pageUri, false);
    }

    private static SoftwareVersion? DetectFromPage(Software software, string page, Uri pageUri, bool directInstaller)
    {
        string? downloadUrl = directInstaller ? pageUri.AbsoluteUri : null;
        Match? downloadLink = null;
        if (downloadUrl is null && !string.IsNullOrWhiteSpace(software.Source.DownloadPattern))
            downloadUrl = FindMatchingLink(page, pageUri, software.Source.DownloadPattern, out downloadLink);

        var contextualVersion = downloadLink is null ? null : DownloadLinkContext.FindVersion(
            page, downloadLink.Index, downloadLink.Length, downloadLink.Groups["url"].Value, string.Empty, software.Source.VersionPattern);
        var match = Regex.Match(page, software.Source.VersionPattern!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
        if (!match.Success)
            match = Regex.Match(pageUri.AbsoluteUri, software.Source.VersionPattern!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
        if (!match.Success && contextualVersion is null) return null;
        var version = contextualVersion?.Version ?? (match.Groups["version"].Success ? match.Groups["version"].Value : match.Groups.Count > 1 ? match.Groups[1].Value : match.Value);
        // Legacy profiles commonly have one expression containing both named groups.
        // Preserve that fast, direct route before attempting a second page scan.
        if (downloadUrl is null && match.Groups["url"].Success)
            downloadUrl = ResolveUrl(pageUri, match.Groups["url"].Value);
        if (downloadUrl is null && !string.IsNullOrWhiteSpace(software.Source.DownloadUrlTemplate))
            downloadUrl = ResolveUrl(pageUri, software.Source.DownloadUrlTemplate.Replace("{version}", version, StringComparison.Ordinal));
        if (downloadUrl is null && !string.IsNullOrWhiteSpace(software.Source.DownloadPattern))
        {
            var downloadMatch = FindDownloadMatch(page, software.Source.DownloadPattern);
            if (downloadUrl is null && downloadMatch.Success)
            {
                var rawUrl = downloadMatch.Groups["url"].Success ? downloadMatch.Groups["url"].Value : downloadMatch.Groups.Count > 1 ? downloadMatch.Groups[1].Value : downloadMatch.Value;
                downloadUrl = ResolveUrl(pageUri, rawUrl);
            }
        }
        var fileName = string.IsNullOrWhiteSpace(software.Source.DownloadFileNameTemplate)
            ? null
            : software.Source.DownloadFileNameTemplate.Replace("{version}", version, StringComparison.Ordinal);
        return new SoftwareVersion { Version = version, DownloadUrl = downloadUrl, SelectedAssetFileName = fileName, DetectionMethod = "Website" };
    }

    private static string? FindMatchingLink(string page, Uri sourceUri, string pattern, out Match? matchingLink)
    {
        matchingLink = null;
        foreach (Match link in LinkAttributeRegex.Matches(page))
        {
            var rawCandidate = WebUtility.HtmlDecode(link.Groups["url"].Value);
            var normalizedCandidate = ResolveUrl(sourceUri, rawCandidate);
            if (MatchesDownloadPattern(rawCandidate, pattern) ||
                (normalizedCandidate is not null && MatchesDownloadPattern(normalizedCandidate, pattern)))
            {
                matchingLink = link;
                return normalizedCandidate;
            }
        }
        return null;
    }

    private static bool MatchesDownloadPattern(string candidate, string pattern) =>
        Regex.IsMatch(candidate, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));

    private static Match FindDownloadMatch(string page, string pattern)
    {
        var match = Regex.Match(page, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
        return match.Success ? match : Regex.Match(WebUtility.HtmlDecode(page), pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
    }

    private static string? ResolveUrl(Uri sourceUri, string rawUrl)
    {
        rawUrl = WebUtility.HtmlDecode(rawUrl).Trim('"', '\'', ' ');
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.AbsoluteUri;
        return Uri.TryCreate(sourceUri, rawUrl, out var relative) ? relative.AbsoluteUri : null;
    }

    private static bool IsInstallerUri(Uri uri) => new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip" }
        .Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);
}
