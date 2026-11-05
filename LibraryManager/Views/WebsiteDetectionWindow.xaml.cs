using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using LibraryManager.Services;

namespace LibraryManager.Views;

public partial class WebsiteDetectionWindow : ThemedWindow
{
    private CancellationTokenSource? _scanCancellation;
    public string? SelectedUrl { get; private set; }
    public string? SelectedVersion { get; private set; }
    public WebsiteDetectionWindow(string name, string sourceUrl) { InitializeComponent(); SoftwareNameTextBox.Text=name; PageUrlTextBox.Text=sourceUrl; }
    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(PageUrlTextBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) { StatusTextBlock.Text="Enter a valid HTTP or HTTPS page URL."; return; }
        _scanCancellation?.Cancel(); _scanCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            StatusTextBlock.Text="Reading the page and looking for download links...";
            using var client=new HttpClient(); client.DefaultRequestHeaders.UserAgent.ParseAdd("LibraryManager/2.0");
            string html; Uri effective=uri;
            try { using var response=await client.GetAsync(uri,_scanCancellation.Token); response.EnsureSuccessStatusCode(); html=await response.Content.ReadAsStringAsync(_scanCancellation.Token); effective=response.RequestMessage?.RequestUri??uri; }
            catch (HttpRequestException) { StatusTextBlock.Text="Direct request rejected; rendering with WebView2..."; html=await BrowserPageReader.ReadHtmlAsync(uri,_scanCancellation.Token); }
            var candidates=Extract(html,effective,SoftwareNameTextBox.Text,_scanCancellation.Token);
            if(candidates.Count==0){ StatusTextBlock.Text="No links in static HTML; rendering with WebView2..."; html=await BrowserPageReader.ReadHtmlAsync(effective,_scanCancellation.Token); candidates=Extract(html,effective,SoftwareNameTextBox.Text,_scanCancellation.Token); }
            ResultsDataGrid.ItemsSource=candidates; StatusTextBlock.Text=candidates.Count==0?"No installer candidate found.":$"{candidates.Count} candidate(s) found.";
        } catch(OperationCanceledException){StatusTextBlock.Text="Scan cancelled or timed out.";} catch(Exception ex){StatusTextBlock.Text=$"Detection failed: {ex.Message}";}
    }
    private void CancelScanButton_Click(object sender,RoutedEventArgs e)=>_scanCancellation?.Cancel();
    private void UseSelectedButton_Click(object sender,RoutedEventArgs e){if(ResultsDataGrid.SelectedItem is not Candidate x){StatusTextBlock.Text="Select a result first.";return;} SelectedUrl=x.Url;SelectedVersion=x.DetectedVersion;DialogResult=true;}
    private static List<Candidate> Extract(string html,Uri page,string name,CancellationToken ct)
    {
        var opt=RegexOptions.IgnoreCase|RegexOptions.Singleline; var timeout=TimeSpan.FromSeconds(2);
        var anchor=new Regex(@"<a\b[^>]*\bhref\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))[^>]*>(?<t>.*?)</a\s*>",opt,timeout);
        var ext=new Regex(@"\.(exe|msi|zip|msix|msixbundle|appx|7z)(?:$|[?&#/])",opt,timeout);
        var normalizedName=Regex.Replace(name.ToLowerInvariant(),@"[^a-z0-9]","");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var found=new List<Candidate>();
        foreach(Match m in anchor.Matches(html)){ct.ThrowIfCancellationRequested(); if(!Uri.TryCreate(page,WebUtility.HtmlDecode(m.Groups["u"].Value),out var link)||link.Scheme is not("http" or "https")||!seen.Add(link.AbsoluteUri))continue;
            var label=WebUtility.HtmlDecode(Regex.Replace(m.Groups["t"].Value,"<[^>]+>"," ")).Trim(); var decoded=Uri.UnescapeDataString(link.AbsoluteUri); if(!ext.IsMatch(decoded)&&!Regex.IsMatch(label+" "+decoded,@"download|installer|setup",opt,timeout))continue;
            var file=Path.GetFileName(Uri.UnescapeDataString(link.AbsolutePath).TrimEnd('/')); var versions=LibraryManager.Services.Detection.DownloadLinkContext.FindVersionCandidates(html,m.Groups["u"].Index,m.Groups["u"].Length,decoded,label); var normalized=Regex.Replace((label+decoded).ToLowerInvariant(),@"[^a-z0-9]",""); var baseScore=(ext.IsMatch(decoded)?20:0)+(normalizedName.Length>0&&normalized.Contains(normalizedName)?40:0)+(Regex.IsMatch(label+decoded,@"win|x64|amd64",opt,timeout)?10:0)-(Regex.IsMatch(label+decoded,@"linux|macos|darwin|\.dmg",opt,timeout)?30:0);
            if(versions.Count==0) found.Add(new Candidate(link.AbsoluteUri,file,"","",baseScore>=55?"Medium":"Low",baseScore));
            else foreach(var detectedVersion in versions){var score=baseScore+10; found.Add(new Candidate(link.AbsoluteUri,file,detectedVersion.Version,detectedVersion.Source,score>=55?"High":"Medium",score));}}
        return found.OrderByDescending(x=>x.Score).ThenBy(x=>x.FileName).Take(300).ToList();
    }
    private sealed record Candidate(string Url,string FileName,string DetectedVersion,string VersionSource,string Confidence,int Score);
}
