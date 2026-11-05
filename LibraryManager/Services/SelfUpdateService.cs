using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using LibraryManager.Helpers;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class SelfUpdateService
{
    private static string LatestReleaseUrl => $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/releases?per_page=30";
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthService _authentication;
    private readonly RequestTrackingService _requests;

    public SelfUpdateService(HttpClient? httpClient = null, GitHubAuthService? authentication = null, RequestTrackingService? requests = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _authentication = authentication ?? AppServices.GitHubAuthentication;
        _requests = requests ?? AppServices.Requests;
    }

    public async Task<ApplicationUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LibraryManager", "1.0"));
        _authentication.ApplyAuthorization(request);
        _requests.RecordRequest();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        _authentication.UpdateRateLimitFromResponse(response);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var release = document.RootElement.EnumerateArray()
            .Where(item => !item.GetProperty("draft").GetBoolean()
                && (DistributionInfo.Current.IsDev || !item.GetProperty("prerelease").GetBoolean()))
            .Select(item => new { Item = item, Text = item.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? string.Empty })
            .Where(item => ApplicationVersion.IsReleaseVersion(item.Text))
            .Select(item => new { item.Item, item.Text, Version = Version.Parse(item.Text) })
            .Where(item => item.Version > GetCurrentVersion())
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();
        if (release is null) return null;
        var root = release.Item;
        var versionText = release.Text;
        var suffix = DistributionInfo.Current.IsInstalled ? "-setup.exe" : "-portable.zip";
        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true
                && item.GetProperty("name").GetString()?.Contains("-" + DistributionInfo.Current.Channel + "-", StringComparison.OrdinalIgnoreCase) == true);
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        var digest = asset.ValueKind != JsonValueKind.Undefined && asset.TryGetProperty("digest", out var digestProperty)
            ? digestProperty.GetString()
            : null;
        return new ApplicationUpdate
        {
            Version = versionText,
            ReleaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() : null,
            ReleaseUrl = root.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null,
            DownloadUrl = asset.ValueKind == JsonValueKind.Undefined ? null : asset.GetProperty("url").GetString(),
            Sha256 = ParseSha256Digest(digest)
        };
    }

    public async Task DownloadAndRestartAsync(ApplicationUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl)) throw new InvalidOperationException("The release does not contain an update asset for this distribution.");
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The application update URL must use HTTPS.");
        if (string.IsNullOrWhiteSpace(update.Sha256))
            throw new InvalidDataException("The release does not provide a SHA-256 digest, so it cannot be installed safely.");

        var staging = Path.Combine(Path.GetTempPath(), "LibraryManager", "updates", DistributionInfo.Current.Channel, DistributionInfo.Current.Distribution, update.Version, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(staging, DistributionInfo.Current.IsInstalled ? "update-setup.exe" : "update.zip");
        var extracted = Path.Combine(staging, "files");
        Directory.CreateDirectory(staging);

        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUri))
            {
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LibraryManager", "1.0"));
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                _authentication.ApplyAuthorization(request);
                _requests.RecordRequest();

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _authentication.UpdateRateLimitFromResponse(response);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("The application update was redirected to a non-HTTPS URL.");

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(archivePath);
                var total = response.Content.Headers.ContentLength;
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    if (total.HasValue && total.Value > 0) progress?.Report((int)(readTotal * 100 / total.Value));
                }
            }

            await VerifySha256Async(archivePath, update.Sha256, cancellationToken);
            if (DistributionInfo.Current.IsInstalled)
            {
                var installer = new ProcessStartInfo
                {
                    FileName = archivePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                installer.ArgumentList.Add("/VERYSILENT");
                installer.ArgumentList.Add("/SUPPRESSMSGBOXES");
                installer.ArgumentList.Add("/NORESTART");
                installer.ArgumentList.Add("/CLOSEAPPLICATIONS");
                if (Process.Start(installer) is null)
                    throw new InvalidOperationException("Could not start the application installer.");
                Application.Current.Shutdown();
                return;
            }
            ZipFile.ExtractToDirectory(archivePath, extracted);
            var application = Path.Combine(extracted, "LibraryManager.exe");
            var updater = Path.Combine(extracted, "LibraryManager.Updater.exe");
            if (!File.Exists(application) || !File.Exists(updater))
                throw new InvalidDataException("The release ZIP does not contain LibraryManager.exe and LibraryManager.Updater.exe.");

            var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var process = Process.GetCurrentProcess();
            var startInfo = new ProcessStartInfo
            {
                FileName = updater,
                WorkingDirectory = extracted,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(process.Id.ToString());
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(extracted);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("--restart");
            startInfo.ArgumentList.Add(Path.Combine(target, "LibraryManager.exe"));

            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Could not start LibraryManager.Updater.exe.");

            Application.Current.Shutdown();
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    public static Version GetCurrentVersion() => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static string GetCurrentVersionText() => ApplicationVersion.ToDisplayString(GetCurrentVersion());

    private static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var value = digest[prefix.Length..];
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static async Task VerifySha256Async(string path, string expectedDigest, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualDigest = Convert.ToHexString(hash);
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update does not match the release SHA-256 digest.");
    }
}
