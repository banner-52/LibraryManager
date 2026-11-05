// Service for LibraryManager.
// Download installers and additional files to local temporary storage.
// Keep this file focused on this single responsibility.
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Diagnostics;

namespace LibraryManager.Services;

public sealed class DownloadService
{
    private const string BrowserCompatibleUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 LibraryManager/2.0";
    private const string SourceForgeUserAgent = "Wget/1.21.4";
    private const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public DownloadService(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();

    public async Task<string> DownloadToTemporaryFolderAsync(string downloadUrl, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? preferredFileName = null, string? expectedSha256 = null)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var requestedUri) || requestedUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Downloads must use a valid HTTPS URL.");
        if (!string.IsNullOrWhiteSpace(expectedSha256) && !IsSha256(expectedSha256))
            throw new InvalidDataException("The expected SHA-256 must contain exactly 64 hexadecimal characters.");
        var folder = Path.Combine(Path.GetTempPath(), "LibraryManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestedUri);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd(IsSourceForge(requestedUri) ? SourceForgeUserAgent : BrowserCompatibleUserAgent);
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new DownloadProgress(0, null, 0, preferredFileName ?? string.Empty, $"Connecting to {requestedUri.Host}..."));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var headersElapsed = stopwatch.Elapsed;
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? requestedUri;
        var redirected = finalUri != requestedUri;
        progress?.Report(new DownloadProgress(0, response.Content.Headers.ContentLength, 0, preferredFileName ?? string.Empty,
            redirected
                ? $"Server response received in {headersElapsed.TotalSeconds:F1} s — redirected to {finalUri.Host}"
                : $"Server response received in {headersElapsed.TotalSeconds:F1} s"));
        if (finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The download was redirected to a non-HTTPS URL.");
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            throw new InvalidDataException("The download exceeds the 2 GB safety limit.");
        var contentDispositionName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        var fileName = preferredFileName ?? contentDispositionName?.Trim('"') ?? Path.GetFileName(finalUri.LocalPath);
        fileName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "installer.bin";
        var targetPath = Path.Combine(folder, fileName);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(targetPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long totalRead = 0;
        var totalLength = response.Content.Headers.ContentLength;
        int read;
        var firstRead = true;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (firstRead)
            {
                firstRead = false;
                progress?.Report(new DownloadProgress(0, totalLength, 0, fileName, $"First data received after {stopwatch.Elapsed.TotalSeconds:F1} s"));
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            totalRead += read;
            if (totalRead > MaximumDownloadBytes)
                throw new InvalidDataException("The download exceeds the 2 GB safety limit.");
            var percentage = totalLength.HasValue ? (int)(totalRead * 100 / Math.Max(1, totalLength.Value)) : 0;
            progress?.Report(new DownloadProgress(totalRead, totalLength, percentage, fileName));
        }
        if (!string.IsNullOrWhiteSpace(expectedSha256) && !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("The downloaded file does not match the expected SHA-256.");
        return targetPath;
    }

    private static bool IsSourceForge(Uri uri) =>
        uri.Host.Equals("sourceforge.net", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".sourceforge.net", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        foreach (var character in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(character, '_');
        return fileName;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed record DownloadProgress(long BytesDownloaded, long? TotalBytes, int Percentage, string FileName = "", string? StatusMessage = null);
