// Service for LibraryManager.
// Track external request/API counts and diagnostics.
// Keep this file focused on this single responsibility.
namespace LibraryManager.Services;

public sealed class RequestTrackingService
{
    public int RequestCount { get; private set; }
    public event EventHandler? RequestCountChanged;

    public void RecordRequest()
    {
        RequestCount++;
        RequestCountChanged?.Invoke(this, EventArgs.Empty);
    }
}
