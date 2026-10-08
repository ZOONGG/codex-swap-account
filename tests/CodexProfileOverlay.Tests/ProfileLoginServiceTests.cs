using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class ProfileLoginServiceTests
{
    private sealed class Runtime(Func<string, CancellationToken, Task<bool>> login) : IProfileLoginRuntime
    {
        public Task<bool> LoginAsync(string directory, CancellationToken cancellationToken) => login(directory, cancellationToken);
    }

    private static ProfileManagerService Manager(TestLayout layout) => new(layout.Paths,
        new ProfileDiscoveryService(layout.Paths.ProfilesDirectory), new ProfileMetadataStore(layout.Paths.ProfilesMetadataFile));

    private static Runtime Success() => new((directory, _) =>
    {
        File.WriteAllText(Path.Combine(directory, "auth.json"), "fake-new-credential");
        File.WriteAllText(Path.Combine(directory, "login.log"), "fake-runtime-data");
        return Task.FromResult(true);
    });

    [Fact]
    public async Task CreationOnlyPublishesAuthorizationAfterOfficialLoginSuccess()
    {
        using var layout = new TestLayout();
        var manager = Manager(layout);
        var runtime = new Runtime((directory, _) =>
        {
            Assert.Empty(manager.ListProfiles());
            Assert.False(directory.StartsWith(layout.Paths.ProfilesDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("cli_auth_credentials_store", File.ReadAllText(Path.Combine(directory, "config.toml")));
            File.WriteAllText(Path.Combine(directory, "auth.json"), "fake-created");
            return Task.FromResult(true);
        });
        Assert.True(await new ProfileLoginService(layout.Paths, manager, runtime).LoginAsync("new", false, null, default));
        Assert.Equal("new", Assert.Single(manager.ListProfiles()).Name);
        Assert.Equal("fake-created", layout.ReadProfileAuth("new"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(layout.Paths.ApplicationDataDirectory, "login-staging")));
    }

    [Theory]
    [InlineData(false, "failed")]
    [InlineData(false, "cancelled")]
    [InlineData(false, "exception")]
    [InlineData(true, "failed")]
    [InlineData(true, "cancelled")]
    [InlineData(true, "exception")]
    public async Task UnsuccessfulLoginLeavesNoBrokenProfileAndKeepsExistingCredentials(bool reauthorize, string outcome)
    {
        using var layout = new TestLayout();
        layout.AddProfile("other", "fake-other");
        layout.WriteSharedAuth("fake-shared");
        layout.WriteSharedState("history");
        if (reauthorize) layout.AddProfile("target", "fake-old");
        var runtime = new Runtime((directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "auth.json"), "fake-partial");
            return outcome switch
            {
                "cancelled" => throw new OperationCanceledException(),
                "exception" => throw new IOException("fake failure"),
                _ => Task.FromResult(false),
            };
        });
        var service = new ProfileLoginService(layout.Paths, Manager(layout), runtime);
        if (outcome == "failed") Assert.False(await service.LoginAsync("target", reauthorize, null, default));
        else await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync("target", reauthorize, null, default));
        Assert.Equal("fake-other", layout.ReadProfileAuth("other"));
        Assert.Equal("fake-shared", layout.ReadSharedAuth());
        Assert.Equal("shared-db-history", layout.ReadSharedStateFile("state_5.sqlite"));
        if (reauthorize) Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
        else Assert.False(Directory.Exists(Path.Combine(layout.Paths.ProfilesDirectory, "target")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(layout.Paths.ApplicationDataDirectory, "login-staging")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReauthorizationPreservesMetadataOtherProfilesAndAllSharedHistory(bool active)
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        layout.AddProfile("other", "fake-other");
        layout.WriteSharedAuth("fake-shared");
        layout.WriteSharedState("history");
        layout.ActiveProfileStore.Write(active ? "target" : "other");
        var manager = Manager(layout);
        manager.EnsureMetadata();
        manager.RenameDisplayName("target", "My account");
        string metadata = File.ReadAllText(layout.Paths.ProfilesMetadataFile);
        var history = Directory.GetFiles(layout.Paths.SharedCodexDirectory, "*", SearchOption.AllDirectories)
            .Where(file => file != layout.Paths.SharedAuthFile).ToDictionary(file => file, File.ReadAllBytes);
        bool prepared = false;
        Assert.True(await new ProfileLoginService(layout.Paths, manager, Success()).LoginAsync("target", true, _ =>
        {
            Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
            Assert.Equal("fake-shared", layout.ReadSharedAuth());
            prepared = true;
            return Task.FromResult(true);
        }, default));
        Assert.Equal(active, prepared);
        Assert.Equal("fake-new-credential", layout.ReadProfileAuth("target"));
        Assert.Equal(active ? "fake-new-credential" : "fake-shared", layout.ReadSharedAuth());
        Assert.Equal("fake-other", layout.ReadProfileAuth("other"));
        Assert.Equal(metadata, File.ReadAllText(layout.Paths.ProfilesMetadataFile));
        foreach (var pair in history) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.False(File.Exists(Path.Combine(layout.Paths.ProfilesDirectory, "target", "login.log")));
    }

    [Fact]
    public async Task ActiveReauthorizationRequiresExplicitSafeTransition()
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        layout.WriteSharedAuth("fake-shared");
        layout.ActiveProfileStore.Write("target");
        var service = new ProfileLoginService(layout.Paths, Manager(layout), Success());
        Assert.False(await service.LoginAsync("target", true, null, default));
        Assert.False(await service.LoginAsync("target", true, _ => Task.FromResult(false), default));
        await Assert.ThrowsAsync<IOException>(() => service.LoginAsync("target", true, _ => throw new IOException("shutdown failed"), default));
        Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
        Assert.Equal("fake-shared", layout.ReadSharedAuth());
    }

    private sealed class FailSharedReplacement(string sharedFile) : IAtomicFileReplacer
    {
        public void ReplaceFromSource(string sourceFile, string destinationFile)
        {
            if (destinationFile == sharedFile) throw new IOException("fake replacement failure");
            new AtomicFileReplacer().ReplaceFromSource(sourceFile, destinationFile);
        }
    }

    [Fact]
    public async Task ActiveCommitFailureRollsBackBothCredentialFiles()
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        layout.WriteSharedAuth("fake-shared");
        layout.ActiveProfileStore.Write("target");
        var service = new ProfileLoginService(layout.Paths, Manager(layout), Success(), new FailSharedReplacement(layout.Paths.SharedAuthFile));
        await Assert.ThrowsAsync<IOException>(() => service.LoginAsync("target", true, _ => Task.FromResult(true), default));
        Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
        Assert.Equal("fake-shared", layout.ReadSharedAuth());
    }

    [Fact]
    public async Task CreationNeverOverwritesAnExistingProfile()
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileLoginService(layout.Paths, Manager(layout), Success())
            .LoginAsync("target", false, null, default));
        Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
    }

    [Fact]
    public async Task MissingOfficialRuntimeHasTypedActionableFailure()
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<LoginRuntimeUnavailableException>(() => new OfficialProfileLoginRuntime(() => null).LoginAsync(temp.Path, default));
        Assert.Null(CodexCliLocator.FindExecutable(temp.Path, null));
    }

    [Theory]
    [InlineData(null, AccountAvailability.Unknown)]
    [InlineData("invalid", AccountAvailability.Unknown)]
    [InlineData("[]", AccountAvailability.Unknown)]
    [InlineData("null", AccountAvailability.Unknown)]
    [InlineData("{\"error\":{\"message\":\"network failure\"}}", AccountAvailability.Unknown)]
    [InlineData("{\"error\":{\"code\":401}}", AccountAvailability.Unknown)]
    [InlineData("{\"result\":{\"account\":null,\"requiresOpenaiAuth\":true}}", AccountAvailability.SignInRequired)]
    [InlineData("{\"result\":{\"account\":null,\"requiresOpenaiAuth\":false}}", AccountAvailability.Unknown)]
    [InlineData("{\"result\":{\"account\":{\"type\":\"chatgpt\"}}}", AccountAvailability.Unknown)]
    [InlineData("{\"result\":{\"account\":{\"type\":\"apiKey\"}}}", AccountAvailability.Unknown)]
    public void StatusRequiresOfficialEvidence(string? response, AccountAvailability expected)
        => Assert.Equal(expected, AccountStatusChecker.Classify(response));

    [Fact]
    public async Task StatusInspectionIsIsolatedAndNeverChangesSavedCredentials()
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        layout.WriteSharedAuth("fake-shared");
        var checker = new AccountStatusChecker(layout.Paths, (directory, _) =>
        {
            Assert.False(directory.StartsWith(layout.Paths.ProfilesDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("fake-old", File.ReadAllText(Path.Combine(directory, "auth.json")));
            return Task.FromResult<string?>("{\"result\":{\"account\":null,\"requiresOpenaiAuth\":true}}");
        });
        Assert.Equal(AccountAvailability.SignInRequired, await checker.CheckAsync(Path.Combine(layout.Paths.ProfilesDirectory, "target"), default));
        Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
        Assert.Equal("fake-shared", layout.ReadSharedAuth());
        Assert.Empty(Directory.GetDirectories(Path.Combine(layout.Paths.ApplicationDataDirectory, "login-staging")));
    }

    [Fact]
    public async Task NetworkFailureNeverMarksAccountAsExpired()
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        var checker = new AccountStatusChecker(layout.Paths, (_, _) => throw new IOException("network unavailable"));
        Assert.Equal(AccountAvailability.Unknown, await checker.CheckAsync(Path.Combine(layout.Paths.ProfilesDirectory, "target"), default));
        Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
    }

    [Fact]
    public async Task CancellationAtSafeTransitionRetainsOldAuthorization()
    {
        using var layout = new TestLayout();
        layout.AddProfile("target", "fake-old");
        layout.WriteSharedAuth("fake-shared");
        layout.ActiveProfileStore.Write("target");
        using var cancellation = new CancellationTokenSource();
        var service = new ProfileLoginService(layout.Paths, Manager(layout), Success());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoginAsync("target", true, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        }, cancellation.Token));
        Assert.Equal("fake-old", layout.ReadProfileAuth("target"));
        Assert.Equal("fake-shared", layout.ReadSharedAuth());
    }
}
