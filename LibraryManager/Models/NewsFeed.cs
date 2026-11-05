namespace LibraryManager.Models;

public sealed class NewsFeed
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedUtc { get; set; }
    public NewsSections Sections { get; set; } = new();
}

public sealed class NewsSections
{
    public List<NewsItem> Catalogue { get; set; } = [];
    public List<NewsItem> Features { get; set; } = [];
    public List<NewsItem> Fixes { get; set; } = [];

    public IEnumerable<NewsItem> All() => Catalogue.Concat(Features).Concat(Fixes);
}

public sealed class NewsItem
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SoftwareId { get; set; }
}
