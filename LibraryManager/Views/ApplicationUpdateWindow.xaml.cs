using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LibraryManager.Models;

namespace LibraryManager.Views;

public partial class ApplicationUpdateWindow : ThemedWindow
{
    private readonly Uri? _releaseUri;
    private static readonly Regex MarkdownTokenRegex = new(
        @"!\[(?<alt>[^\]]*)\]\((?<image>https://[^\s)]+)(?:\s+""[^""]*"")?\)|<img\b[^>]*?\bsrc\s*=\s*[""'](?<htmlimage>https://[^""']+)[""'][^>]*>|\[(?<text>[^\]]+)\]\((?<link>https://[^\s)]+)(?:\s+""[^""]*"")?\)|(?<url>https://[^\s<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public string TitleText { get; }

    public ApplicationUpdateWindow(ApplicationUpdate update)
    {
        InitializeComponent();
        TitleText = $"Library Manager {update.Version} is available";
        DataContext = this;
        _releaseUri = Uri.TryCreate(update.ReleaseUrl, UriKind.Absolute, out var releaseUri) && releaseUri.Scheme == Uri.UriSchemeHttps
            ? releaseUri
            : null;
        FullReleaseNotesButton.IsEnabled = _releaseUri is not null;
        RenderReleaseNotes(update.ReleaseNotes);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void FullReleaseNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_releaseUri is null) return;
        try { Process.Start(new ProcessStartInfo(_releaseUri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    private void RenderReleaseNotes(string? notes)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Foreground = Foreground
        };
        if (string.IsNullOrWhiteSpace(notes))
        {
            document.Blocks.Add(new Paragraph(new Run("No release notes were provided for this version.")));
            ReleaseNotesBox.Document = document;
            return;
        }

        // GitHub can wrap HTML image tags across multiple lines in release bodies.
        // Normalize them first so the inline parser receives one complete <img> token.
        var normalizedNotes = Regex.Replace(
            notes.Replace("\r\n", "\n"),
            @"<img\b[\s\S]*?>",
            match => Regex.Replace(match.Value, @"\s*\n\s*", " "),
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        foreach (var rawLine in normalizedNotes.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 3, 0, 3) }); continue; }

            var headingLevel = line.TakeWhile(c => c == '#').Count();
            if (headingLevel is > 0 and <= 6 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                var heading = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = headingLevel <= 2 ? 18 : 15, Margin = new Thickness(0, 8, 0, 4) };
                AddInlineContent(heading, line[(headingLevel + 1)..]);
                document.Blocks.Add(heading);
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 4) };
            var content = line;
            if (content.StartsWith("- ") || content.StartsWith("* ")) { paragraph.Inlines.Add(new Run("• ")); content = content[2..]; }
            AddInlineContent(paragraph, content);
            document.Blocks.Add(paragraph);
        }
        ReleaseNotesBox.Document = document;
    }

    private void AddInlineContent(Paragraph paragraph, string text)
    {
        var position = 0;
        foreach (Match match in MarkdownTokenRegex.Matches(text))
        {
            if (match.Index > position) paragraph.Inlines.Add(new Run(CleanMarkdown(text[position..match.Index])));
            if (match.Groups["image"].Success || match.Groups["htmlimage"].Success)
            {
                var imageUrl = match.Groups["image"].Success ? match.Groups["image"].Value : match.Groups["htmlimage"].Value;
                AddImage(paragraph, imageUrl, match.Groups["alt"].Value);
            }
            else
            {
                var url = match.Groups["link"].Success ? match.Groups["link"].Value : match.Groups["url"].Value;
                var label = match.Groups["link"].Success ? match.Groups["text"].Value : url.TrimEnd('.', ',', ';', ':');
                AddLink(paragraph, CleanMarkdown(label), url.TrimEnd('.', ',', ';', ':'));
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length) paragraph.Inlines.Add(new Run(CleanMarkdown(text[position..])));
    }

    private static string CleanMarkdown(string text) => text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("\x60", string.Empty);

    private static void AddLink(Paragraph paragraph, string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) { paragraph.Inlines.Add(new Run(label)); return; }
        var hyperlink = new Hyperlink(new Run(label))
        {
            NavigateUri = uri,
            Foreground = System.Windows.Media.Brushes.DodgerBlue,
            TextDecorations = TextDecorations.Underline,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        hyperlink.RequestNavigate += (_, args) =>
        {
            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            args.Handled = true;
        };
        paragraph.Inlines.Add(hyperlink);
    }

    private static void AddImage(Paragraph paragraph, string url, string altText)
    {
        // Images embedded in GitHub release notes are intentionally not loaded in-app.
        // The full GitHub release remains available through the dedicated button.
        if (!string.IsNullOrWhiteSpace(altText) && !string.Equals(altText, "image", StringComparison.OrdinalIgnoreCase))
            paragraph.Inlines.Add(new Run($"[{altText}]"));
    }
}