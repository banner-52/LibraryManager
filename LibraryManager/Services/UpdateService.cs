// Service for LibraryManager.
// Orchestrate checks and updates for managed software. Never use for LibraryManager self-update.
// Keep this file focused on this single responsibility.
using System.IO;
using System.Net.Http;
using System.Net;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LibraryManager.Helpers;
using LibraryManager.Models;
using LibraryManager.Services.Detection;

namespace LibraryManager.Services;

public sealed class UpdateService
{
    public static readonly TimeSpan CheckCacheDuration = TimeSpan.FromMinutes(30);
    private const int MaxCheckCacheEntries = 2048;
    private static readonly TimeSpan IndividualCheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, CachedCheck> CheckCache = new(StringComparer.Ordinal);
    private readonly GitHubDetection _gitHubDetection;
    private readonly WebsiteDetection _websiteDetection;
    private readonly WingetDetection _wingetDetection;
    private readonly DownloadService _downloadService;
    private readonly PackageService _packageService;

    public UpdateService(GitHubDetection gitHubDetection, WebsiteDetection websiteDetection, WingetDetection wingetDetection, DownloadService downloadService, PackageService packageService)
    {
        _gitHubDetection = gitHubDetection;
        _websiteDetection = websiteDetection;
        _wingetDetection = wingetDetection;
        _downloadService = downloadService;
        _packageService = packageService;
    }

    public async Task<SoftwareVersion?> CheckAsync(Software software, CancellationToken cancellationToken = default)
    {
        var key = BuildCheckCacheKey(software);
        SoftwareVersion? result;
        if (CheckCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CreatedUtc < CheckCacheDuration)
        {
            result = cached.Result;
        }
        else
        {
            CheckCache.TryRemove(key, out _);
            result = software.Source.Kind switch
            {
                SoftwareSourceKind.GitHubReleases => await _gitHubDetection.DetectAsync(software, cancellationToken),
                SoftwareSourceKind.Website => await _websiteDetection.DetectAsync(software, cancellationToken),
                SoftwareSourceKind.WinGet => await _wingetDetection.DetectAsync(software, cancellationToken),
                _ => null
            };
            if (result is not null)
            {
                CheckCache[key] = new CachedCheck(result, DateTimeOffset.UtcNow);
                TrimCheckCache();
            }
        }

        software.LastCheckedAt = DateTimeOffset.UtcNow;
        software.LastDetectionResult = result;
        if (result is null) { software.Status = SoftwareStatus.Error; return null; }
        software.AvailableVersion = result.Version;
        software.Status = string.Equals(software.LocalVersion, result.Version, StringComparison.OrdinalIgnoreCase) ? SoftwareStatus.UpToDate : SoftwareStatus.UpdateAvailable;
        return result;
    }

