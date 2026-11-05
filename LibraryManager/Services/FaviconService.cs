using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LibraryManager.Services;

public sealed class FaviconService
{
    private const int MaxPageBytes = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient? _httpClient;

    public FaviconService(HttpClient? httpClient = null) => _httpClient = httpClient;

    public async Task<string?> FindAsync(string websiteUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(websiteUrl, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Enter an HTTPS source URL before detecting an icon.");

        using var ownedClient = _httpClient is null ? new HttpClient { Timeout = Timeout } : null;
        var client = _httpClient ?? ownedClient!;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LibraryManager/2.0");

        using var response = await client.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var effectivePage = response.RequestMessage?.RequestUri ?? page;
        if (response.Content.Headers.ContentLength > MaxPageBytes)
            return await FindFallbackAsync(client, effectivePage, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes.AsMemory(), cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxPageBytes)
                return await FindFallbackAsync(client, effectivePage, cancellationToken);
            buffer.Write(bytes, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        var html = await reader.ReadToEndAsync(cancellationToken);
        foreach (var candidate in FindDeclaredIcons(html, effectivePage))
            if (await IsReachableAsync(client, candidate, cancellationToken))
                return candidate.AbsoluteUri;

        return await FindFallbackAsync(client, effectivePage, cancellationToken);
    }

    private static List<Uri> FindDeclaredIcons(string html, Uri page)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromSeconds(2);
        var tags = Regex.Matches(html, @"<link\b[^>]*>", options, timeout);
        var attributes = new Regex(@"(?<name>[\w:-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", options, timeout);
        var icons = new List<(Uri Url, int Priority)>();

        foreach (Match tag in tags)
        {
            var values = attributes.Matches(tag.Value).Cast<Match>()
                .ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
            if (!values.TryGetValue("rel", out var rel) || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;
            if (!values.TryGetValue("href", out var href) || !Uri.TryCreate(page, href, out var icon) || icon.Scheme != Uri.UriSchemeHttps) continue;
            var priority = rel.Equals("icon", StringComparison.OrdinalIgnoreCase) ? 0
                : rel.Contains("shortcut", StringComparison.OrdinalIgnoreCase) ? 1
                : rel.Contains("apple-touch", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            icons.Add((icon, priority));
        }

        return icons.OrderBy(x => x.Priority).Select(x => x.Url).Distinct().ToList();
    }

    private static async Task<string> FindFallbackAsync(HttpClient client, Uri page, CancellationToken cancellationToken)
    {
        var fallback = new Uri(page.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
        if (await IsReachableAsync(client, fallback, cancellationToken)) return fallback.AbsoluteUri;
        throw new InvalidDataException("No usable favicon was found on this website.");
    }

    private static async Task<bool> IsReachableAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }
}
