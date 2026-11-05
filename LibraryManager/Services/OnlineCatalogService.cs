using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Net.Http.Headers;
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class OnlineCatalogService
{
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthService _authentication;
    private readonly RequestTrackingService _requests;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    public OnlineCatalogService(HttpClient? httpClient = null, GitHubAuthService? authentication = null, RequestTrackingService? requests = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _authentication = authentication ?? AppServices.GitHubAuthentication;
        _requests = requests ?? AppServices.Requests;
    }

    public async Task<IReadOnlyList<Software>> LoadAsync(string catalogueUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(catalogueUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The online catalogue URL must be a valid HTTPS address.", nameof(catalogueUrl));

        // raw.githubusercontent.com returns 404 for private repository content even when a
        // normal GitHub OAuth token is sent to the raw URL. Route raw GitHub content through
        // the Contents API so the centralized OAuth/PAT authentication can be used.
        var requestUri = ConvertRawGitHubUrlToApi(uri) ?? uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (IsGitHubHost(requestUri))
        {
            request.Headers.UserAgent.ParseAdd("LibraryManager/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            _authentication.ApplyAuthorization(request);
            _requests.RecordRequest();
        }
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (IsGitHubHost(requestUri)) _authentication.UpdateRateLimitFromResponse(response);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (responseBody.Length > 500) responseBody = responseBody[..500];
            throw new HttpRequestException(
                $"GitHub catalogue request failed: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                $"Requested URL: {requestUri}. Response: {responseBody}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var profiles = root.ValueKind == JsonValueKind.Array
            ? root
            : FindProfilesArray(root);
        if (profiles.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The online catalogue does not contain a profile array.");
        if (profiles.GetArrayLength() == 0) return [];
        var first = profiles[0];
        if (first.TryGetProperty("configuration", out _))
            return (JsonSerializer.Deserialize<List<CatalogProfile>>(profiles.GetRawText(), JsonOptions) ?? [])
                .Select(CatalogProfileMapper.ToSoftware).ToList();
        // Read the brief rewritten schema too, so existing privately-hosted catalogues do not break.
        return JsonSerializer.Deserialize<List<Software>>(profiles.GetRawText(), JsonOptions) ?? [];
    }

    public async Task<CatalogueSubmissionPlan> PrepareSubmissionAsync(Software profile, CancellationToken cancellationToken = default)
    {
        var title = $"Catalogue profile: {profile.DisplayName} ({profile.Source})";
        using var request = CreateGitHubRequest(HttpMethod.Get, $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/issues?state=all&per_page=100");
        using var response = await SendGitHubAsync(request, cancellationToken);
        EnsureGitHubSuccess(response, "Unable to check existing online catalogue submissions");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        // Only reuse an OPEN submission. A closed issue represents a completed review
        // and must remain immutable; a new submission for the same profile creates a new issue.
        var existing = document.RootElement.EnumerateArray().FirstOrDefault(issue =>
            issue.TryGetProperty("state", out var state) &&
            string.Equals(state.GetString(), "open", StringComparison.OrdinalIgnoreCase) &&
            issue.TryGetProperty("title", out var issueTitle) &&
            string.Equals(issueTitle.GetString(), title, StringComparison.OrdinalIgnoreCase));
        var number = existing.ValueKind == JsonValueKind.Undefined ? (int?)null : existing.GetProperty("number").GetInt32();
        var labels = new[] { "catalogue-submission", $"source-{profile.Source.Kind.ToString().ToLowerInvariant()}" };
        return new CatalogueSubmissionPlan(title, CreateSubmissionBody(profile), number, labels);
    }

    public async Task<CatalogueSubmissionResult> SubmitAsync(CatalogueSubmissionPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_authentication.Account.IsConnected) throw new InvalidOperationException("Connect a GitHub account in Settings before submitting an online catalogue profile.");
        var issueNumber = plan.ExistingIssueNumber;
        var payload = JsonSerializer.Serialize(new { title = plan.Title, body = plan.Body });
        using (var request = CreateGitHubRequest(issueNumber is null ? HttpMethod.Post : HttpMethod.Patch,
                   issueNumber is null ? $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/issues" : $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/issues/{issueNumber}"))
        {
            request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await SendGitHubAsync(request, cancellationToken);
            EnsureGitHubSuccess(response, "Unable to create or update the online catalogue issue");
            if (issueNumber is null)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                issueNumber = document.RootElement.GetProperty("number").GetInt32();
            }
        }

        using (var request = CreateGitHubRequest(HttpMethod.Put, $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/issues/{issueNumber}/labels"))
        {
            request.Content = new StringContent(JsonSerializer.Serialize(plan.Labels), System.Text.Encoding.UTF8, "application/json");
            using var response = await SendGitHubAsync(request, cancellationToken);
            EnsureGitHubSuccess(response, "Unable to apply online catalogue labels");
        }
        return new CatalogueSubmissionResult(issueNumber!.Value, plan.ExistingIssueNumber is not null);
    }

    public async Task<SoftwareRequestSubmissionResult> SubmitSoftwareRequestAsync(string softwareName, string sourceUrl, string details, CancellationToken cancellationToken = default)
    {
        if (!_authentication.Account.IsConnected)
            throw new InvalidOperationException("Connect a GitHub account in Settings before requesting software.");

        var title = "[Software Request] " + softwareName.Trim();
        var body = "## Software request\n\n"
            + "**Software:** " + softwareName.Trim() + "\n"
            + "**Official website / download page:** " + sourceUrl.Trim() + "\n\n"
            + "### Catalogue requirements\n"
            + "- Software must be free to download.\n"
            + "- Installer and update source must be publicly accessible from the Internet.\n"
            + "- Sources requiring login, customer/company portals, subscriptions or other authenticated access are not accepted.\n\n"
            + "### Additional information\n"
            + (string.IsNullOrWhiteSpace(details) ? "_No additional information provided._" : details.Trim());

        using var request = CreateGitHubRequest(HttpMethod.Post, $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/issues");
        request.Content = new StringContent(JsonSerializer.Serialize(new { title, body, labels = new[] { "software-request" } }), System.Text.Encoding.UTF8, "application/json");
        using var response = await SendGitHubAsync(request, cancellationToken);
        EnsureGitHubSuccess(response, "Unable to submit the software request");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return new SoftwareRequestSubmissionResult(
            document.RootElement.GetProperty("number").GetInt32(),
            document.RootElement.GetProperty("html_url").GetString() ?? string.Empty);
    }

    public async Task<IncidentSubmissionResult> SubmitIncidentAsync(string summary, string details, CancellationToken cancellationToken = default)
    {
        if (!_authentication.Account.IsConnected)
            throw new InvalidOperationException("Connect a GitHub account in Settings before reporting an incident.");

        var title = "[Incident] " + summary.Trim();
        var body = "## #incident\n\n"
            + "**Application version:** " + SelfUpdateService.GetCurrentVersionText() + "\n"
            + "**Reported UTC:** " + DateTimeOffset.UtcNow.ToString("O") + "\n\n"
            + (string.IsNullOrWhiteSpace(details) ? "_No additional details provided._" : details.Trim());

        using var request = CreateGitHubRequest(HttpMethod.Post, $"https://api.github.com/repos/{DistributionInfo.Current.Repository}/issues");
        request.Content = new StringContent(JsonSerializer.Serialize(new { title, body }), System.Text.Encoding.UTF8, "application/json");
        using var response = await SendGitHubAsync(request, cancellationToken);
        EnsureGitHubSuccess(response, "Unable to report the incident");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return new IncidentSubmissionResult(
            document.RootElement.GetProperty("number").GetInt32(),
            document.RootElement.GetProperty("html_url").GetString() ?? string.Empty);
    }

    private static string CreateSubmissionBody(Software profile) => $"Please review this Library Manager catalogue profile.\n\n```json\n{JsonSerializer.Serialize(CatalogProfileMapper.ToProfile(profile), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } })}\n```";

    private HttpRequestMessage CreateGitHubRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LibraryManager", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        _authentication.ApplyAuthorization(request);
        return request;
    }

    private async Task<HttpResponseMessage> SendGitHubAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.RecordRequest();
        var response = await _httpClient.SendAsync(request, cancellationToken);
        _authentication.UpdateRateLimitFromResponse(response);
        return response;
    }

    private static void EnsureGitHubSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var rateLimited = response.StatusCode == System.Net.HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.FirstOrDefault() == "0";
        throw new InvalidOperationException(rateLimited ? "GitHub API rate limit reached. Try again after the reset time shown in Settings." : $"{operation} ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private static bool IsGitHubHost(Uri uri) =>
        uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static Uri? ConvertRawGitHubUrlToApi(Uri uri)
    {
        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/', 4);
        if (segments.Length != 4) return null;
        var owner = Uri.EscapeDataString(segments[0]);
        var repository = Uri.EscapeDataString(segments[1]);
        var branch = Uri.EscapeDataString(segments[2]);
        var path = string.Join("/", segments[3].Split('/').Select(Uri.EscapeDataString));
        return new Uri($"https://api.github.com/repos/{owner}/{repository}/contents/{path}?ref={branch}");
    }

    private static JsonElement FindProfilesArray(JsonElement root)
    {
        foreach (var name in new[] { "profiles", "software", "items" })
            if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array) return property;
        return default;
    }
}

public sealed record CatalogueSubmissionPlan(string Title, string Body, int? ExistingIssueNumber, IReadOnlyList<string> Labels)
{
    public string Preview => $"{(ExistingIssueNumber is null ? "Create" : "Update")} GitHub issue\nTitle: {Title}\nLabels: {string.Join(", ", Labels)}\n\n{Body}";
}
public sealed record CatalogueSubmissionResult(int IssueNumber, bool UpdatedExistingIssue);

public sealed record IncidentSubmissionResult(int Number, string Url);
public sealed record SoftwareRequestSubmissionResult(int Number, string Url);
