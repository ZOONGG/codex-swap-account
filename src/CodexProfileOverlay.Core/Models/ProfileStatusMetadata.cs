namespace CodexProfileOverlay.Core.Models;

public sealed class ProfileStatusMetadata
{
    public string ProfileId { get; set; } = string.Empty;

    public bool AutomaticRefreshEnabled { get; set; } = true;

    public DateTimeOffset? LastAutomaticSnapshot { get; set; }

    public DateTimeOffset? LastRefreshAttemptAt { get; set; }

    public string? LastRefreshError { get; set; }
}
