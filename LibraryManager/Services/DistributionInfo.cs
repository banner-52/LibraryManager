using System.IO;
using System.Text.Json;

namespace LibraryManager.Services;

/// <summary>Describes the release channel and packaging of the running application.</summary>
public sealed class DistributionInfo
{
    public string Channel { get; init; } = string.Empty;
    public string Distribution { get; init; } = string.Empty;
    public bool IsDev => Channel == "dev";
    public bool IsInstalled => Distribution == "installed";
    public string DevLabel => IsDev ? "[DEV] " : string.Empty;
    public string Repository => IsDev ? "banner-52/Library-manager-dev" : "banner-52/LibraryManager";
    public static DistributionInfo Current { get; } = Load();

    private static DistributionInfo Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "distribution.json");
        if (!File.Exists(path))
            throw new InvalidDataException("Missing distribution.json. A valid release channel and distribution are required.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var channel = root.GetProperty("channel").GetString()?.Trim().ToLowerInvariant();
        var distribution = root.GetProperty("distribution").GetString()?.Trim().ToLowerInvariant();
        if (channel is not ("dev" or "public") || distribution is not ("portable" or "installed"))
            throw new InvalidDataException("distribution.json must specify channel (dev/public) and distribution (portable/installed).");

        return new DistributionInfo { Channel = channel, Distribution = distribution };
    }
}
