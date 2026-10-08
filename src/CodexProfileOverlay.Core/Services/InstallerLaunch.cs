using System.Diagnostics;

namespace CodexProfileOverlay.Core.Services;

public static class InstallerLaunch
{
    // Fixed switches only: never interpolate release metadata or profile paths.
    public static ProcessStartInfo Create(string verifiedPath)
    {
        if (!Path.IsPathFullyQualified(verifiedPath) || !Path.GetFileName(verifiedPath).StartsWith("codex-update-", StringComparison.Ordinal)
            || Path.GetExtension(verifiedPath) != ".exe"
            || !string.Equals(Path.GetDirectoryName(verifiedPath), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected installer location.");
        var info = new ProcessStartInfo(verifiedPath) { UseShellExecute = false };
        info.ArgumentList.Add("/UPDATE=1");
        info.ArgumentList.Add("/SILENT"); // Inno shows installation progress; no background/VERYSILENT mode.
        info.ArgumentList.Add("/NORESTART");
        return info;
    }
}
