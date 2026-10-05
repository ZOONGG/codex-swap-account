using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed record PreparedCodexLaunch(string AppId);

[SupportedOSPlatform("windows")]
internal sealed class CodexAppLauncher
{
    public async Task<PreparedCodexLaunch> PrepareAsync(CancellationToken token)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        // Query registered package/manifest, not Start Menu display names. No user input
        // enters the command. Validate every returned field again in managed code.
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; @(Get-AppxPackage -Name OpenAI.Codex | "
            + "Where-Object { $_.PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' } | ForEach-Object { "
            + "$p=$_; $m=Get-AppxPackageManifest -Package $p; $m.Package.Applications.Application | ForEach-Object { "
            + "[pscustomobject]@{ Family=$p.PackageFamilyName; Root=$p.InstallLocation; "
            + "AppId=($p.PackageFamilyName+'!'+$_.Id); Executable=$_.Executable } } }) | ConvertTo-Json -Compress");
        using Process query = Process.Start(start) ?? throw new InvalidOperationException("Could not query Codex registration.");
        Task<string> stdout = query.StandardOutput.ReadToEndAsync();
        Task<string> stderr = query.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await query.WaitForExitAsync(timeout.Token).ConfigureAwait(true); }
        catch (OperationCanceledException)
        {
            if (!query.HasExited) query.Kill(entireProcessTree: true); // Only our own query.
            await query.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Timed out resolving Codex registration. Codex has not been closed.");
        }
        string output = await stdout.ConfigureAwait(true);
        _ = await stderr.ConfigureAwait(true); // Never log package-query output or private paths.
        if (query.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("The official Codex package could not be resolved. Codex has not been closed.");
        using var json = JsonDocument.Parse(output);
        var registrations = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
        var ids = new List<string>();
        foreach (var registration in registrations)
        {
            string? family = registration.GetProperty("Family").GetString();
            string? root = registration.GetProperty("Root").GetString();
            string? executable = registration.GetProperty("Executable").GetString();
            string? id = CodexLaunchIdentity.Validate(registration.GetProperty("AppId").GetString());
            if (root is null || executable is null || id is null || Path.IsPathRooted(executable)) continue;
            string path = Path.Combine(root, executable);
            if (OfficialCodexDesktopIdentity.IsOfficial(family, root, path) && File.Exists(path)) ids.Add(id);
        }
        string appId = CodexLaunchIdentity.Select(ids)
            ?? throw new InvalidOperationException("No usable official Codex launch entry was found. Codex has not been closed.");
        token.ThrowIfCancellationRequested();
        var manager = CreateManager(); // Preflight activation service before shutdown.
        _ = Marshal.ReleaseComObject(manager);
        return new PreparedCodexLaunch(appId);
    }

    public void Launch(PreparedCodexLaunch target)
    {
        string appId = CodexLaunchIdentity.Validate(target.AppId)
            ?? throw new InvalidOperationException("Untrusted Codex launch entry.");
        var manager = CreateManager();
        try
        {
            int result = manager.ActivateApplication(appId, null, 2, out uint processId); // AO_NOERRORUI
            if (result < 0 || processId == 0)
                throw new InvalidOperationException($"Windows did not accept Codex activation (HRESULT 0x{result:X8}).");
        }
        finally { _ = Marshal.ReleaseComObject(manager); }
    }

    private static IApplicationActivationManager CreateManager()
    {
        Guid classId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        Guid interfaceId = typeof(IApplicationActivationManager).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 4, ref interfaceId, out var manager));
        return manager;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager manager);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId,
            IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, out uint processId);
    }
}
