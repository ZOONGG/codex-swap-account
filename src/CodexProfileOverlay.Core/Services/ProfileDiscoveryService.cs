using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class ProfileDiscoveryService
{
    private readonly string profilesDirectory;

    public ProfileDiscoveryService(string profilesDirectory)
    {
        this.profilesDirectory = Path.GetFullPath(profilesDirectory);
    }

    public IReadOnlyList<ProfileInfo> DiscoverProfiles()
    {
        if (!Directory.Exists(profilesDirectory))
        {
            return Array.Empty<ProfileInfo>();
        }

        return Directory.EnumerateDirectories(profilesDirectory)
            .Select(CreateProfileInfo)
            .Where(static profile => profile is not null)
            .Cast<ProfileInfo>()
            .OrderBy(static profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToArray();
    }

    public ProfileInfo GetRequiredProfile(string profileName)
    {
        string validName = ProfileName.RequireValid(profileName);
        string directory = Path.Combine(profilesDirectory, validName);
        string fullDirectory = Path.GetFullPath(directory);
        EnsureInsideProfilesRoot(fullDirectory);

        string authFile = Path.Combine(fullDirectory, "auth.json");
        if (!File.Exists(authFile))
        {
            throw new FileNotFoundException($"Profile '{validName}' does not contain auth.json.", authFile);
        }

        return new ProfileInfo(validName, fullDirectory, authFile);
    }

    private ProfileInfo? CreateProfileInfo(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        EnsureInsideProfilesRoot(fullDirectory);

        string name = Path.GetFileName(fullDirectory);
        if (!ProfileName.IsValid(name))
        {
            return null;
        }

        string authFile = Path.Combine(fullDirectory, "auth.json");
        return File.Exists(authFile)
            ? new ProfileInfo(name, fullDirectory, authFile)
            : null;
    }

    private void EnsureInsideProfilesRoot(string fullPath)
    {
        string root = Path.TrimEndingDirectorySeparator(profilesDirectory) + Path.DirectorySeparatorChar;
        string candidate = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved profile path escapes the profiles directory.");
        }
    }
}
