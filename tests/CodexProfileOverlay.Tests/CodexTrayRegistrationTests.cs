using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class CodexTrayRegistrationTests
{
    [Fact]
    public void RemovesOnlyKnownProductionGuidAndLeavesUnrelatedIcon()
    {
        var api = new FakeTray();
        Guid other = Guid.NewGuid();
        api.Present.UnionWith([CodexTrayRegistration.ProductionGuid, other]);
        Assert.Equal(TrayCleanupResult.Removed, CodexTrayRegistration.RemoveStale(api));
        Assert.Equal([new Guid("e5768d8b-6936-4f45-b1ad-4c5fb414cb35")], api.Deleted);
        Assert.Contains(other, api.Present);
        Assert.All(api.Queried, guid => Assert.Equal(CodexTrayRegistration.ProductionGuid, guid));
    }

    [Fact]
    public void MissingIconIsNoOp()
    {
        var api = new FakeTray();
        Assert.Equal(TrayCleanupResult.NotPresent, CodexTrayRegistration.RemoveStale(api));
        Assert.Empty(api.Deleted);
    }

    [Fact]
    public void FailedDeletionReturnsFailureWithoutThrowing()
    {
        var api = new FakeTray { FailRemoval = true };
        api.Present.Add(CodexTrayRegistration.ProductionGuid);
        Assert.Equal(TrayCleanupResult.Failed, CodexTrayRegistration.RemoveStale(api));
    }

    private sealed class FakeTray : ITrayRegistrationApi
    {
        public HashSet<Guid> Present { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public List<Guid> Queried { get; } = [];
        public bool FailRemoval { get; init; }
        public bool IsPresent(Guid guid) { Queried.Add(guid); return Present.Contains(guid); }
        public bool Remove(Guid guid) { Deleted.Add(guid); return !FailRemoval && Present.Remove(guid); }
    }
}
