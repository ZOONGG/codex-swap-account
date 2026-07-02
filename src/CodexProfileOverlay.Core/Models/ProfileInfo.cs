namespace CodexProfileOverlay.Core.Models;

public sealed record ProfileInfo(string Name, string DirectoryPath, string AuthFilePath)
{
    public string DisplayName { get; init; } = Name;

    public int Order { get; init; }

    public string Initials { get; init; } = Name[..Math.Min(2, Name.Length)].ToUpperInvariant();

    public string Accent { get; init; } = "#A970FF";

    public bool Hidden { get; init; }
}
