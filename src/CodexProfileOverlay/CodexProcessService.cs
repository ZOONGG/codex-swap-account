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
    public async Task<bool> LoginProfileAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        string fullProfileDirectory = Path.GetFullPath(profileDirectory);
        Directory.CreateDirectory(fullProfileDirectory);
        string executable = CodexCliLocator.FindExecutable()
            ?? throw new FileNotFoundException("codex executable was not found.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = fullProfileDirectory,
        };
        startInfo.ArgumentList.Add("login");
        startInfo.Environment["CODEX_HOME"] = fullProfileDirectory;

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start codex login.");
        Task standardOutputTask = DrainReaderAsync(process.StandardOutput);
        Task standardErrorTask = DrainReaderAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode == 0 && File.Exists(Path.Combine(fullProfileDirectory, "auth.json"));
    }

    private static async Task DrainReaderAsync(TextReader reader)
    {
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
        {
        }
    }

}
