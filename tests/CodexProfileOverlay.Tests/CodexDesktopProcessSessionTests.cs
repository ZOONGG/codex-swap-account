using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class CodexDesktopProcessSessionTests
{
    [Fact]
    public void IncludesVerifiedRootsAndDescendantsButNotIndependentAppsOrSwitcherTree()
    {
        var catalog = new FakeCatalog();
        catalog.Add(10, 1, official: true);
        catalog.Add(11, 10);
        catalog.Add(12, 11);
        catalog.Add(20, 1, name: "ChatGPT.exe");
        catalog.Add(21, 1, name: "codex.exe");
        catalog.Add(99, 10, name: "CodexProfileOverlay.exe");
        catalog.Add(100, 99);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        Assert.Equal(new[] { 10, 11, 12 }, session.FindRunning().Order().ToArray());
        Assert.True(session.ObservedDesktop);
    }

    [Fact]
    public void RetainsChildrenAfterParentExitAndFindsPreviouslyUnseenOrphan()
    {
        var catalog = new FakeCatalog();
        var root = catalog.Add(10, 1, official: true);
        var child = catalog.Add(11, 10);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        _ = session.FindRunning();
        root.Exit = root.Start.AddSeconds(5);
        catalog.Remove(10);
        catalog.Add(12, 10, started: root.Start.AddSeconds(2));
        Assert.Equal(new[] { 11, 12 }, session.FindRunning().Order().ToArray());
        session.Terminate(11);
        Assert.Equal(1, child.Terminations);
    }

    [Fact]
    public void FindsNewChildrenAndIndependentVerifiedRespawn()
    {
        var catalog = new FakeCatalog();
        catalog.Add(10, 1, official: true);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        _ = session.FindRunning();
        catalog.Add(11, 10);
        catalog.Add(20, 1, official: true);
        Assert.Equal(new[] { 10, 11, 20 }, session.FindRunning().Order().ToArray());
    }

    [Fact]
    public void ProtectedCliAndChatgptChildrenAreWaitedForButNeverClosedOrKilled()
    {
        var catalog = new FakeCatalog();
        catalog.Add(10, 1, official: true);
        var cli = catalog.Add(11, 10, name: "codex.exe", protect: true);
        var chatgpt = catalog.Add(12, 10, name: "ChatGPT.exe", protect: true);
        var terminalChild = catalog.Add(13, 11);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        Assert.Equal(4, session.FindRunning().Count);
        session.RequestClose(11); session.Terminate(11);
        session.RequestClose(12); session.Terminate(12);
        session.RequestClose(13); session.Terminate(13);
        Assert.Equal(0, cli.Closes + cli.Terminations + chatgpt.Closes + chatgpt.Terminations
            + terminalChild.Closes + terminalChild.Terminations);
    }

    [Fact]
    public void RejectsChildCreatedAfterRetainedParentExited()
    {
        var catalog = new FakeCatalog();
        var root = catalog.Add(10, 1, official: true);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        _ = session.FindRunning();
        root.Exit = root.Start.AddSeconds(2);
        catalog.Remove(10);
        catalog.Add(11, 10, started: root.Start.AddSeconds(3));
        Assert.Empty(session.FindRunning());
    }

    [Fact]
    public void ReusedParentPidFailsSafelyWithoutTargetingUnrelatedChild()
    {
        var catalog = new FakeCatalog();
        var root = catalog.Add(10, 1, official: true);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        _ = session.FindRunning();
        root.Exit = root.Start.AddSeconds(2);
        catalog.Add(10, 1, name: "unrelated.exe", started: root.Start.AddSeconds(3));
        var child = catalog.Add(11, 10, started: root.Start.AddSeconds(4));
        Assert.Throws<InvalidOperationException>(() => session.FindRunning());
        Assert.Equal(0, child.Terminations);
    }

    [Fact]
    public void ReusedChildPidIsNotConfusedWithExitedChild()
    {
        var catalog = new FakeCatalog();
        catalog.Add(10, 1, official: true);
        var child = catalog.Add(11, 10);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        _ = session.FindRunning();
        child.Exit = child.Start.AddSeconds(2);
        var replacement = catalog.Add(11, 10, started: child.Start.AddSeconds(3));
        Assert.Equal(new[] { 10, 11 }, session.FindRunning().Order().ToArray());
        session.Terminate(11);
        Assert.Equal(0, child.Terminations);
        Assert.Equal(1, replacement.Terminations);
    }

    [Fact]
    public void ExitedChildInSnapshotDoesNotLoopAndStillAnchorsSurvivingGrandchild()
    {
        var catalog = new FakeCatalog();
        catalog.Add(10, 1, official: true);
        var child = catalog.Add(11, 10);
        child.Exit = child.Start.AddSeconds(5);
        catalog.Add(12, 11);
        using var session = new CodexDesktopProcessSession(catalog, 99);
        Assert.Equal(new[] { 10, 12 }, session.FindRunning().Order().ToArray());
        Assert.Equal(new[] { 10, 12 }, session.FindRunning().Order().ToArray());
    }

    [Fact]
    public void DisposeReleasesAllRetainedHandlesIncludingExitedParents()
    {
        var catalog = new FakeCatalog();
        var root = catalog.Add(10, 1, official: true);
        var session = new CodexDesktopProcessSession(catalog, 99);
        _ = session.FindRunning();
        session.Dispose();
        Assert.Equal(1, root.Disposals);
    }

    private sealed class State(int id, DateTimeOffset start, bool official, bool protect)
    {
        public int Id = id;
        public DateTimeOffset Start = start;
        public DateTimeOffset? Exit;
        public bool Official = official, Protected = protect;
        public int Closes, Terminations, Disposals;
    }

    private sealed class FakeHandle(State state) : IDesktopProcessHandle
    {
        public int Id => state.Id;
        public DateTimeOffset StartedAt => state.Start;
        public DateTimeOffset? ExitedAt => state.Exit;
        public bool IsOfficialDesktop => state.Official;
        public bool IsProtected => state.Protected;
        public void RequestClose() => state.Closes++;
        public void Terminate() => state.Terminations++;
        public void Dispose() => state.Disposals++;
    }

    private sealed class FakeCatalog : IDesktopProcessCatalog
    {
        private readonly Dictionary<int, (DesktopProcessEntry Entry, State State)> entries = new();
        public State Add(int id, int parent, bool official = false, string? name = null, bool protect = false, DateTimeOffset? started = null)
        {
            var state = new State(id, started ?? DateTimeOffset.UnixEpoch.AddSeconds(id), official, protect);
            entries[id] = (new DesktopProcessEntry(id, parent, name ?? (official ? "ChatGPT.exe" : "helper.exe")), state);
            return state;
        }
        public void Remove(int id) => entries.Remove(id);
        public IReadOnlyList<DesktopProcessEntry> Snapshot() => entries.Values.Select(e => e.Entry).ToArray();
        public IDesktopProcessHandle? Open(int processId) => entries.TryGetValue(processId, out var entry) ? new FakeHandle(entry.State) : null;
    }
}