    public async Task CheckManyAsync(IReadOnlyList<Software> softwareItems, IProgress<CheckProgress>? progress = null, int maxDegreeOfParallelism = 4, CancellationToken cancellationToken = default)
    {
        if (softwareItems.Count == 0) return;
        if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

        var completed = 0;
        foreach (var software in softwareItems.Where(item => item.Source.Kind == SoftwareSourceKind.None))
        {
            cancellationToken.ThrowIfCancellationRequested();
            software.LastCheckedAt = DateTimeOffset.UtcNow;
            software.LastDetectionResult = null;
            software.AvailableVersion = null;
            software.Status = SoftwareStatus.Error;
            var done = Interlocked.Increment(ref completed);
            progress?.Report(new CheckProgress(done, softwareItems.Count, software.DisplayName));
        }

        var configuredItems = softwareItems.Where(item => item.Source.Kind != SoftwareSourceKind.None).ToList();
        await Parallel.ForEachAsync(configuredItems, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        }, async (software, token) =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(IndividualCheckTimeout);
                try
                {
                    await CheckAsync(software, timeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    software.LastCheckedAt = DateTimeOffset.UtcNow;
                    software.Status = SoftwareStatus.Error;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                software.LastCheckedAt = DateTimeOffset.UtcNow;
                software.Status = SoftwareStatus.Error;
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new CheckProgress(done, softwareItems.Count, software.DisplayName));
            }
        });
    }

    public async Task<string?> UpdateAsync(Software software, string repositoryPath, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var version = GetCachedDetection(software) ?? await CheckAsync(software, cancellationToken);
        if (version is null || software.Status != SoftwareStatus.UpdateAvailable) return null;
        var delivery = await ResolveDeliveryAsync(software, version, cancellationToken);
        if (delivery is null) return null;
        var resolved = delivery.Value;
        var installer = resolved.Source.Source.Kind == SoftwareSourceKind.WinGet
            ? await _wingetDetection.DownloadAsync(resolved.Source, version.Version, null, cancellationToken)
            : resolved.Version.DownloadUrl is not null
                ? await _downloadService.DownloadToTemporaryFolderAsync(resolved.Version.DownloadUrl, progress, cancellationToken, resolved.Version.SelectedAssetFileName, resolved.Source.Source.ExpectedSha256)
                : null;
        if (installer is null) return null;
        IReadOnlyList<PackageFile> additionalFiles = [];
        try
        {
            additionalFiles = await DownloadAdditionalFilesAsync(software, cancellationToken);
            var package = await _packageService.CreatePackageAsync(software, installer, repositoryPath, additionalFiles, cancellationToken: cancellationToken);
            software.LocalVersion = VersionHelper.GetVersionFromPackageName(package);
            software.PackageFileName = Path.GetFileName(package);
            software.Status = SoftwareStatus.UpToDate;
            return package;
        }
        finally
        {
            CleanupTemporaryDownload(installer);
            foreach (var file in additionalFiles) CleanupTemporaryDownload(file.SourcePath);
        }
    }

    public async Task<string?> UpdateWithProgressAsync(Software software, string repositoryPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken = default)
    {
        var version = GetCachedDetection(software);
        if (version is null)
        {
            progress?.Report(new OperationProgress { Message = "Step 1/6 — Checking update source", Percentage = 2, CanCancel = true, IsIndeterminate = true });
            version = await CheckAsync(software, cancellationToken);
        }
        else
        {
            progress?.Report(new OperationProgress { Message = $"Step 1/6 — Using checked version {version.Version}", Percentage = 4, CanCancel = true });
        }
        if (version is null)
        {
            progress?.Report(new OperationProgress { Message = "Step 1/6 — No update version could be detected", Percentage = 100, CanCancel = false });
            return null;
        }

        if (software.Status != SoftwareStatus.UpdateAvailable)
        {
            progress?.Report(new OperationProgress { Message = $"Step 1/6 — Already up to date ({software.LocalVersion ?? version.Version})", Percentage = 100, CanCancel = false });
            return null;
        }

        var downloadProgress = new Progress<DownloadProgress>(value =>
        {
            var transferred = FormatTransfer(value.BytesDownloaded, value.TotalBytes);
            var percentage = value.TotalBytes.HasValue ? 5 + value.Percentage * 40 / 100 : 5;
            progress?.Report(new OperationProgress
            {
                Message = !string.IsNullOrWhiteSpace(value.StatusMessage)
                    ? $"Step 2/6 — {value.StatusMessage}"
                    : $"Step 2/6 — Downloading {(!string.IsNullOrWhiteSpace(value.FileName) ? value.FileName : "installer")} — {transferred}",
                Percentage = percentage,
                CanCancel = true,
                IsIndeterminate = !value.TotalBytes.HasValue
            });
        });

        progress?.Report(new OperationProgress { Message = "Step 2/6 — Downloading installer to local temporary storage", Percentage = 5, CanCancel = true, IsIndeterminate = true });
        var delivery = await ResolveDeliveryAsync(software, version, cancellationToken);
        if (delivery is null) return null;
        var resolved = delivery.Value;
        var installer = resolved.Source.Source.Kind == SoftwareSourceKind.WinGet
            ? await _wingetDetection.DownloadAsync(resolved.Source, version.Version, downloadProgress, cancellationToken)
            : resolved.Version.DownloadUrl is not null
                ? await _downloadService.DownloadToTemporaryFolderAsync(resolved.Version.DownloadUrl, downloadProgress, cancellationToken, resolved.Version.SelectedAssetFileName, resolved.Source.Source.ExpectedSha256)
                : null;
        if (installer is null) return null;

        IReadOnlyList<PackageFile> additionalFiles = [];
        try
        {
            progress?.Report(new OperationProgress { Message = "Step 3/6 — Downloading additional files locally", Percentage = 47, CanCancel = true });
            additionalFiles = await DownloadAdditionalFilesAsync(software, progress, cancellationToken);

            var packageProgress = new Progress<PackageProgress>(step =>
            {
                var detail = step.BytesProcessed.HasValue ? " — " + FormatTransfer(step.BytesProcessed.Value, step.TotalBytes) : string.Empty;
                progress?.Report(new OperationProgress
                {
                    Message = $"Step 4/6 — {step.Message}{detail}",
                    Percentage = 55 + step.Percentage * 35 / 100,
                    CanCancel = true,
                    IsIndeterminate = false
                });
            });
            var package = await _packageService.CreatePackageAsync(software, installer, repositoryPath, additionalFiles, packageProgress, cancellationToken);
            software.LocalVersion = VersionHelper.GetVersionFromPackageName(package);
            software.PackageFileName = Path.GetFileName(package);
            software.Status = SoftwareStatus.UpToDate;
            progress?.Report(new OperationProgress { Message = $"Step 6/6 — Updated {software.DisplayName}", Percentage = 100, CanCancel = false });
            return package;
        }
        finally
        {
            CleanupTemporaryDownload(installer);
            foreach (var file in additionalFiles) CleanupTemporaryDownload(file.SourcePath);
        }
    }

    private static string FormatTransfer(long bytes, long? totalBytes)
    {
        var current = bytes / 1024d / 1024d;
        return totalBytes.HasValue
            ? $"{current:F1} MB / {totalBytes.Value / 1024d / 1024d:F1} MB"
            : $"{current:F1} MB transferred";
    }

    public static void InvalidateCheckCache(Software software) => CheckCache.TryRemove(BuildCheckCacheKey(software), out _);

    private async Task<(Software Source, SoftwareVersion Version)?> ResolveDeliveryAsync(Software software, SoftwareVersion available, CancellationToken cancellationToken)
    {
        if (software.Source.DownloadMethod == DownloadMethod.SameAsCheck) return (software, available);
        var kind = software.Source.DownloadMethod switch
        {
            DownloadMethod.GitHubRelease => SoftwareSourceKind.GitHubRelease,
            DownloadMethod.Website => SoftwareSourceKind.Website,
            DownloadMethod.WinGet => SoftwareSourceKind.WinGet,
            _ => SoftwareSourceKind.None
        };
        var source = software.Source.Copy();
        source.Kind = kind;
        source.Url = software.Source.DownloadSourceUrl;
        source.PackageId = software.Source.DownloadWinGetPackageId;
        source.DownloadPattern = software.Source.DownloadAssetPattern;
        source.VersionPattern = software.Source.DownloadVersionPattern;
        source.DownloadUrlTemplate = software.Source.DeliveryUrlTemplate;
        source.DownloadFileNameTemplate = software.Source.DeliveryFileNameTemplate;
        source.StableSourceConfirmed = software.Source.DeliveryStableSourceConfirmed;
        var deliveryProfile = software.Copy();
        deliveryProfile.Source = source;
        var detected = kind switch
        {
            SoftwareSourceKind.GitHubRelease => await _gitHubDetection.DetectAsync(deliveryProfile, cancellationToken),
            SoftwareSourceKind.Website => await _websiteDetection.DetectAsync(deliveryProfile, cancellationToken),
            SoftwareSourceKind.WinGet => await _wingetDetection.DetectAsync(deliveryProfile, cancellationToken),
            _ => null
        };
        return detected is not null && SameVersion(detected.Version, available.Version) ? (deliveryProfile, detected) : null;
    }

    private static bool SameVersion(string left, string right) =>
        string.Equals(left.Trim().TrimStart('v', 'V').Replace('_', '.').Replace('-', '.'), right.Trim().TrimStart('v', 'V').Replace('_', '.').Replace('-', '.'), StringComparison.OrdinalIgnoreCase);

    private static string BuildCheckCacheKey(Software software) => string.Join("\u001f",
        software.Source.Kind,
        software.Source.Url?.Trim() ?? string.Empty,
        software.Source.PackageId?.Trim() ?? string.Empty,
        software.Source.VersionPattern?.Trim() ?? string.Empty,
        software.Source.DownloadPattern?.Trim() ?? string.Empty);

    private static void TrimCheckCache()
    {
        var cutoff = DateTimeOffset.UtcNow - CheckCacheDuration;
        foreach (var item in CheckCache.Where(item => item.Value.CreatedUtc < cutoff))
            CheckCache.TryRemove(item.Key, out _);
        var overflow = CheckCache.Count - MaxCheckCacheEntries;
        if (overflow <= 0) return;
        foreach (var item in CheckCache.OrderBy(item => item.Value.CreatedUtc).Take(overflow))
            CheckCache.TryRemove(item.Key, out _);
    }

    public sealed record CheckProgress(int Completed, int Total, string SoftwareName);

    private sealed record CachedCheck(SoftwareVersion Result, DateTimeOffset CreatedUtc);

    private static SoftwareVersion? GetCachedDetection(Software software)
    {
        var cached = software.LastDetectionResult;
        return software.Status == SoftwareStatus.UpdateAvailable &&
               cached is not null &&
               !string.IsNullOrWhiteSpace(software.AvailableVersion) &&
               string.Equals(cached.Version, software.AvailableVersion, StringComparison.OrdinalIgnoreCase)
            ? cached
            : null;
    }

    private static void CleanupTemporaryDownload(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LibraryManager")) + Path.DirectorySeparatorChar;
        if (folder is null || !folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<IReadOnlyList<PackageFile>> DownloadAdditionalFilesAsync(Software software, CancellationToken cancellationToken) =>
        await DownloadAdditionalFilesAsync(software, null, cancellationToken);

    private async Task<IReadOnlyList<PackageFile>> DownloadAdditionalFilesAsync(Software software, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var files = new List<PackageFile>();
        using var httpClient = new HttpClient();

        foreach (var additionalFile in software.AdditionalFiles.Where(file =>
                     !string.IsNullOrWhiteSpace(file.SourceUrl) && !string.IsNullOrWhiteSpace(file.FilePattern)))
        {
            // SourceUrl is the page/feed containing download links. FilePattern selects the
            // actual files from that page; never download SourceUrl itself.
            using var response = await httpClient.GetAsync(additionalFile.SourceUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var pageUri = response.RequestMessage?.RequestUri ?? new Uri(additionalFile.SourceUrl);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Match against decoded HTML as well. Vendor pages commonly encode URL query
            // separators and other characters in href attributes.
            body = WebUtility.HtmlDecode(body);
            var regex = new Regex(additionalFile.FilePattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
            if (!regex.GetGroupNames().Contains("url"))
                throw new InvalidDataException($"Additional source '{additionalFile.Name}' must include a (?<url>...) group.");

            var matches = regex.Matches(body);
            if (matches.Count == 0)
            {
                if (additionalFile.Required)
                    throw new InvalidDataException($"Required additional source '{additionalFile.Name}' did not match any download links on {additionalFile.SourceUrl}.");
                continue;
            }

            foreach (Match match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawUrl = match.Groups["url"].Value.Trim();
                if (!Uri.TryCreate(pageUri, rawUrl, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException($"Additional source '{additionalFile.Name}' returned an invalid HTTPS link.");

                var preferredName = match.Groups["name"].Success
                    ? match.Groups["name"].Value.Trim()
                    : Path.GetFileName(downloadUri.LocalPath);
                var fileProgress = new Progress<DownloadProgress>(value =>
                    progress?.Report(new OperationProgress
                    {
                        Message = !string.IsNullOrWhiteSpace(value.StatusMessage)
                            ? $"Step 3/6 — {additionalFile.Name} — {value.StatusMessage}"
                            : $"Step 3/6 — Downloading {(!string.IsNullOrWhiteSpace(value.FileName) ? value.FileName : preferredName)} — {FormatTransfer(value.BytesDownloaded, value.TotalBytes)}",
                        Percentage = 50,
                        CanCancel = true,
                        IsIndeterminate = !value.TotalBytes.HasValue
                    }));
                var path = await _downloadService.DownloadToTemporaryFolderAsync(downloadUri.AbsoluteUri, fileProgress, cancellationToken, preferredName);
                if (Path.GetExtension(preferredName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var extractRoot = Path.Combine(Path.GetTempPath(), "LibraryManager", "additional-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(extractRoot);
                    using var archive = ZipFile.OpenRead(path);
                    foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relativeEntry = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var target = Path.GetFullPath(Path.Combine(extractRoot, relativeEntry));
                        var safeRoot = Path.GetFullPath(extractRoot) + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Additional ZIP '{preferredName}' contains an unsafe path.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, true);
                        var entryFolder = Path.GetDirectoryName(relativeEntry);
                        var destination = string.IsNullOrWhiteSpace(additionalFile.DestinationFolder)
                            ? entryFolder
                            : string.IsNullOrWhiteSpace(entryFolder)
                                ? additionalFile.DestinationFolder
                                : Path.Combine(additionalFile.DestinationFolder, entryFolder);
                        files.Add(new PackageFile(target, destination, entry.Name));
                    }
                }
                else
                {
                    files.Add(new PackageFile(path, additionalFile.DestinationFolder, preferredName));
                }
            }

            if (additionalFile.Required && !files.Any(file => string.Equals(file.DestinationPath, additionalFile.DestinationFolder, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Required additional source '{additionalFile.Name}' did not match any download links.");
        }
        return files;
    }
}
