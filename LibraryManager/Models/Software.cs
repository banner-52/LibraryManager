// Model for LibraryManager.
// Describe one managed software profile.
// Keep this file focused on this single responsibility.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LibraryManager.Models;

public sealed class Software : INotifyPropertyChanged
{
    public const string DefaultIconPath = "pack://application:,,,/Assets/LibraryManagerIcon.png";
    private string? _localVersion;
    private string? _availableVersion;
    private SoftwareStatus _status = SoftwareStatus.NotChecked;
    private bool _isSelected;
    private bool _isFavorite;
    private bool _isUpdateNotificationEnabled;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? SoftwareId { get; set; }
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    public string? IconPath { get; set; }
    [JsonIgnore] public string? LocalVersion { get => _localVersion; set => SetField(ref _localVersion, value); }
    [JsonIgnore] public string? AvailableVersion { get => _availableVersion; set => SetField(ref _availableVersion, value); }
    [JsonIgnore] public SoftwareStatus Status { get => _status; set => SetField(ref _status, value); }
    public bool IsFavorite { get => _isFavorite; set => SetField(ref _isFavorite, value); }
    public bool IsUpdateNotificationEnabled { get => _isUpdateNotificationEnabled; set => SetField(ref _isUpdateNotificationEnabled, value); }
    [JsonIgnore] public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public SoftwareSource Source { get; set; } = new();
    public List<AdditionalFile> AdditionalFiles { get; set; } = [];
    public string? SilentInstallCommand { get; set; }
    public string? SilentUninstallCommand { get; set; }
    public bool KeepInstallerFilename { get; set; }
    public string? InstallerFileName { get; set; }
    [JsonIgnore] public string? PackageFileName { get; set; }
    [JsonIgnore] public SoftwareVersion? LastDetectionResult { get; set; }
    [JsonIgnore] public DateTimeOffset? LastCheckedAt { get; set; }

    [JsonIgnore] public string DisplayName => Name;
    [JsonIgnore] public string Icon => string.IsNullOrWhiteSpace(IconPath) ? DefaultIconPath : IconPath;
    [JsonIgnore] public string? Local => LocalVersion;
    [JsonIgnore] public string? Latest => AvailableVersion;
    [JsonIgnore] public bool Selected { get => IsSelected; set => IsSelected = value; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Software Copy() => new()
    {
        Id = Id, Name = Name, SoftwareId = SoftwareId, Description = Description, Publisher = Publisher, IconPath = IconPath,
        LocalVersion = LocalVersion, AvailableVersion = AvailableVersion, Status = Status,
        IsSelected = IsSelected, IsFavorite = IsFavorite, IsUpdateNotificationEnabled = IsUpdateNotificationEnabled, Source = Source.Copy(),
        AdditionalFiles = AdditionalFiles.Select(file => file.Copy()).ToList(),
        SilentInstallCommand = SilentInstallCommand, SilentUninstallCommand = SilentUninstallCommand,
        KeepInstallerFilename = KeepInstallerFilename, InstallerFileName = InstallerFileName,
        PackageFileName = PackageFileName, LastDetectionResult = LastDetectionResult,
        LastCheckedAt = LastCheckedAt
    };

    public void ApplyFrom(Software source)
    {
        Name = source.Name;
        SoftwareId = source.SoftwareId;
        Description = source.Description;
        Publisher = source.Publisher;
        IconPath = source.IconPath;
        LocalVersion = source.LocalVersion;
        AvailableVersion = source.AvailableVersion;
        Status = source.Status;
        IsFavorite = source.IsFavorite;
        IsUpdateNotificationEnabled = source.IsUpdateNotificationEnabled;
        Source = source.Source.Copy();
        AdditionalFiles = source.AdditionalFiles.Select(file => file.Copy()).ToList();
        SilentInstallCommand = source.SilentInstallCommand;
        SilentUninstallCommand = source.SilentUninstallCommand;
        KeepInstallerFilename = source.KeepInstallerFilename;
        InstallerFileName = source.InstallerFileName;
        PackageFileName = source.PackageFileName;
        LastDetectionResult = source.LastDetectionResult;
        LastCheckedAt = source.LastCheckedAt;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(LocalVersion)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Local)));
        if (propertyName == nameof(AvailableVersion)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Latest)));
    }
}

public enum SoftwareStatus { NotChecked, UpToDate, UpdateAvailable, Error }
