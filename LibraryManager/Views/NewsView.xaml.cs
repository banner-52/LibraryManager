using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LibraryManager.Models;
using LibraryManager.Services;

namespace LibraryManager.Views;

public partial class NewsView : UserControl
{
    private readonly NewsService _newsService = new();
    private readonly SettingsService _settingsService = new();
    private readonly ObservableCollection<NewsDisplayItem> _items = [];
    private NewsFeed _feed = new();

    public NewsView()
    {
        InitializeComponent();
        NewsItems.ItemsSource = _items;
        Loaded += NewsView_Loaded;
    }

    public event Action<int>? UnreadCountChanged;

    private async void NewsView_Loaded(object sender, RoutedEventArgs e) => await LoadAsync(false);
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync(true);
    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private async Task LoadAsync(bool forceRefresh)
    {
        StatusTextBlock.Text = "Loading news...";
        try
        {
            _feed = await _newsService.LoadAsync(forceRefresh);
            await RemoveExpiredReadIdsAsync();
            ApplyFilter();
            StatusTextBlock.Text = _feed.Sections.All().Any() ? $"Updated {_feed.UpdatedUtc.LocalDateTime:g}" : "No news available.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Unable to load news: {ex.Message}";
        }
    }

    private async void MarkAllReadButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = await _settingsService.LoadAsync();
        settings.ReadNewsIds = _feed.Sections.All().Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _settingsService.SaveAsync(settings);
        ApplyFilter();
        UnreadCountChanged?.Invoke(0);
    }

    private async Task RemoveExpiredReadIdsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var available = _feed.Sections.All().Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleaned = settings.ReadNewsIds.Where(available.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleaned.Count == settings.ReadNewsIds.Count) return;
        settings.ReadNewsIds = cleaned;
        await _settingsService.SaveAsync(settings);
    }

    private async void ApplyFilter()
    {
        if (FilterComboBox is null || NewsItems is null) return;
        var settings = await _settingsService.LoadAsync();
        var read = settings.ReadNewsIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = (FilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var source = selected switch
        {
            "Catalogue" => _feed.Sections.Catalogue.Select(item => new NewsDisplayItem(item, "CATALOGUE", read.Contains(item.Id))),
            "Features" => _feed.Sections.Features.Select(item => new NewsDisplayItem(item, "FEATURE", read.Contains(item.Id))),
            "Fixes" => _feed.Sections.Fixes.Select(item => new NewsDisplayItem(item, "FIXED", read.Contains(item.Id))),
            _ => _feed.Sections.Catalogue.Select(item => new NewsDisplayItem(item, "CATALOGUE", read.Contains(item.Id)))
                .Concat(_feed.Sections.Features.Select(item => new NewsDisplayItem(item, "FEATURE", read.Contains(item.Id))))
                .Concat(_feed.Sections.Fixes.Select(item => new NewsDisplayItem(item, "FIXED", read.Contains(item.Id))))
        };
        _items.Clear();
        foreach (var item in source.OrderByDescending(item => item.Item.Date)) _items.Add(item);

        // Opening News counts the displayed entries as read. Downloading them in the background does not.
        var visibleIds = _items.Select(item => item.Item.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (visibleIds.Except(read).Any())
        {
            settings.ReadNewsIds = read.Union(visibleIds).ToList();
            await _settingsService.SaveAsync(settings);
            foreach (var item in _items) item.IsRead = true;
        }
        var allIds = _feed.Sections.All().Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id));
        UnreadCountChanged?.Invoke(allIds.Count(id => !settings.ReadNewsIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        NewsItems.Items.Refresh();
    }
}

public sealed class NewsDisplayItem
{
    public NewsDisplayItem(NewsItem item, string category, bool isRead) { Item = item; Category = category; IsRead = isRead; }
    public NewsItem Item { get; }
    public string Category { get; }
    public bool IsRead { get; set; }
    public string UnreadMarker => IsRead ? string.Empty : "●";
    public string DateText => Item.Date.LocalDateTime.ToString("d");
}
