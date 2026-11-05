namespace LibraryManager.Models;

public sealed class AdditionalFile
{
    public string Name { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string FilePattern { get; set; } = string.Empty;
    public string SampleDownloadUrl { get; set; } = string.Empty;
    public string LinkTemplate { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public bool Required { get; set; } = true;

    // Compatibility aliases for profiles created by the rebuilt UI.
    public string? DestinationPath { get => DestinationFolder; set => DestinationFolder = value ?? string.Empty; }
    public string? DestinationFileName { get; set; }

    public AdditionalFile Copy() => new()
    {
        Name = Name,
        SourceUrl = SourceUrl,
        FilePattern = FilePattern,
        SampleDownloadUrl = SampleDownloadUrl,
        LinkTemplate = LinkTemplate,
        DestinationFolder = DestinationFolder,
        Required = Required,
        DestinationFileName = DestinationFileName
    };
}