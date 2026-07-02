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

    public async Task CloseCodexAsync(CancellationToken cancellationToken)
    {
        var processes = FindCodexProcesses().ToArray();
        foreach (Process process in processes)
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        logger.Info($"Requesting Codex close for process {process.Id}.");
                        _ = process.CloseMainWindow();
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    logger.Error("Failed to request Codex close.", exception);
                }
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && FindCodexProcesses().Any())
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        foreach (Process process in FindCodexProcesses())
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        logger.Info($"Terminating Codex process {process.Id} after graceful timeout.");
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    logger.Error("Failed to terminate Codex process.", exception);
                }
            }
        }
    }

    public void LaunchCodex()
    {
        if (TryLaunchStartMenuApp())
        {
            return;
        }

        logger.Info("Falling back to 'codex app'.");
        var startInfo = new ProcessStartInfo
        {
            FileName = "codex",
            Arguments = "app",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment.Remove("CODEX_HOME");
        _ = Process.Start(startInfo);
    }

    private bool TryLaunchStartMenuApp()
    {
        string? appId = ResolveStartAppId();
        if (!string.IsNullOrWhiteSpace(appId))
        {
            logger.Info("Launching Codex through Start menu AppUserModelID.");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{appId}",
                UseShellExecute = true,
            });
            return true;
        }

        string? shortcut = FindStartMenuShortcut();
        if (!string.IsNullOrWhiteSpace(shortcut))
        {
            logger.Info("Launching Codex through Start menu shortcut.");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = shortcut,
                UseShellExecute = true,
            });
            return true;
        }

        return false;
    }

    private static string? ResolveStartAppId()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"(Get-StartApps | Where-Object { $_.Name -eq 'Codex' -or $_.Name -like '*Codex*' } | Select-Object -First 1 -ExpandProperty AppID)\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            });

            string? output = process?.StandardOutput.ReadLine();
            process?.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindStartMenuShortcut()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        ];

        foreach (string root in roots.Where(Directory.Exists))
        {
            string? shortcut = Directory.EnumerateFiles(root, "*Codex*.lnk", SearchOption.AllDirectories)
                .OrderBy(static path => path.Length)
                .FirstOrDefault();
            if (shortcut is not null)
            {
                return shortcut;
            }
        }

        return null;
    }

    private static IEnumerable<Process> FindCodexProcesses()
    {
        return Process.GetProcesses()
            .Where(static process =>
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        return false;
                    }

                    return process.ProcessName.Contains("Codex", StringComparison.OrdinalIgnoreCase)
                        && !process.ProcessName.Contains("Overlay", StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
    }
}
