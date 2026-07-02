namespace CodexProfileOverlay.Core.Models;

public sealed class ProfileMetadata
{
    public string DirectoryName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int Order { get; set; }

    public string Initials { get; set; } = string.Empty;

    public string Accent { get; set; } = "#A970FF";

    public bool Hidden { get; set; }
}
