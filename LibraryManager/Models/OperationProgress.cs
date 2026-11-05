// Model for LibraryManager.
// Describe progress/status information passed from Services to UI.
// Keep this file focused on this single responsibility.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace LibraryManager.Models;

public sealed class OperationProgress : INotifyPropertyChanged
{
    private string _message = string.Empty;
    private int _percentage;
    private bool _canCancel;
    private string _currentSoftware = string.Empty;
    private bool _isIndeterminate;

    public string Message { get => _message; set { _message = value; Notify(); Notify(nameof(ProgressText)); } }
    public int Percentage { get => _percentage; set { _percentage = value; Notify(); Notify(nameof(ProgressText)); } }
    public bool CanCancel { get => _canCancel; set { _canCancel = value; Notify(); } }
    public string CurrentSoftware { get => _currentSoftware; set { _currentSoftware = value; Notify(); } }
    public bool IsIndeterminate { get => _isIndeterminate; set { _isIndeterminate = value; Notify(); } }
    public string ProgressText => string.IsNullOrWhiteSpace(Message) ? $"{Percentage}%" : $"{Message} — {Percentage}%";
    public ObservableCollection<string> LogEntries { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PackageProgress(string Message, int Percentage, long? BytesProcessed = null, long? TotalBytes = null);
