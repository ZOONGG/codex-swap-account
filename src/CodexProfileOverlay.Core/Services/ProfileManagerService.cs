using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class ProfileManagerService
{
    private readonly AppPaths paths;
    private readonly ProfileDiscoveryService discovery;
    private readonly ProfileMetadataStore metadataStore;

    public ProfileManagerService(AppPaths paths, ProfileDiscoveryService discovery, ProfileMetadataStore metadataStore)
    {
        this.paths = paths;
        this.discovery = discovery;
        this.metadataStore = metadataStore;
    }

    public IReadOnlyList<ProfileInfo> ListProfiles()
    {
        IReadOnlyList<ProfileInfo> discovered = discovery.DiscoverProfiles();
        ProfileMetadataDocument document = metadataStore.Load();
        Dictionary<string, ProfileMetadata> metadata = document.Profiles
            .Where(static profile => ProfileName.IsValid(profile.DirectoryName))
            .GroupBy(static profile => profile.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        return discovered
            .Select((profile, index) =>
            {
                if (!metadata.TryGetValue(profile.Name, out ProfileMetadata? item))
                {
                    item = CreateMetadata(profile.Name, index);
                }

                return profile with
                {
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? profile.Name : item.DisplayName,
                    Order = item.Order,
                    Initials = string.IsNullOrWhiteSpace(item.Initials) ? CreateInitials(item.DisplayName, profile.Name) : item.Initials,
                    Accent = string.IsNullOrWhiteSpace(item.Accent) ? "#A970FF" : item.Accent,
                    Hidden = item.Hidden,
                };
            })
            .Where(static profile => !profile.Hidden)
            .OrderBy(static profile => profile.Order)
            .ThenBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .ToArray();
    }

    public void EnsureMetadata()
    {
        IReadOnlyList<ProfileInfo> profiles = discovery.DiscoverProfiles();
        ProfileMetadataDocument document = metadataStore.Load();
        bool changed = false;
        int nextOrder = document.Profiles.Count == 0 ? 0 : document.Profiles.Max(static profile => profile.Order) + 1;

        foreach (ProfileInfo profile in profiles)
        {
            if (document.Profiles.Any(item => string.Equals(item.DirectoryName, profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            document.Profiles.Add(CreateMetadata(profile.Name, nextOrder++));
            changed = true;
        }

        if (changed)
        {
            metadataStore.Save(document);
        }
    }

    public string CreateProfileDirectory(string profileName)
    {
        string validName = ProfileName.RequireValid(profileName);
        string directory = Path.GetFullPath(Path.Combine(paths.ProfilesDirectory, validName));
        EnsureInsideProfilesRoot(directory);

        if (Directory.Exists(directory))
        {
            throw new InvalidOperationException("A profile with that directory name already exists.");
        }

        Directory.CreateDirectory(directory);
        string configFile = Path.Combine(directory, "config.toml");
        if (!File.Exists(configFile))
        {
            File.WriteAllText(configFile, "cli_auth_credentials_store = \"file\"" + Environment.NewLine);
        }

        UpsertMetadata(validName, validName);
        return directory;
    }

    public void RenameDisplayName(string directoryName, string displayName)
    {
        string validName = ProfileName.RequireValid(directoryName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name cannot be empty.", nameof(displayName));
        }

        UpsertMetadata(validName, displayName.Trim());
    }

    public string RenameDirectory(string oldDirectoryName, string newDirectoryName)
    {
        string oldName = ProfileName.RequireValid(oldDirectoryName);
        string newName = ProfileName.RequireValid(newDirectoryName);
        string oldDirectory = Path.GetFullPath(Path.Combine(paths.ProfilesDirectory, oldName));
        string newDirectory = Path.GetFullPath(Path.Combine(paths.ProfilesDirectory, newName));
        EnsureInsideProfilesRoot(oldDirectory);
        EnsureInsideProfilesRoot(newDirectory);

        if (!Directory.Exists(oldDirectory))
        {
            throw new DirectoryNotFoundException("Profile directory was not found.");
        }

        if (Directory.Exists(newDirectory))
        {
            throw new InvalidOperationException("A profile with the new name already exists.");
        }

        Directory.Move(oldDirectory, newDirectory);
        ProfileMetadataDocument document = metadataStore.Load();
        ProfileMetadata? item = document.Profiles.FirstOrDefault(profile => string.Equals(profile.DirectoryName, oldName, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.DirectoryName = newName;
            if (string.Equals(item.DisplayName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                item.DisplayName = newName;
                item.Initials = CreateInitials(newName, newName);
            }

            metadataStore.Save(document);
        }

        return newDirectory;
    }

    public string RemoveProfile(string directoryName, string? activeProfile)
    {
        string validName = ProfileName.RequireValid(directoryName);
        if (string.Equals(validName, activeProfile, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The active profile cannot be removed.");
        }

        string source = Path.GetFullPath(Path.Combine(paths.ProfilesDirectory, validName));
        EnsureInsideProfilesRoot(source);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("Profile directory was not found.");
        }

        Directory.CreateDirectory(paths.RemovedProfilesDirectory);
        string destination = Path.Combine(
            paths.RemovedProfilesDirectory,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{validName}");
        Directory.Move(source, destination);

        ProfileMetadataDocument document = metadataStore.Load();
        foreach (ProfileMetadata item in document.Profiles.Where(item => string.Equals(item.DirectoryName, validName, StringComparison.OrdinalIgnoreCase)))
        {
            item.Hidden = true;
        }

        metadataStore.Save(document);
        return destination;
    }

    public void Reorder(IReadOnlyList<string> orderedDirectoryNames)
    {
        ProfileMetadataDocument document = metadataStore.Load();
        for (int index = 0; index < orderedDirectoryNames.Count; index++)
        {
            string name = ProfileName.RequireValid(orderedDirectoryNames[index]);
            ProfileMetadata? item = document.Profiles.FirstOrDefault(profile => string.Equals(profile.DirectoryName, name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                document.Profiles.Add(CreateMetadata(name, index));
            }
            else
            {
                item.Order = index;
            }
        }

        metadataStore.Save(document);
    }

    private void UpsertMetadata(string directoryName, string displayName)
    {
        ProfileMetadataDocument document = metadataStore.Load();
        ProfileMetadata? item = document.Profiles.FirstOrDefault(profile => string.Equals(profile.DirectoryName, directoryName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            int order = document.Profiles.Count == 0 ? 0 : document.Profiles.Max(static profile => profile.Order) + 1;
            document.Profiles.Add(CreateMetadata(directoryName, order, displayName));
        }
        else
        {
            item.DisplayName = displayName;
            item.Initials = CreateInitials(displayName, directoryName);
            item.Hidden = false;
        }

        metadataStore.Save(document);
    }

    private static ProfileMetadata CreateMetadata(string directoryName, int order, string? displayName = null)
    {
        string resolvedDisplayName = string.IsNullOrWhiteSpace(displayName) ? directoryName : displayName;
        return new ProfileMetadata
        {
            DirectoryName = directoryName,
            DisplayName = resolvedDisplayName,
            Order = order,
            Initials = CreateInitials(resolvedDisplayName, directoryName),
        };
    }

    private static string CreateInitials(string? displayName, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(displayName) ? fallback : displayName;
        string[] parts = source.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return fallback[..Math.Min(2, fallback.Length)].ToUpperInvariant();
        }

        string initials = string.Concat(parts.Take(2).Select(static part => char.ToUpperInvariant(part[0])));
        return initials[..Math.Min(2, initials.Length)];
    }

    private void EnsureInsideProfilesRoot(string fullPath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ProfilesDirectory)) + Path.DirectorySeparatorChar;
        string candidate = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved profile path escapes the profiles directory.");
        }
    }
}
