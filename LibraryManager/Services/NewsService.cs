using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LibraryManager.Helpers;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class NewsService
{
    public static string NewsUrl => $"https://raw.githubusercontent.com/{DistributionInfo.Current.Repository}/main/OnlineCatalog/news.json";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthService _authentication;
    private readonly RequestTrackingService _requests;
    private readonly string _cachePath;

    public NewsService(HttpClient? httpClient = null, GitHubAuthService? authentication = null, RequestTrackingService? requests = null, string? cachePath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _authentication = authentication ?? AppServices.GitHubAuthentication;
        _requests = requests ?? AppServices.Requests;
        _cachePath = cachePath ?? FileHelper.GetApplicationDataPath(Path.Combine("Cache", DistributionInfo.Current.Channel, "news.json"));
    }

    public async Task<NewsFeed> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && File.Exists(_cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) < CacheLifetime)
            return await ReadFeedAsync(_cachePath, cancellationToken);

        try
        {
            var feed = await DownloadAsync(cancellationToken);
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(feed, JsonOptions), cancellationToken);
            return feed;
        }
        catch when (File.Exists(_cachePath))
        {
            return await ReadFeedAsync(_cachePath, cancellationToken);
        }
    }

    public async Task RefreshCacheSilentlyAsync(CancellationToken cancellationToken = default)
    {
        try { _ = await LoadAsync(false, cancellationToken); }
        catch { }
    }

    private async Task<NewsFeed> DownloadAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{DistributionInfo.Current.Repository}/contents/OnlineCatalog/news.json?ref=main");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LibraryManager", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        _authentication.ApplyAuthorization(request);
        _requests.RecordRequest();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        _authentication.UpdateRateLimitFromResponse(response);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<NewsFeed>(stream, JsonOptions, cancellationToken) ?? new NewsFeed();
    }

    private static async Task<NewsFeed> ReadFeedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<NewsFeed>(stream, JsonOptions, cancellationToken) ?? new NewsFeed();
    }
}
