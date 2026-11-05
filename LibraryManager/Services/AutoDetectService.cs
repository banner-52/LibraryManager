using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;
using LibraryManager.Models;
using LibraryManager.Services.Detection;

namespace LibraryManager.Services;

public sealed class AutoDetectService
{
    private static readonly Regex LinkRegex = new(@"href\s*=\s*(?:""(?<url>[^""]+)""|'(?<url>[^']+)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly string[] InstallerExtensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip", ".7z", ".tar", ".gz", ".deb", ".rpm", ".appimage", ".dmg", ".pkg", ".msi"];
    private readonly HttpClient _httpClient = new();
    private readonly RegexGeneratorService _generator = new();

    public async Task<AutoDetectResult> DetectAsync(string softwareName, string sourceUrl, string? knownVersion = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = await DetectAllAsync(softwareName, sourceUrl, knownVersion, progress, cancellationToken);
        return results.First();
    }

    public async Task<IReadOnlyList<AutoDetectResult>> DetectAllAsync(string softwareName, string sourceUrl, string? knownVersion = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a valid HTTP or HTTPS URL.", nameof(sourceUrl));

        var results = new List<AutoDetectResult>();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LibraryManager/2.0");
        progress?.Report("Checking for a GitHub repository...");
        var github = TryGetGitHubRepository(source);
        if (github is not null)
        {
            try
            {
                var release = await new GitHubDetection(AppServices.GitHubAuthentication, AppServices.Requests).GetLatestReleaseAsync(github, cancellationToken);
                foreach (var asset in RankGitHubAssets(release.Assets, softwareName).Take(60))
                {
                    results.Add(new AutoDetectResult
                    {
                        Method = SoftwareSourceKind.GitHubReleases, SourceUrl = github, Version = release.Version,
                        DownloadUrl = asset.DownloadUrl, DownloadPattern = null,
                        Confidence = 100, Summary = "GitHub Releases was detected and the latest Windows installer asset was selected."
                    });
                }
            }
            catch { /* Continue with website discovery when the repository is not usable as a release source. */ }
        }

        progress?.Report("Reading the website...");
        var pages = new List<PageSnapshot>();
        await AddPageAsync(source, pages, cancellationToken);
        foreach (var link in ExtractLinks(pages[0].Html, pages[0].Uri)
                     .Where(x => IsLikelyDownloadPage(x, source))
                     .DistinctBy(x => x.AbsoluteUri)
                     .Take(5))
        {
            progress?.Report($"Exploring {link.Host}{link.AbsolutePath}...");
            await AddPageAsync(link, pages, cancellationToken);
        }

        if (github is null)
        {
            progress?.Report("Looking for linked GitHub repositories...");
            var repositories = pages
                .SelectMany(page => ExtractLinks(page.Html, page.Uri))
                .Select(TryGetGitHubRepository)
                .Where(repository => repository is not null)
                .Select(repository => repository!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(repository => new
                {
                    Url = repository,
                    Score = ScoreGitHubRepository(repository, softwareName)
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .Take(8)
                .ToList();

            foreach (var candidate in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Checking GitHub releases: {candidate.Url}...");
                try
                {
                    var release = await new GitHubDetection(AppServices.GitHubAuthentication, AppServices.Requests)
                        .GetLatestReleaseAsync(candidate.Url, cancellationToken);
                    foreach (var asset in RankGitHubAssets(release.Assets, softwareName).Take(60))
                    {
                        results.Add(new AutoDetectResult
                        {
                            Method = SoftwareSourceKind.GitHubReleases,
                            SourceUrl = candidate.Url,
                            Version = release.Version,
                            DownloadUrl = asset.DownloadUrl,
                            Confidence = Math.Clamp(70 + candidate.Score, 0, 95),
                            Summary = "A matching GitHub repository was discovered from the website."
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress?.Report($"GitHub repository {candidate.Url} unavailable: {ex.Message}");
                }
            }
        }

        progress?.Report("Scoring installer candidates...");
        var candidates = pages.SelectMany(page => ExtractInstallerCandidates(page, softwareName)).OrderByDescending(x => x.Score).ToList();
        candidates = await ResolveIndirectCandidatesAsync(candidates, progress, cancellationToken);
        progress?.Report("Checking dynamically rendered download links...");
        try
        {
            var html = await BrowserPageReader.ReadHtmlAsync(source, cancellationToken);
            var rendered = new PageSnapshot(source, html);
            pages.Add(rendered);
            var dynamicCandidates = ExtractInstallerCandidates(rendered, softwareName)
                .OrderByDescending(item => item.Score).ToList();
            dynamicCandidates = await ResolveIndirectCandidatesAsync(dynamicCandidates, progress, cancellationToken);
            candidates = candidates.Concat(dynamicCandidates)
                .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.Score).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            progress?.Report("Dynamic page inspection unavailable; continuing with other sources.");
        }
        if (candidates.Count > 0)
        {
            foreach (var candidate in candidates
                .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Take(8))
            {
                try
                {
                    var versionEvidenceUrl = string.Join(" ", new[] { candidate.ResolvedFileName, candidate.ResolvedUrl, candidate.Url }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                    var version = candidate.ContextualVersion?.Version
                        ?? DetectBestVersion(versionEvidenceUrl, candidate.Context, knownVersion)
                        ?? DetectLocalVersion(candidate.Context, versionEvidenceUrl);
                    var versionFromUrl = !string.IsNullOrWhiteSpace(version)
                        && candidate.Url.Contains(version, StringComparison.OrdinalIgnoreCase);
                    var downloadPattern = _generator.GenerateDownloadPattern(candidate.Url, version);
                    var versionPattern = versionFromUrl
                        ? _generator.GenerateVersionPatternFromUrl(candidate.Url, version!)
                        : _generator.GenerateNearbyVersionPattern();
                    results.Add(new AutoDetectResult
                    {
                        Method = SoftwareSourceKind.Website,
                        SourceUrl = candidate.PageUrl,
                        Version = version,
                        DownloadUrl = candidate.Url,
                        VersionPattern = null,
                        DownloadPattern = null,
                        Confidence = Math.Clamp(candidate.Score + (version is null ? 0 : 15), 0, 99),
                        Evidence = string.Join(" ", new[] { candidate.ArchitectureEvidence, candidate.ResolvedFileName, candidate.Context }
                            .Where(value => !string.IsNullOrWhiteSpace(value))),
                        Summary = candidate.IsIndirect
                            ? "Indirect download endpoint. Review the reusable pattern and resolved file."
                            : "Website download candidate. Review the version and reusable rules."
                    });
                }
                catch (ArgumentException) { /* A candidate without a usable filename cannot produce a reusable rule. */ }
                catch (InvalidOperationException) { }
            }
        }

        progress?.Report("Checking WinGet...");
        var wingetResult = await TryDetectWinGetAsync(softwareName, knownVersion, cancellationToken);
        if (wingetResult is not null) results.Add(wingetResult);

        var ordered = results
            .DistinctBy(result => result.Method + "|" + (result.PackageId ?? result.DownloadUrl ?? result.SourceUrl),
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(result => result.Confidence)
            .ToList();
        if (ordered.Count == 0)
            throw new InvalidOperationException("No reliable update source was detected. Try another official download page.");
        return ordered.Select(WithPlatform).ToList();
    }

    private static AutoDetectResult WithPlatform(AutoDetectResult result)
    {
        var file = result.DownloadUrl is null ? "" : Uri.UnescapeDataString(new Uri(result.DownloadUrl).AbsolutePath);
        var name = Path.GetFileName(file).ToLowerInvariant();
        var evidence = result.Evidence ?? string.Empty;
        var fileEvidence = name + " " + evidence;
        var platform = Regex.IsMatch(fileEvidence, @"(?:linux|ubuntu|debian|fedora|\\.deb(?:$|\s)|\\.rpm(?:$|\s)|\\.appimage(?:$|\s))", RegexOptions.IgnoreCase) ? "Linux"
            : Regex.IsMatch(fileEvidence, @"(?:macos|darwin|osx|mac[-_.]|\\.dmg(?:$|\s)|\\.pkg(?:$|\s))", RegexOptions.IgnoreCase) ? "macOS"
            : Regex.IsMatch(fileEvidence, @"(?:windows|win32|win64|win[-_.]|\\.exe(?:$|\s)|\\.msi(?:$|\s)|\\.msix(?:$|\s))", RegexOptions.IgnoreCase) ? "Windows"
            : "Unknown";
        var architecture = DownloadLinkContext.DetectArchitecture(name, evidence);
        return new AutoDetectResult
        {
            Method = result.Method, SourceUrl = result.SourceUrl, Version = result.Version,
            DownloadUrl = result.DownloadUrl, VersionPattern = result.VersionPattern,
            DownloadPattern = result.DownloadPattern, PackageId = result.PackageId,
            Confidence = result.Confidence, Summary = result.Summary,
            Platform = platform, Architecture = architecture, Evidence = result.Evidence
        };
    }

    public AutoDetectResult GenerateSelectedRule(AutoDetectResult selected)
    {
        if (string.IsNullOrWhiteSpace(selected.DownloadUrl)) return selected;
        var filename = Path.GetFileName(new Uri(selected.DownloadUrl).AbsolutePath);
        if (selected.Method == SoftwareSourceKind.GitHubReleases)
        {
            var pattern = GitHubDetection.CreateAssetPattern(filename, selected.Version);
            ValidateFilePattern(pattern, filename);
            return new AutoDetectResult
            {
                Method = selected.Method, SourceUrl = selected.SourceUrl, Version = selected.Version,
                DownloadUrl = selected.DownloadUrl, PackageId = selected.PackageId,
                Platform = selected.Platform, Architecture = selected.Architecture, Evidence = selected.Evidence,
                DownloadPattern = pattern, Confidence = selected.Confidence,
                Summary = selected.Summary + " Generated asset rule matches the selected file."
            };
        }
        var downloadPattern = _generator.GenerateDownloadPattern(selected.DownloadUrl, selected.Version);
        if (!Regex.IsMatch(selected.DownloadUrl, downloadPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("The generated download rule does not match the selected URL.");
        return new AutoDetectResult
        {
            Method = selected.Method, SourceUrl = selected.SourceUrl, Version = selected.Version,
            DownloadUrl = selected.DownloadUrl, PackageId = selected.PackageId,
            Platform = selected.Platform, Architecture = selected.Architecture,
            DownloadPattern = downloadPattern,
            VersionPattern = !string.IsNullOrWhiteSpace(selected.Version) && selected.DownloadUrl.Contains(selected.Version, StringComparison.OrdinalIgnoreCase)
                ? _generator.GenerateVersionPatternFromUrl(selected.DownloadUrl, selected.Version)
                : _generator.GenerateNearbyVersionPattern(),
            Confidence = selected.Confidence,
            Summary = selected.Summary + " Generated download rule matches the selected URL."
        };
    }

    private static void ValidateFilePattern(string pattern, string filename)
    {
        if (!Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("The generated asset rule does not match the selected filename.");
    }

    private async Task AddPageAsync(Uri uri, List<PageSnapshot> pages, CancellationToken token)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, token);
            if (!response.IsSuccessStatusCode) return;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return;
            var html = await response.Content.ReadAsStringAsync(token);
            pages.Add(new PageSnapshot(response.RequestMessage?.RequestUri ?? uri, html));
        }
        catch (HttpRequestException) { }
        if (pages.Count == 0) pages.Add(new PageSnapshot(uri, string.Empty));
    }

    private static IEnumerable<InstallerCandidate> ExtractInstallerCandidates(PageSnapshot page, string name)
    {
        foreach (Match match in LinkRegex.Matches(page.Html))
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!Uri.TryCreate(page.Uri, raw, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var path = uri.AbsolutePath.ToLowerInvariant();
            var extension = Path.GetExtension(path);
            var start = Math.Max(0, match.Index - 350);
            var length = Math.Min(page.Html.Length - start, match.Length + 700);
            var context = StripHtml(page.Html.Substring(start, length));
            // Prefer the containing table row: nearby links may belong to different architectures.
            var rowStart = page.Html.LastIndexOf("<tr", match.Index, StringComparison.OrdinalIgnoreCase);
            var rowEnd = page.Html.IndexOf("</tr>", match.Index, StringComparison.OrdinalIgnoreCase);
            if (rowStart >= 0 && rowEnd > match.Index && rowEnd - rowStart < 6000)
                context = StripHtml(page.Html.Substring(rowStart, rowEnd + 5 - rowStart));
            // Extract a short, link-specific description, not an entire page or table.
            // Shared table cells often mention multiple architectures and OS versions.
            var anchorEnd = page.Html.IndexOf("</a>", match.Index, StringComparison.OrdinalIgnoreCase);
            var anchorText = anchorEnd > match.Index && anchorEnd - match.Index < 800
                ? StripHtml(page.Html.Substring(match.Index, anchorEnd + 4 - match.Index)) : string.Empty;
            var localStart = Math.Max(0, match.Index - 170);
            var localText = StripHtml(page.Html.Substring(localStart, match.Index - localStart));
            var rowText = context;
            context = anchorText + " " + localText + " " + rowText;
            var combined = uri.AbsoluteUri + " " + context;
            var directInstaller = InstallerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
            var downloadHint = ContainsDownloadHint(combined);
            var platformHint = ContainsWindowsHint(combined);
            // A download button's surrounding product description may omit "Windows".
            // Treat an explicit download action as a candidate, but still verify the
            // response before accepting an indirect URL.
            var explicitDownloadAction = Regex.IsMatch(
                page.Html.Substring(Math.Max(0, match.Index - 180), Math.Min(page.Html.Length - Math.Max(0, match.Index - 180), match.Length + 420)),
                @"download[-_ ](?:asset|btn|button)|download[-_ ]link|downloadId",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!directInstaller && !(downloadHint && (platformHint || explicitDownloadAction))) continue;
            var score = directInstaller ? 35 : 20;
            if (extension is ".exe" or ".msi" or ".msix" or ".msixbundle") score += 25;
            if (ContainsWindowsHint(uri.AbsoluteUri + " " + context)) score += 20;
            if (ContainsArchitectureHint(uri.AbsoluteUri, "x64", "win64", "amd64")) score += 10;
            if (ContainsArchitectureHint(uri.AbsoluteUri, "arm64")) score -= 12;
            if (ContainsArchitectureHint(uri.AbsoluteUri, "x86", "win32", "32bit")) score -= 5;
            if (!string.IsNullOrWhiteSpace(name) && (uri.AbsoluteUri + context).Contains(name, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (uri.AbsoluteUri.Contains("portable", StringComparison.OrdinalIgnoreCase)) score -= 3;
            var versionContext = GetVersionContext(page.Html, match.Index, uri, anchorText);
            var contextualVersion = DownloadLinkContext.FindVersion(
                page.Html, match.Index, match.Length, uri.AbsoluteUri, anchorText);
            yield return new InstallerCandidate(uri.AbsoluteUri, page.Uri.AbsoluteUri, versionContext, score, !directInstaller,
                ArchitectureEvidence: anchorText + " " + localText,
                ContextualVersion: contextualVersion);
        }
    }

    private static IEnumerable<Uri> ExtractLinks(string html, Uri baseUri)
    {
        foreach (Match match in LinkRegex.Matches(html))
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (Uri.TryCreate(baseUri, raw, out var uri) && uri.Scheme is "http" or "https") yield return uri;
        }
    }

    private static bool IsLikelyDownloadPage(Uri uri, Uri source)
    {
        if (!uri.Host.Equals(source.Host, StringComparison.OrdinalIgnoreCase)) return false;
        var value = uri.AbsoluteUri.ToLowerInvariant();
        return value.Contains("download") || value.Contains("windows") || value.Contains("release") || value.Contains("get-");
    }

    private static string? TryGetGitHubRepository(Uri uri)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"https://github.com/{segments[0]}/{segments[1]}" : null;
    }

    private static int ScoreGitHubRepository(string repository, string softwareName)
    {
        var segments = new Uri(repository).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return 0;
        var name = NormalizeIdentity(softwareName);
        if (name.Length == 0) return 0;
        var owner = NormalizeIdentity(segments[0]);
        var project = NormalizeIdentity(segments[1]);
        if (project == name) return 30;
        if (owner == name) return 20;
        if (project.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(project, StringComparison.OrdinalIgnoreCase)) return 12;
        return 0;
    }

    private static IEnumerable<GitHubReleaseAsset> RankGitHubAssets(IEnumerable<GitHubReleaseAsset> assets, string name) =>
        assets.OrderByDescending(asset =>
        {
            var score = 0;
            var value = asset.FileName;
            if (Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(value).Equals(".msi", StringComparison.OrdinalIgnoreCase)) score += 30;
            if (ContainsWindowsHint(value)) score += 25;
            if (ContainsArchitectureHint(value, "x64", "win64", "amd64")) score += 15;
            if (ContainsArchitectureHint(value, "arm64")) score -= 15;
            if (value.Contains("portable", StringComparison.OrdinalIgnoreCase)) score -= 3;
            if (!string.IsNullOrWhiteSpace(name) && value.Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) score += 5;
            return score;
        });

    private async Task<List<InstallerCandidate>> ResolveIndirectCandidatesAsync(List<InstallerCandidate> candidates, IProgress<string>? progress, CancellationToken token)
    {
        var resolved = new List<InstallerCandidate>();
        foreach (var candidate in candidates.Take(30))
        {
            if (!candidate.IsIndirect)
            {
                resolved.Add(candidate);
                continue;
            }

            try
            {
                progress?.Report($"Checking indirect download link on {new Uri(candidate.Url).Host}...");
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) continue;

                var finalUri = response.RequestMessage?.RequestUri;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var finalExtension = finalUri is null ? string.Empty : Path.GetExtension(finalUri.AbsolutePath);
                var installerResponse = InstallerExtensions.Contains(finalExtension, StringComparer.OrdinalIgnoreCase)
                    || contentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("application/x-msdownload", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("application/vnd.microsoft.portable-executable", StringComparison.OrdinalIgnoreCase)
                    || response.Content.Headers.ContentDisposition?.FileName is { Length: > 0 };

                if (!installerResponse) continue;

                // Keep the stable page link as the reusable download candidate. The final URL is
                // evidence that it resolves to an installer and may contain useful version data.
                var evidenceUrl = finalUri?.AbsoluteUri ?? candidate.Url;
                var resolvedFileName = DownloadLinkContext.GetContentDispositionFileName(response.Content.Headers.ContentDisposition);
                resolved.Add(candidate with
                {
                    ResolvedUrl = evidenceUrl,
                    ResolvedFileName = resolvedFileName,
                    Score = candidate.Score + 30
                });
            }
            catch (HttpRequestException) { }
        }

        return resolved.OrderByDescending(candidate => candidate.Score).ToList();
    }

    private static bool ContainsDownloadHint(string value) =>
        new[] { "download", "installer", "setup", "get", "product=" }.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsWindowsHint(string value) => new[] { "windows", "win64", "win32", "win-", "_win", "-win" }.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsArchitectureHint(string value, params string[] hints) => hints.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static string GetVersionContext(string html, int linkIndex, Uri url, string anchorText)
    {
        // The closest heading/section preceding a link is safer than global page text.
        var preceding = html[..linkIndex];
        var headings = Regex.Matches(preceding, @"<(?:h[1-6]|strong|b)\\b[^>]*>.*?</(?:h[1-6]|strong|b)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        var heading = headings.Count > 0 ? StripHtml(headings[^1].Value) : "";
        var nearest = StripHtml(preceding[Math.Max(0, linkIndex - 350)..]);
        return anchorText + " " + heading + " " + nearest;
    }

    private static string? DetectLocalVersion(string context, string url)
    {
        // Prefer an explicit release/version label; never infer a version from unrelated
        // system requirements such as "macOS 12.0".
        var labeled = Regex.Match(context, @"(?i)\\b(?:windows\\s+)?(?:installers?|releases?|versions?)\\s*[:\\[]\\s*(?<version>\\d+(?:[._-]\\d+){0,4})",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        if (labeled.Success) return labeled.Groups["version"].Value;
        var path = new Uri(url).AbsolutePath;
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Reverse().Skip(1).FirstOrDefault();
        if (segment is not null && Regex.IsMatch(segment, @"^\\d{3,6}$"))
            return segment;
        return null;
    }

    private static string? DetectBestVersion(string url, string context, string? knownVersion)
    {
        // Installer URLs often contain both a product version and an unrelated build/revision
        // number. Prefer dotted semantic versions, repeated values (directory + filename), and
        // values close to version/release wording. Penalize underscore-only build identifiers.
        var decoded = Uri.UnescapeDataString(url);
        var matches = Regex.Matches(decoded, @"(?<!\d)[vV]?(?<version>\d+(?:[._-]\d+){1,7})(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            .Cast<Match>()
            .Select(match => new
            {
                Raw = match.Groups["version"].Value,
                Normalized = match.Groups["version"].Value.Replace('_', '.').Replace('-', '.'),
                Match = match
            })
            .ToList();

        if (matches.Count == 0) return null;

        return matches
            .GroupBy(item => item.Normalized, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var raw = first.Raw;
                var parts = raw.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
                var score = group.Count() * 35;
                var known = NormalizeVersion(knownVersion);
                if (known is not null)
                {
                    if (string.Equals(group.Key, known, StringComparison.OrdinalIgnoreCase)) score += 150;
                    else if (HasSameVersionShape(group.Key, known)) score += 15;
                }
                if (raw.Contains('.')) score += 35;
                if (parts.Length >= 3) score += 20;
                if (raw.Contains('_') && !raw.Contains('.')) score -= 45;
                if (parts.Any(part => part.Length >= 5)) score -= 30;
                if (Regex.IsMatch(raw, @"^\d{3,}[_-]\d{3,}$", RegexOptions.CultureInvariant)) score -= 70;
                if (context.Contains(raw, StringComparison.OrdinalIgnoreCase)) score += 10;
                return new { Version = group.Key, Score = score };
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Version.Count(character => character == '.'))
            .First().Version;
    }

    private static async Task<AutoDetectResult?> TryDetectWinGetAsync(string softwareName, string? knownVersion, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(softwareName)) return null;
        try
        {
            var packages = await new WingetDetection().SearchAsync(softwareName, token);
            if (packages.Count == 0) return null;

            var normalizedName = NormalizeIdentity(softwareName);
            var known = NormalizeVersion(knownVersion);
            var ranked = packages.Select(package =>
            {
                var packageName = NormalizeIdentity(package.Name);
                var packageIdTail = NormalizeIdentity(package.Id.Split('.').LastOrDefault() ?? package.Id);
                var score = 0;
                if (packageName == normalizedName) score += 80;
                else if (packageName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase) || normalizedName.Contains(packageName, StringComparison.OrdinalIgnoreCase)) score += 45;
                if (packageIdTail == normalizedName) score += 60;
                else if (packageIdTail.Contains(normalizedName, StringComparison.OrdinalIgnoreCase)) score += 30;
                if (known is not null && string.Equals(NormalizeVersion(package.Version), known, StringComparison.OrdinalIgnoreCase)) score += 60;
                return new { Package = package, Score = score };
            }).OrderByDescending(item => item.Score).ToList();

            var best = ranked.First();
            var secondScore = ranked.Skip(1).Select(item => item.Score).FirstOrDefault();
            if (best.Score < 60 || best.Score - secondScore < 15) return null;

            return new AutoDetectResult
            {
                Method = SoftwareSourceKind.WinGet,
                SourceUrl = string.Empty,
                PackageId = best.Package.Id,
                Version = best.Package.Version,
                Confidence = Math.Clamp(best.Score, 0, 98),
                Summary = $"WinGet package {best.Package.Id} matched the software identity with high confidence."
            };
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeIdentity(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().TrimStart('v', 'V').Replace('_', '.').Replace('-', '.');
    }

    private static bool HasSameVersionShape(string candidate, string knownVersion)
    {
        var candidateParts = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var knownParts = knownVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return candidateParts.Length == knownParts.Length
            && candidateParts.All(part => part.All(char.IsDigit))
            && knownParts.All(part => part.All(char.IsDigit));
    }

    private static string? FindNearbyVersion(string text)
    {
        var match = Regex.Match(text, @"(?<!\d)[vV]?(?<version>\d+(?:[._-]\d+){1,7})(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["version"].Value.Replace('_', '.').Replace('-', '.') : null;
    }

    private static string StripHtml(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Replace("\r", " ").Replace("\n", " ");

    private sealed record PageSnapshot(Uri Uri, string Html);
    private sealed record InstallerCandidate(string Url, string PageUrl, string Context, int Score, bool IsIndirect = false, string? ResolvedUrl = null, string? ArchitectureEvidence = null, DetectedLinkVersion? ContextualVersion = null, string? ResolvedFileName = null);
}

public sealed class AutoDetectResult
{
    public SoftwareSourceKind Method { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? DownloadUrl { get; init; }
    public string? VersionPattern { get; init; }
    public string? DownloadPattern { get; init; }
    public string? PackageId { get; init; }
    public int Confidence { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Platform { get; init; } = "Unknown";
    public string Architecture { get; init; } = "Unknown";
    public string? Evidence { get; init; }
    public SoftwareSource ToSource() => new() { Kind = Method, Url = SourceUrl, PackageId = PackageId, VersionPattern = VersionPattern, DownloadPattern = DownloadPattern };
}
