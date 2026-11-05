// Helper for LibraryManager.
// Generic file/path utilities only.
// Keep this file focused on this single responsibility.
using System.IO;

namespace LibraryManager.Helpers;

public static class FileHelper
{
    public static string GetApplicationDataPath(string fileName)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibraryManager");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, fileName);
    }
}
