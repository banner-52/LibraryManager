// Service for LibraryManager.
// Manage silent install/uninstall command discovery and data.
// Keep this file focused on this single responsibility.
using LibraryManager.Models;

namespace LibraryManager.Services;

public sealed class SilentInstallService
{
    public void SetCommands(Software software, string? installCommand, string? uninstallCommand)
    {
        software.SilentInstallCommand = installCommand?.Trim();
        software.SilentUninstallCommand = uninstallCommand?.Trim();
    }
}
