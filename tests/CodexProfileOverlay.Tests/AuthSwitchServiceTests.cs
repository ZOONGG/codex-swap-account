using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class AuthSwitchServiceTests
{
    [Fact]
    public async Task SwitchAsync_CopiesCurrentAuthBackAndInstallsTarget()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-profile-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.ActiveProfileStore.Write("current");

        var service = temp.CreateSwitchService();

        var result = await service.SwitchAsync("target");

        Assert.Equal("target", result.TargetProfile);
        Assert.Equal("current", result.PreviousProfile);
        Assert.Equal("fresh-current-auth", temp.ReadProfileAuth("current"));
        Assert.Equal("target-auth", temp.ReadSharedAuth());
        Assert.Equal("target", temp.ActiveProfileStore.Read());
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public async Task SwitchAsync_WhenReplaceFails_RestoresPreviousSharedAuth()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-profile-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.ActiveProfileStore.Write("current");
        var service = temp.CreateSwitchService(new FailingReplacer());

        await Assert.ThrowsAsync<IOException>(() => service.SwitchAsync("target"));

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("target-auth", temp.ReadProfileAuth("target"));
        Assert.Equal("current", temp.ActiveProfileStore.Read());
    }

    [Fact]
    public async Task SwitchAsync_RejectsAlreadyActiveProfile()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "current-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.ActiveProfileStore.Write("current");
        var service = temp.CreateSwitchService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync("current"));

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("current", temp.ActiveProfileStore.Read());
    }

    [Fact]
    public async Task SwitchAsync_RejectsEmptyTargetAuth()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "current-auth");
        temp.AddProfile("target", string.Empty);
        temp.WriteSharedAuth("fresh-current-auth");
        temp.ActiveProfileStore.Write("current");
        var service = temp.CreateSwitchService();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SwitchAsync("target"));

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("current", temp.ActiveProfileStore.Read());
    }

    private sealed class FailingReplacer : IAtomicFileReplacer
    {
        public void ReplaceFromSource(string sourceFile, string destinationFile)
        {
            File.WriteAllText(destinationFile, "corrupted-before-failure");
            throw new IOException("Simulated replacement failure.");
        }
    }
}
