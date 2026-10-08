using System.Diagnostics;
using System.IO;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class CodexProcessService
{
    private readonly SafeLogger logger;

    public CodexProcessService(SafeLogger logger)
    {
        this.logger = logger;
    }

    private readonly CodexAppLauncher launcher = new();

    public Task<PreparedCodexLaunch> PrepareLaunchAsync(CancellationToken token) => launcher.PrepareAsync(token);

    public async Task<CodexDesktopProcessSession> CloseCodexAsync(int gracefulTimeoutSeconds, bool allowForceClose, CancellationToken token)
    {
        var session = new CodexDesktopProcessSession(new WindowsDesktopProcessCatalog(), Environment.ProcessId);
        try
        {
            await new CodexShutdownCoordinator().CloseAsync(session,
                TimeSpan.FromSeconds(Math.Clamp(gracefulTimeoutSeconds, 1, 60)), allowForceClose,
                () =>
                {
                    token.ThrowIfCancellationRequested();
                    var result = CodexTrayRegistration.RemoveStale(new WindowsTrayRegistration());
                    if (result == TrayCleanupResult.Failed) LogTrayWarning();
                    else logger.Info($"Codex tray cleanup: {result}.");
                }, LogTrayWarning, token).ConfigureAwait(false);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    private void LogTrayWarning()
    {
        const string message = "Warning: stale Codex tray registration could not be removed; continuing account switch.";
        try { logger.Info(message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Trace.WriteLine(message); }
    }

    public void LaunchCodex(PreparedCodexLaunch target)
    {
        launcher.Launch(target);
        logger.Info("Windows accepted activation of the verified Codex package.");
    }
}
