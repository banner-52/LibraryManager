// Model for persistent LibraryManager application settings.
// It will include repository path, theme, startup behavior, self-update preferences, and non-sensitive GitHub preferences.
// It may remember the selected GitHub authentication method, but OAuth credentials and Personal Access Tokens must not be stored here as plain text.
namespace LibraryManager.Models;

public sealed class AppSettings
{
    public string RepositoryPath { get; set; } = string.Empty;
    public AppDesign Design { get; set; } = AppDesign.Modern;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool CheckForUpdatesAtStartup { get; set; } = true;
    public bool CheckForApplicationUpdates { get; set; } = true;
    public bool EnableBackgroundUpdateNotifications { get; set; }
    public string DailyUpdateCheckTime { get; set; } = "09:00";
    public BackgroundUpdateFrequency BackgroundUpdateFrequency { get; set; } = BackgroundUpdateFrequency.Daily;
    public DayOfWeek WeeklyUpdateCheckDay { get; set; } = DayOfWeek.Monday;
    public bool EnableLogging { get; set; } = true;
    public int LogRetentionDays { get; set; } = 30;
    public double PanelOpacity { get; set; } = 1;
    public string? BackgroundImagePath { get; set; }
    public string BuiltInBackground { get; set; } = "None";
    public static string OfficialOnlineCatalogueUrl => $"https://raw.githubusercontent.com/{LibraryManager.Services.DistributionInfo.Current.Repository}/main/OnlineCatalog/catalog.json";
    public string OnlineCatalogueUrl { get; set; } = OfficialOnlineCatalogueUrl;
    public bool UseCustomOnlineCatalogue { get; set; }
    public bool CanSubmitToOnlineCatalogue => !UseCustomOnlineCatalogue;
    public string EffectiveOnlineCatalogueUrl => UseCustomOnlineCatalogue ? OnlineCatalogueUrl : OfficialOnlineCatalogueUrl;
    public bool EnableOnlineCatalogue { get; set; } = true;
    public bool ShowRequestCounter { get; set; } = true;
    public bool ShowFirstLaunchGuide { get; set; } = true;
    public List<string> ReadNewsIds { get; set; } = [];
    public string CustomBackgroundColor { get; set; } = "#FFF5F6F8";
    public string CustomSurfaceColor { get; set; } = "#FFFFFFFF";
    public string CustomAccentColor { get; set; } = "#FF3B82F6";
}

public enum BackgroundUpdateFrequency { Daily, Weekly }
public enum AppDesign { Modern, Classic }
public enum ThemeMode { Light, Dark, System, Custom }
