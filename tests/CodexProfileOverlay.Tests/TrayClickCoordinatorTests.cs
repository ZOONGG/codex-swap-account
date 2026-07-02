using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class TrayClickCoordinatorTests
{
    [Fact]
    public void LeftSingleClickFiresOnceAfterDoubleClickWindow()
    {
        var coordinator = new TrayClickCoordinator { DoubleClickWindow = TimeSpan.FromMilliseconds(250) };
        DateTimeOffset now = DateTimeOffset.UtcNow;

        coordinator.RegisterLeftClick(now);

        Assert.False(coordinator.TryConsumeDueSingleClick(now.AddMilliseconds(100)));
        Assert.True(coordinator.TryConsumeDueSingleClick(now.AddMilliseconds(260)));
        Assert.False(coordinator.TryConsumeDueSingleClick(now.AddMilliseconds(520)));
    }

    [Fact]
    public void DoubleClickSuppressesPendingSingleClick()
    {
        var coordinator = new TrayClickCoordinator();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        coordinator.RegisterLeftClick(now);
        Assert.True(coordinator.RegisterLeftDoubleClick());

        Assert.False(coordinator.TryConsumeDueSingleClick(now.AddSeconds(1)));
    }
}
