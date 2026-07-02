namespace CodexProfileOverlay.Core.Services;

public sealed class AppPaths
{
    public AppPaths(string userProfile, string localAppData)
    {
        UserProfile = RequireDirectoryLikePath(userProfile, nameof(userProfile));
        LocalAppData = RequireDirectoryLikePath(localAppData, nameof(localAppData));
        SharedCodexDirectory = Path.Combine(UserProfile, ".codex");
        SharedAuthFile = Path.Combine(SharedCodexDirectory, "auth.json");
        ProfilesDirectory = Path.Combine(UserProfile, ".codex-profiles");
        ApplicationDataDirectory = Path.Combine(LocalAppData, "CodexProfileOverlay");
        SettingsFile = Path.Combine(ApplicationDataDirectory, "settings.json");
        ProfilesMetadataFile = Path.Combine(ApplicationDataDirectory, "profiles.json");
        ActiveProfileFile = Path.Combine(ApplicationDataDirectory, "active-profile.txt");
        BackupDirectory = Path.Combine(ApplicationDataDirectory, "backups");
        LogDirectory = Path.Combine(ApplicationDataDirectory, "logs");
        RemovedProfilesDirectory = Path.Combine(ApplicationDataDirectory, "removed-profiles");
        PreflightBackupDirectory = Path.Combine(ApplicationDataDirectory, "preflight-backups");
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string SharedCodexDirectory { get; }

    public string SharedAuthFile { get; }

    public string ProfilesDirectory { get; }

    public string ApplicationDataDirectory { get; }

    public string SettingsFile { get; }

    public string ProfilesMetadataFile { get; }

    public string ActiveProfileFile { get; }

    public string BackupDirectory { get; }

    public string LogDirectory { get; }

    public string RemovedProfilesDirectory { get; }

    public string PreflightBackupDirectory { get; }

    public static AppPaths FromEnvironment()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(userProfile, localAppData);
    }

    private static string RequireDirectoryLikePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", parameterName);
        }

        return Path.GetFullPath(path);
    }
}
