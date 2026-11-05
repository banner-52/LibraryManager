using System.IO;

namespace LibraryManager.Services;

public static class RepositoryFolder
{
    public static bool IsSafeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or "..") return false;
        if (Path.IsPathRooted(folderName) || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return !folderName.Contains(Path.DirectorySeparatorChar) && !folderName.Contains(Path.AltDirectorySeparatorChar);
    }

    public static string GetPath(string repositoryPath, string? folderName)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath)) throw new ArgumentException("A repository folder is required.", nameof(repositoryPath));
        if (!IsSafeFolderName(folderName)) throw new InvalidDataException("The software name must be a single safe folder name.");

        var repositoryRoot = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(repositoryRoot, folderName!));
        var prefix = repositoryRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The software folder must remain inside the selected repository.");
        return candidate;
    }
}
