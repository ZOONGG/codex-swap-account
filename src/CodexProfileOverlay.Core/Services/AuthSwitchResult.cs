namespace CodexProfileOverlay.Core.Services;

public sealed record AuthSwitchResult(string TargetProfile, string? PreviousProfile, string? BackupPath);
