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
        Assert.True(Directory.Exists(result.BackupPath));
        Assert.Equal(
            new[] { "manifest.json", "previous-active-profile.txt", "previous-auth.json" },
            Directory.EnumerateFiles(result.BackupPath).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task SwitchAsync_PreservesSharedCodexStateForEveryProfile()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-profile-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.WriteSharedState("current-chat-state");
        temp.WriteProfileState("target", "legacy-target-chat-state");
        temp.ActiveProfileStore.Write("current");

        var service = temp.CreateSwitchService();

        await service.SwitchAsync("target");

        Assert.Equal("current-chat-state", temp.ReadSharedStateFile(".codex-global-state.json"));
        Assert.Equal("shared-index-current-chat-state", temp.ReadSharedStateFile("session_index.jsonl"));
        Assert.Equal("shared-db-current-chat-state", temp.ReadSharedStateFile("state_5.sqlite"));
    }

    [Fact]
    public async Task SwitchAsync_WhenReplaceFails_RestoresPreviousSharedAuth()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-profile-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.ActiveProfileStore.Write("current");
        var service = temp.CreateSwitchService(new FailSecondReplacement());

        await Assert.ThrowsAsync<IOException>(() => service.SwitchAsync("target"));

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("fresh-current-auth", temp.ReadProfileAuth("current"));
        Assert.Equal("target-auth", temp.ReadProfileAuth("target"));
        Assert.Equal("current", temp.ActiveProfileStore.Read());
    }

    [Fact]
    public async Task SwitchAsync_RollbackDoesNotModifySharedWorkspaceData()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-profile-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.WriteSharedState("keep-me");
        string attachments = Path.Combine(temp.Paths.SharedCodexDirectory, "attachments", "item.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(attachments)!);
        File.WriteAllText(attachments, "attachment");
        temp.ActiveProfileStore.Write("current");

        await Assert.ThrowsAsync<IOException>(() => temp.CreateSwitchService(new FailSecondReplacement()).SwitchAsync("target"));

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("current", temp.ActiveProfileStore.Read());
        Assert.Equal("shared-session-keep-me", File.ReadAllText(Path.Combine(temp.Paths.SharedCodexDirectory, "sessions", "2026", "07", "04", "rollout.jsonl")));
        Assert.Equal("shared-db-keep-me", temp.ReadSharedStateFile("state_5.sqlite"));
        Assert.Equal("attachment", File.ReadAllText(attachments));
    }

    [Fact]
    public async Task Rollback_RestoresAuthAndActiveProfileMetadataOnly()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-current-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.WriteSharedState("untouched");
        temp.ActiveProfileStore.Write("current");
        AuthSwitchService service = temp.CreateSwitchService();
        AuthSwitchResult result = await service.SwitchAsync("target");

        service.Rollback(result);

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("current", temp.ActiveProfileStore.Read());
        Assert.Equal("shared-index-untouched", temp.ReadSharedStateFile("session_index.jsonl"));
        Assert.Equal("shared-db-untouched", temp.ReadSharedStateFile("state_5.sqlite"));
        Assert.Equal("shared-session-untouched", File.ReadAllText(Path.Combine(temp.Paths.SharedCodexDirectory, "sessions", "2026", "07", "04", "rollout.jsonl")));
    }

    [Fact]
    public async Task MultipleFakeSwitches_CreateOnlySubMegabyteMinimalBackups()
    {
        using var temp = new TestLayout();
        temp.AddProfile("one", "one-auth");
        temp.AddProfile("two", "two-auth");
        temp.WriteSharedAuth("one-live-auth");
        temp.WriteSharedState("never-backed-up");
        temp.ActiveProfileStore.Write("one");
        AuthSwitchService service = temp.CreateSwitchService();

        AuthSwitchResult first = await service.SwitchAsync("two");
        AuthSwitchResult second = await service.SwitchAsync("one");

        foreach (string backup in new[] { first.BackupPath!, second.BackupPath! })
        {
            Assert.True(Directory.EnumerateFiles(backup).Sum(file => new FileInfo(file).Length) < 1024 * 1024);
            Assert.Equal(
                new[] { "manifest.json", "previous-active-profile.txt", "previous-auth.json" },
                Directory.EnumerateFiles(backup).Select(Path.GetFileName).Order().ToArray());
        }
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

    [Fact]
    public async Task SwitchAsync_WhenBackupCannotBeFinalized_RestoresPreviousAuthorization()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestLayout();
        temp.AddProfile("current", "old-current-auth");
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("fresh-current-auth");
        temp.ActiveProfileStore.Write("current");
        using var blocker = new BlockBackupFinalization(temp.Paths.BackupDirectory);

        var error = await Assert.ThrowsAsync<AggregateException>(() => temp.CreateSwitchService(blocker).SwitchAsync("target"));
        Assert.Collection(error.InnerExceptions, item => Assert.IsAssignableFrom<IOException>(item),
            item => Assert.IsAssignableFrom<IOException>(item));

        Assert.Equal("fresh-current-auth", temp.ReadSharedAuth());
        Assert.Equal("current", temp.ActiveProfileStore.Read());
        Assert.Equal("target-auth", temp.ReadProfileAuth("target"));
    }

    private sealed class BlockBackupFinalization(string root) : IAtomicFileReplacer, IDisposable
    {
        private FileStream? manifest;

        public void ReplaceFromSource(string sourceFile, string destinationFile)
        {
            new AtomicFileReplacer().ReplaceFromSource(sourceFile, destinationFile);
            string transaction = Directory.GetDirectories(root, "txn-*").Single();
            manifest ??= File.Open(Path.Combine(transaction, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public void Dispose() => manifest?.Dispose();
    }

    private sealed class FailSecondReplacement : IAtomicFileReplacer
    {
        private readonly AtomicFileReplacer inner = new();
        private int calls;

        public void ReplaceFromSource(string sourceFile, string destinationFile)
        {
            calls++;
            if (calls == 2)
            {
                File.WriteAllText(destinationFile, "corrupted-before-failure");
                throw new IOException("Simulated replacement failure.");
            }
            inner.ReplaceFromSource(sourceFile, destinationFile);
        }
    }
}
