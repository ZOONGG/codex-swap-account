using CodexProfileOverlay.Core.Services;
using System.Runtime.Versioning;

namespace CodexProfileOverlay.Tests;

public sealed class WindowsDesktopAdapterTests
{
    [Theory]
    [InlineData("ChatGPT", "OpenAI.Codex_2p2nqsd0c76g0", true, false)]
    [InlineData("codex-command-runner", "OpenAI.Codex_2p2nqsd0c76g0", false, false)]
    [InlineData("crashpad-handler", "OpenAI.Codex_2p2nqsd0c76g0", false, false)]
    [InlineData("codex", "OpenAI.Codex_2p2nqsd0c76g0", false, true)]
    [InlineData("codex", null, false, true)]
    [InlineData("ChatGPT", "OpenAI.ChatGPT_2p2nqsd0c76g0", false, true)]
    [InlineData("helper", "OpenAI.ChatGPT_2p2nqsd0c76g0", false, true)]
    [InlineData("unrelated-child", null, false, true)]
    [InlineData("code", null, false, true)]
    [InlineData("windowsterminal", null, false, true)]
    [InlineData("powershell", null, false, true)]
    [InlineData("chrome", null, false, true)]
    [InlineData("CodexProfileOverlay", null, false, true)]
    public void NativeProtectionAllowsCodexHelpersButProtectsOtherApplications(string name, string? family, bool desktop, bool expected)
        => Assert.Equal(expected, WindowsDesktopProcessCatalog.IsProtectedProcess(name, family, desktop));

    [Fact]
    public void NativeProcessSnapshotIncludesOnlyObservationOfTestHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        var entries = new WindowsDesktopProcessCatalog().Snapshot();
        Assert.Contains(entries, e => e.Id == Environment.ProcessId);
    }

    [Fact]
    public void NativeHandleIdentifiesUnpackagedTestHostAsNonDesktop()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var handle = new WindowsDesktopProcessCatalog().Open(Environment.ProcessId);
        Assert.NotNull(handle);
        Assert.False(handle.IsOfficialDesktop);
        Assert.Null(handle.ExitedAt);
        Assert.True(handle.StartedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void NativeTrayAdapterRejectsUnrelatedGuidBeforeCallingShell()
    {
        var api = new WindowsTrayRegistration();
        Assert.Throws<ArgumentException>(() => api.IsPresent(Guid.Empty));
        Assert.Throws<ArgumentException>(() => api.Remove(Guid.NewGuid()));
    }

    [Fact]
    public void NativeLaunchLeasePreventsAnotherImageFromStarting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        string executable = Path.Combine(temp.Path, "launch-probe.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), executable);
        using var session = new CodexDesktopProcessSession(new EmptyProcessCatalog(), Environment.ProcessId);
        using var lease = WindowsDesktopStorage.PreventLaunch(new PreparedCodexLaunch("", [executable]), session);
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        Assert.Throws<System.ComponentModel.Win32Exception>(() => System.Diagnostics.Process.Start(start)?.Dispose());
    }

    [Fact]
    public void NativeRestartManagerDoesNotAttributeAnUnrelatedFileToCodex()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        string profile = Path.Combine(temp.Path, "Default");
        Directory.CreateDirectory(profile);
        using var file = File.Open(Path.Combine(profile, "Cookies"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        Assert.False(WindowsDesktopStorage.IsInUseByOfficialCodex([file.Name]));
    }

    private sealed class EmptyProcessCatalog : IDesktopProcessCatalog
    {
        public IReadOnlyList<DesktopProcessEntry> Snapshot() => [];
        public IDesktopProcessHandle? Open(int processId) => null;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void NativeLauncherRejectsUntrustedAumidBeforeActivation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launcher = new CodexAppLauncher();
        Assert.Throws<InvalidOperationException>(() => launcher.Launch(new PreparedCodexLaunch("OpenAI.ChatGPT_2p2nqsd0c76g0!App", [])));
    }
}
