using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

internal sealed class TestLayout : IDisposable
{
    private readonly TempDirectory tempDirectory = new();

    public TestLayout()
    {
        UserProfile = Path.Combine(tempDirectory.Path, "user");
        LocalAppData = Path.Combine(tempDirectory.Path, "local");
        Paths = new AppPaths(UserProfile, LocalAppData);
        Directory.CreateDirectory(Paths.SharedCodexDirectory);
        Directory.CreateDirectory(Paths.ProfilesDirectory);
        ActiveProfileStore = new ActiveProfileStore(Paths.ActiveProfileFile);
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public AppPaths Paths { get; }

    public ActiveProfileStore ActiveProfileStore { get; }

    public AuthSwitchService CreateSwitchService(IAtomicFileReplacer? replacer = null)
    {
        return new AuthSwitchService(
            Paths,
            new ProfileDiscoveryService(Paths.ProfilesDirectory),
            ActiveProfileStore,
            replacer);
    }

    public void AddProfile(string name, string authContent)
    {
        string directory = Path.Combine(Paths.ProfilesDirectory, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "auth.json"), authContent);
    }

    public void WriteSharedAuth(string authContent)
    {
        File.WriteAllText(Paths.SharedAuthFile, authContent);
    }

    public string ReadSharedAuth() => File.ReadAllText(Paths.SharedAuthFile);

    public string ReadProfileAuth(string name)
    {
        return File.ReadAllText(Path.Combine(Paths.ProfilesDirectory, name, "auth.json"));
    }

    public void Dispose()
    {
        tempDirectory.Dispose();
    }
}
