namespace CodexProfileOverlay.Core.Services;

public interface IStartupRegistrationService
{
    bool IsEnabled();

    void SetEnabled(bool enabled, string executablePath);
}
