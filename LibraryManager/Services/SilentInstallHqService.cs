using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LibraryManager.Services;

public sealed record SilentInstallCommandResult(string ArticleUrl, string InstallCommand, string UninstallCommand, string ArticleTitle);

public sealed class SilentInstallHqService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex Anchor = new(@"<a\b[^>]*?href\s*=\s*[""'](?<url>[^""'#]+)[""'][^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CodeBlock = new(@"<code\b[^>]*>(?<command>.*?)</code>|<pre\b[^>]*>(?<command>.*?)</pre>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<SilentInstallCommandResult> FindAsync(string softwareName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(softwareName)) throw new InvalidDataException("Enter the software name before searching SilentInstallHQ.");
        var searchUrl = "https://silentinstallhq.com/?s=" + Uri.EscapeDataString(softwareName.Trim());
        var searchPage = await DownloadAsync(searchUrl, cancellationToken);
        var candidate = FindBestArticle(searchPage, softwareName) ?? throw new InvalidDataException("No matching SilentInstallHQ article was found. You can still enter the commands manually.");
        var article = await DownloadAsync(candidate.Url, cancellationToken);
        // Prefer the summary table: SilentInstallHQ consistently labels the exact
        // install/uninstall switch there. Section-based parsing can accidentally pick
        // unrelated code blocks (for example a version number) later in the article.
        var install = FindLabeledCommand(article, "Silent Install Switch");
        var uninstall = FindLabeledCommand(article, "Silent Uninstall Switch");

        // Keep the article-section parser only as a fallback for older pages that do
        // not expose the commands in the summary table.
        var installStart = article.IndexOf("how to install", StringComparison.OrdinalIgnoreCase);
        var uninstallStart = article.IndexOf("how to uninstall", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(install))
            install = FindCommand(article, installStart, uninstallStart);
        if (string.IsNullOrWhiteSpace(uninstall))
            uninstall = FindCommand(article, uninstallStart, article.Length);
        if (string.IsNullOrWhiteSpace(install) && string.IsNullOrWhiteSpace(uninstall)) throw new InvalidDataException("An article was found, but no command block could be extracted.");
        return new SilentInstallCommandResult(candidate.Url, install, uninstall, candidate.Title);
    }

    private static async Task<string> DownloadAsync(string url, CancellationToken ct) { using var response=await Client.GetAsync(url,ct); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(ct); }
    private static Article? FindBestArticle(string html,string softwareName)
    {
        var source=new Uri("https://silentinstallhq.com/"); var query=Normalize(softwareName);
        return Anchor.Matches(html).Select(m=>CreateArticle(m,source,query)).Where(x=>x is not null).Cast<Article>().OrderByDescending(x=>x.Score).ThenBy(x=>x.Title,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
    private static Article? CreateArticle(Match match,Uri source,string query)
    {
        if(!Uri.TryCreate(source,WebUtility.HtmlDecode(match.Groups["url"].Value),out var uri)||uri.Scheme!=Uri.UriSchemeHttps||!uri.Host.Equals("silentinstallhq.com",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath=="/") return null;
        var title=PlainText(match.Groups["title"].Value); var normalized=Normalize(title); if(string.IsNullOrWhiteSpace(title)||!normalized.Contains("silent")) return null;
        var score=normalized.Contains(query,StringComparison.Ordinal)?1000:0; score+=query.Split(' ',StringSplitOptions.RemoveEmptyEntries).Count(word=>normalized.Contains(word,StringComparison.Ordinal))*100;
        return score==0?null:new Article(uri.AbsoluteUri,title,score);
    }
    private static string FindLabeledCommand(string html, string label)
    {
        var labelIndex = html.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0) return string.Empty;

        // Limit extraction to the current table row so a nearby Version/Download
        // value can never be mistaken for the requested command.
        var rowStart = html.LastIndexOf("<tr", labelIndex, StringComparison.OrdinalIgnoreCase);
        var rowEnd = html.IndexOf("</tr>", labelIndex, StringComparison.OrdinalIgnoreCase);
        if (rowStart < 0 || rowEnd < 0) return string.Empty;
        rowEnd += 5;

        var row = html[rowStart..rowEnd];
        foreach (Match match in CodeBlock.Matches(row))
        {
            var command = PlainText(match.Groups["command"].Value).Replace("\r", " ").Replace("\n", " ").Trim();
            if (!string.IsNullOrWhiteSpace(command)) return command;
        }

        // Some WordPress/table variants render the value as plain cell text instead
        // of <code>/<pre>. Read the cell following the label in that case.
        var cells = Regex.Matches(row, @"<t[dh]\b[^>]*>(?<value>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (cells.Count >= 2)
        {
            var value = PlainText(cells[^1].Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains(label, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return string.Empty;
    }

    private static string FindCommand(string html,int start,int end)
    {
        if(start<0)return ""; if(end<0)end=html.Length;
        foreach(Match match in CodeBlock.Matches(html,start)){if(match.Index>=end)break;var command=PlainText(match.Groups["command"].Value).Replace("\r"," ").Replace("\n"," ").Trim();if(!string.IsNullOrWhiteSpace(command))return command;} return "";
    }
    private static string PlainText(string html)=>Regex.Replace(WebUtility.HtmlDecode(Tags.Replace(html," ")),@"\s+"," ").Trim();
    private static string Normalize(string value)=>Regex.Replace(value.ToLowerInvariant(),@"[^a-z0-9]+"," ").Trim();
    private static HttpClient CreateClient(){var client=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};client.DefaultRequestHeaders.UserAgent.ParseAdd("LibraryManager/2.0");return client;}
    private sealed record Article(string Url,string Title,int Score);
}