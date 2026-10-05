using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class CodexLaunchIdentityTests
{
    [Theory]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0!App", true)]
    [InlineData("  OpenAI.Codex_2p2nqsd0c76g0!ChatGPT \r\n", true)]
    [InlineData("OpenAI.ChatGPT_2p2nqsd0c76g0!App", false)]
    [InlineData("FakeOpenAI.Codex_2p2nqsd0c76g0!App", false)]
    [InlineData("OpenAI.Codex_fake!App", false)]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0!", false)]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0!App;start", false)]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0!App\nother", false)]
    [InlineData("Codex", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyOfficialAumids(string? value, bool accepted) =>
        Assert.Equal(accepted, CodexLaunchIdentity.Validate(value) is not null);

    [Fact]
    public async Task LaunchIsResolvedBeforeShutdown()
    {
        var events = new List<string>();
        string? target = await CodexSwitchPreparation.PrepareAndCloseAsync(true,
            () => { events.Add("prepare"); return Task.FromResult("verified"); },
            () => { events.Add("shutdown"); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("verified", target);
        Assert.Equal(["prepare", "shutdown"], events);
    }

    [Fact]
    public async Task FailedPreparationLeavesDesktopAndTemporaryAuthorizationUnchanged()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("unchanged-auth");
        bool closed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await CodexSwitchPreparation.PrepareAndCloseAsync<string>(true,
                () => throw new InvalidOperationException("No registration"),
                () => { closed = true; return Task.CompletedTask; }, CancellationToken.None);
            await temp.CreateSwitchService().SwitchAsync("target");
        });
        Assert.False(closed);
        Assert.Equal("unchanged-auth", temp.ReadSharedAuth());
    }

    [Fact]
    public async Task DisabledRelaunchDoesNotRequireInstalledLaunchTarget()
    {
        bool closed = false;
        string? target = await CodexSwitchPreparation.PrepareAndCloseAsync<string>(false,
            () => throw new InvalidOperationException(),
            () => { closed = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(closed);
        Assert.Null(target);
    }

    [Fact]
    public async Task CancelledPreparationDoesNotCloseDesktop()
    {
        using var cts = new CancellationTokenSource();
        bool closed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexSwitchPreparation.PrepareAndCloseAsync(true,
            () => { cts.Cancel(); return Task.FromResult("verified"); },
            () => { closed = true; return Task.CompletedTask; }, cts.Token));
        Assert.False(closed);
    }
}
