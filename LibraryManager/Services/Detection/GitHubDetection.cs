// GitHub Releases detection service.
// Detects software versions and downloadable assets from GitHub Releases.
// Authenticated requests must obtain credentials through GitHubAuthService, regardless of whether OAuth or a Personal Access Token is selected.
// This service must not implement login, logout, or credential persistence itself.
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibraryManager.Models;
using LibraryManager.Services;

namespace LibraryManager.Services.Detection;

public sealed class GitHubDetection
{
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthService _authentication;
    private readonly RequestTrackingService _requests;

    public GitHubDetection(GitHubAuthService authentication, RequestTrackingService requests, HttpClient? httpClient = null)
    {
        _authentication = authentication;
        _requests = requests;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SoftwareVersion?> DetectAsync(Software software, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(software.Source.Url)) return null;
        var release = await GetLatestReleaseAsync(software.Source.Url, cancellationToken);
        var assetPattern = software.Source.DownloadPattern;
        var asset = release.Assets.FirstOrDefault(item => string.IsNullOrWhiteSpace(assetPattern) || Regex.IsMatch(item.FileName, assetPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)));
        return new SoftwareVersion
        {
            Version = release.Version,
            DownloadUrl = asset?.DownloadUrl,
            SelectedAssetFileName = asset?.FileName,
            DetectionMethod = "GitHub Releases"
        };
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var repository = GetRepositoryName(repositoryUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LibraryManager", "1.0"));
        _authentication.ApplyAuthorization(request);
        _requests.RecordRequest();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        _authentication.UpdateRateLimitFromResponse(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub release lookup failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var releaseName = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var assets = root.TryGetProperty("assets", out var assetsElement)
            ? assetsElement.EnumerateArray()
                .Select(item => new GitHubReleaseAsset(
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("browser_download_url").GetString() ?? string.Empty,
                    item.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
                .Where(asset => IsSuitableAsset(asset.FileName))
                .ToList()
            : [];
        var version = ExtractVersion(tagName)
            ?? ExtractVersion(releaseName)
            ?? assets.Select(asset => ExtractVersion(asset.FileName)).FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
            ?? throw new InvalidOperationException("GitHub release version could not be detected from the tag, release name, or assets.");
        return new GitHubRelease(version, assets);
    }

    public static string CreateAssetPattern(string fileName, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

        const string versionTokenPattern = @"(?<!\d)[vV]?\d+(?:[._-]\d+){1,7}(?!\d)";
        var matches = Regex.Matches(fileName, versionTokenPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            .Cast<Match>()
            .ToList();

        Match? match = null;
        if (!string.IsNullOrWhiteSpace(version))
        {
            var normalizedTarget = NormalizeVersion(version);
            match = matches.FirstOrDefault(candidate =>
                string.Equals(NormalizeVersion(candidate.Value), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        }

        match ??= matches.FirstOrDefault();
        if (match is null)
            return $"^{Regex.Escape(fileName)}$";

        var prefix = Regex.Escape(fileName[..match.Index]);
        var suffix = Regex.Escape(fileName[(match.Index + match.Length)..]);
        const string reusableVersionPattern = @"[vV]?[0-9]+(?:[._-][0-9]+){1,7}";
        return $"^{prefix}{reusableVersionPattern}{suffix}$";
    }

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v', 'V').Replace('_', '.').Replace('-', '.');

    private static string? ExtractVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?<!\d)[vV]?(?<version>\d+(?:[._-]\d+){1,7})(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["version"].Value.Replace('_', '.').Replace('-', '.') : null;
    }

    private static string GetRepositoryName(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The GitHub source URL must identify a repository.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 3 && segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
            return $"{segments[1]}/{segments[2]}";
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The GitHub source URL must be a github.com repository or api.github.com release URL.");
        if (segments.Length < 2) throw new InvalidOperationException("The GitHub source URL must identify an owner and repository.");
        return $"{segments[0]}/{segments[1]}";
    }

    private static bool IsSuitableAsset(string fileName) => new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip", ".7z", ".rar", ".tar", ".gz", ".deb", ".rpm", ".appimage", ".dmg", ".pkg" }
        .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

public sealed record GitHubRelease(string Version, IReadOnlyList<GitHubReleaseAsset> Assets);
public sealed record GitHubReleaseAsset(string FileName, string DownloadUrl, long SizeBytes)
{
    public string Type => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    public string Size => SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024d:0.0} KB" : $"{SizeBytes / 1024d / 1024d:0.0} MB";
}
