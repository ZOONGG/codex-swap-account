namespace CodexProfileOverlay.Core.Services;

public interface ITrayRegistrationApi
{
    bool IsPresent(Guid guid);
    bool Remove(Guid guid);
}

public enum TrayCleanupResult { NotPresent, Removed, Failed }

public static class CodexTrayRegistration
{
    // Production Electron tray GUID verified in the HYFOAM compatibility fix.
    public static readonly Guid ProductionGuid = new("e5768d8b-6936-4f45-b1ad-4c5fb414cb35");

    public static TrayCleanupResult RemoveStale(ITrayRegistrationApi api)
    {
        if (!api.IsPresent(ProductionGuid)) return TrayCleanupResult.NotPresent;
        _ = api.Remove(ProductionGuid);
        return api.IsPresent(ProductionGuid) ? TrayCleanupResult.Failed : TrayCleanupResult.Removed;
    }
}
