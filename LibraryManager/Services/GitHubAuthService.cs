// Central GitHub authentication service for LibraryManager.
// Supports two optional authentication methods: GitHub OAuth or a manually supplied Personal Access Token.
// It will manage sign-in, sign-out/disconnect, authentication state, secure credential access, and account information.
// GitHubDetection and other GitHub-dependent services must consume authentication through this service.
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class GitHubAuthService
{
    public const string CredentialTargetName = "LibraryManager/GitHubToken";
    public const string OAuthClientId = "Ov23liaU67K9UfJZ3EHY";
    private readonly HttpClient _httpClient;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly RequestTrackingService _requests;
    private string? _personalAccessToken;
    public GitHubAccount Account { get; } = new() { AuthenticationMethod = GitHubAuthenticationMethod.Anonymous };

    public GitHubAuthService(RequestTrackingService? requests = null, HttpClient? httpClient = null, ISecureCredentialStore? credentialStore = null)
    {
        _requests = requests ?? new RequestTrackingService();
        _httpClient = httpClient ?? new HttpClient();
        _credentialStore = credentialStore ?? new WindowsCredentialStore();
        try { _personalAccessToken = _credentialStore.Read(CredentialTargetName); }
        catch (PlatformNotSupportedException) { }
        if (!string.IsNullOrWhiteSpace(_personalAccessToken))
        {
            Account.AuthenticationMethod = GitHubAuthenticationMethod.OAuth;
            Account.IsConnected = true;
        }
    }

    public async Task<GitHubDeviceAuthorization> BeginOAuthDeviceFlowAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = OAuthClientId,
                ["scope"] = "repo"
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        return new GitHubDeviceAuthorization(
            root.GetProperty("device_code").GetString() ?? throw new InvalidOperationException("GitHub did not return a device code."),
            root.GetProperty("user_code").GetString() ?? throw new InvalidOperationException("GitHub did not return a user code."),
            root.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
            root.TryGetProperty("interval", out var interval) ? Math.Max(5, interval.GetInt32()) : 5,
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
    }

    public async Task CompleteOAuthDeviceFlowAsync(GitHubDeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresInSeconds);
        var interval = authorization.IntervalSeconds;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = OAuthClientId,
                    ["device_code"] = authorization.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("access_token", out var tokenElement))
            {
                var token = tokenElement.GetString();
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("GitHub returned an empty access token.");
                await StoreAndValidateTokenAsync(token, GitHubAuthenticationMethod.OAuth, cancellationToken);
                return;
            }
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval += 5; continue; }
            if (error == "expired_token") throw new InvalidOperationException("The GitHub authorization code expired.");
            if (error == "access_denied") throw new InvalidOperationException("GitHub authorization was cancelled.");
            throw new InvalidOperationException($"GitHub OAuth failed: {error ?? "unknown error"}.");
        }
        throw new TimeoutException("The GitHub authorization code expired.");
    }

    public async Task ConnectWithPersonalAccessTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        token = token.Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A token is required.", nameof(token));

        await StoreAndValidateTokenAsync(token, GitHubAuthenticationMethod.PersonalAccessToken, cancellationToken);
    }

    private async Task StoreAndValidateTokenAsync(string token, GitHubAuthenticationMethod method, CancellationToken cancellationToken)
    {
        using var request = CreateRequest("https://api.github.com/user", token);
        _requests.RecordRequest();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateRateLimitFromResponse(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub authentication failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var userName = document.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
        _credentialStore.Write(CredentialTargetName, token);
        _personalAccessToken = token;
        Account.UserName = userName;
        Account.AuthenticationMethod = method;
        Account.IsConnected = true;
    }

    public void Disconnect()
    {
        _credentialStore.Delete(CredentialTargetName);
        _personalAccessToken = null;
        Account.UserName = null;
        Account.IsConnected = false;
        Account.AuthenticationMethod = GitHubAuthenticationMethod.Anonymous;
    }

    public void ApplyAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_personalAccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _personalAccessToken);
    }

    public async Task RefreshAccountAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(_personalAccessToken)
            ? "https://api.github.com/rate_limit"
            : "https://api.github.com/user";
        using var request = CreateRequest(endpoint, _personalAccessToken);
        _requests.RecordRequest();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        UpdateRateLimitFromResponse(response);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) Disconnect();
            throw new InvalidOperationException($"GitHub account check failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!string.IsNullOrWhiteSpace(_personalAccessToken))
        {
            Account.UserName = document.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
            Account.IsConnected = true;
        }
        else if (document.RootElement.TryGetProperty("resources", out var resources) &&
                 resources.TryGetProperty("core", out var core))
        {
            if (core.TryGetProperty("remaining", out var remaining))
                Account.RemainingRequests = remaining.GetInt32();
            if (core.TryGetProperty("reset", out var reset))
                Account.RateLimitReset = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LibraryManager", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public void UpdateRateLimitFromResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && int.TryParse(remaining.FirstOrDefault(), out var count))
            Account.RemainingRequests = count;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var unixTime))
            Account.RateLimitReset = DateTimeOffset.FromUnixTimeSeconds(unixTime);
    }
}

public sealed record GitHubDeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int IntervalSeconds, int ExpiresInSeconds);
