using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class CodexShutdownCoordinatorTests
{
    [Fact]
    public async Task GracefulExitWaitsForQuietPeriodAndThenCleansUp()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, elapsed => elapsed < TimeSpan.FromMilliseconds(200) ? [10] : []);
        TimeSpan? cleanedAt = null;
        await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(1), false,
            () => cleanedAt = clock.Elapsed, () => Assert.Fail("Unexpected warning"));
        Assert.NotEmpty(session.Closed);
        Assert.Empty(session.Killed);
        Assert.True(cleanedAt >= TimeSpan.FromMilliseconds(950));
    }

    [Fact]
    public async Task OneEmptySnapshotDoesNotCompleteShutdownAndRespawnIsGracefullyClosed()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, elapsed => elapsed.TotalMilliseconds switch
        {
            < 100 => [10],
            < 300 => [],
            < 500 => [20, 21],
            _ => [],
        });
        bool cleaned = false;
        await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(2), false,
            () => cleaned = true, () => Assert.Fail("Unexpected warning"));
        Assert.True(cleaned);
        Assert.Contains(20, session.Closed);
        Assert.Contains(21, session.Closed);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1250));
    }

    [Fact]
    public async Task SurvivingChildPreventsAuthReplacementAndTrayCleanup()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("unchanged-auth");
        var clock = new FakeClock();
        var session = new FakeSession(clock, elapsed => elapsed.TotalMilliseconds < 200 ? [10, 11] : [11]);
        bool cleaned = false;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(1), false,
                () => cleaned = true, () => { });
            await temp.CreateSwitchService().SwitchAsync("target");
        });
        Assert.False(cleaned);
        Assert.Empty(session.Killed);
        Assert.Equal("unchanged-auth", temp.ReadSharedAuth());
    }

    [Fact]
    public async Task ForceIsUsedOnlyAfterGracefulTimeoutAndWhenEnabled()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => [10]);
        session.KillExits = true;
        await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(1), true, () => { }, () => { });
        Assert.NotEmpty(session.Killed);
        Assert.True(session.FirstKillAt >= TimeSpan.FromMilliseconds(1750));
    }

    [Fact]
    public async Task ForceFailureDoesNotCleanUp()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => [10]);
        bool cleaned = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CodexShutdownCoordinator(clock).CloseAsync(
            session, TimeSpan.FromSeconds(1), true, () => cleaned = true, () => { }));
        Assert.False(cleaned);
        Assert.NotEmpty(session.Killed);
    }

    [Fact]
    public async Task CancellationDuringQuietPeriodDoesNotCleanUp()
    {
        using var cts = new CancellationTokenSource();
        var clock = new FakeClock { OnDelay = () => cts.Cancel() };
        var session = new FakeSession(clock, _ => []);
        bool cleaned = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexShutdownCoordinator(clock).CloseAsync(
            session, TimeSpan.FromSeconds(1), false, () => cleaned = true, () => { }, cts.Token));
        Assert.False(cleaned);
    }

    [Fact]
    public async Task CancellationOnFinalSnapshotDoesNotCleanUp()
    {
        using var cts = new CancellationTokenSource();
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => []);
        session.OnSnapshot = count => { if (count == 10) cts.Cancel(); };
        bool cleaned = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexShutdownCoordinator(clock).CloseAsync(
            session, TimeSpan.FromSeconds(1), false, () => cleaned = true, () => { }, cts.Token));
        Assert.False(cleaned);
    }

    [Fact]
    public async Task NoObservedDesktopMeansNoTrayCleanup()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => []) { ObservedDesktop = false };
        bool cleaned = false;
        await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(1), false, () => cleaned = true, () => { });
        Assert.False(cleaned);
    }

    [Fact]
    public async Task RespawnOnFinalCheckPreventsCleanupAndAuthReplacement()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => []);
        session.OnSnapshot = count => { if (count == 10) session.Respawn = true; };
        bool cleaned = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CodexShutdownCoordinator(clock).CloseAsync(
            session, TimeSpan.FromSeconds(1), false, () => cleaned = true, () => { }));
        Assert.False(cleaned);
    }

    [Fact]
    public async Task RespawnDuringCleanupStillPreventsAuthReplacement()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CodexShutdownCoordinator(clock).CloseAsync(
            session, TimeSpan.FromSeconds(1), false, () => session.Respawn = true, () => { }));
    }

    [Fact]
    public async Task TrayFailureWarnsAndAllowsSwitchOfTemporaryAuthorization()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => []);
        bool warned = false;
        await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(1), false,
            () => throw new InvalidOperationException("Tray unavailable"), () => warned = true);
        await temp.CreateSwitchService().SwitchAsync("target");
        Assert.True(warned);
        Assert.Equal("target-auth", temp.ReadSharedAuth());
    }

    [Fact]
    public async Task TrayFailureAndWarningSinkFailureAreBothNonFatal()
    {
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => []);
        await new CodexShutdownCoordinator(clock).CloseAsync(session, TimeSpan.FromSeconds(1), false,
            () => throw new IOException("Tray unavailable"), () => throw new IOException("Log unavailable"));
    }

    [Fact]
    public async Task CancelledBeforeShutdownDoesNotObserveOrActOnProcesses()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var clock = new FakeClock();
        var session = new FakeSession(clock, _ => [10]);
        session.OnSnapshot = _ => Assert.Fail("Cancelled shutdown must not enumerate");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexShutdownCoordinator(clock).CloseAsync(
            session, TimeSpan.FromSeconds(1), true, () => Assert.Fail("No cleanup"), () => { }, cts.Token));
        Assert.Empty(session.Closed);
        Assert.Empty(session.Killed);
    }

    private sealed class FakeClock : IShutdownClock
    {
        public TimeSpan Elapsed { get; private set; }
        public Action? OnDelay { get; init; }
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Elapsed += duration;
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSession(FakeClock clock, Func<TimeSpan, int[]> snapshot) : ICodexProcessSession
    {
        private int snapshotCount;
        public bool ObservedDesktop { get; set; } = true;
        public bool KillExits { get; set; }
        public bool Respawn { get; set; }
        public TimeSpan? FirstKillAt { get; private set; }
        public Action<int>? OnSnapshot { get; set; }
        public List<int> Closed { get; } = [];
        public List<int> Killed { get; } = [];
        public IReadOnlyList<int> FindRunning()
        {
            OnSnapshot?.Invoke(++snapshotCount);
            return Respawn ? [20] : KillExits && Killed.Count > 0 ? [] : snapshot(clock.Elapsed);
        }
        public void RequestClose(int processId) => Closed.Add(processId);
        public void Terminate(int processId) { FirstKillAt ??= clock.Elapsed; Killed.Add(processId); }
    }
}
