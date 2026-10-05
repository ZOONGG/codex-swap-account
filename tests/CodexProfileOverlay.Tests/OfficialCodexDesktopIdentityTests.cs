using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class OfficialCodexDesktopIdentityTests
{
    private const string Root = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1005.1.0_x64__2p2nqsd0c76g0";

    [Theory]
    [InlineData("app/ChatGPT.exe", true)]
    [InlineData("app/Codex.exe", true)]
    [InlineData("app/nested/ChatGPT.exe", true)]
    [InlineData("ChatGPT.exe", true)]
    [InlineData("app/bin/codex.exe", false)]
    [InlineData("app/resources/codex.exe", false)]
    [InlineData("app/CodexProfileOverlay.exe", false)]
    [InlineData("app/code.exe", false)]
    [InlineData("app/chrome.exe", false)]
    [InlineData("app/../ChatGPT.exe", false)]
    [InlineData("app/./ChatGPT.exe", false)]
    [InlineData("app//ChatGPT.exe", false)]
    [InlineData("app/ChatGPT.exe:stream", false)]
    public void PackageAndDesktopExecutableMustBothMatch(string relative, bool accepted)
    {
        Assert.Equal(accepted, OfficialCodexDesktopIdentity.IsOfficial(
            OfficialCodexDesktopIdentity.PackageFamily, Root, Root + "/" + relative));
    }

    [Theory]
    [InlineData("OpenAI.ChatGPT_2p2nqsd0c76g0")]
    [InlineData("OpenAI.Codex_fakepublisher")]
    [InlineData("fakeOpenAI.Codex_2p2nqsd0c76g0")]
    [InlineData(null)]
    public void OtherPackageIdentitiesAreRejected(string? family) => Assert.False(
        OfficialCodexDesktopIdentity.IsOfficial(family, Root, Root + @"\app\ChatGPT.exe"));

    [Theory]
    [InlineData(@"C:\Tools\ChatGPT.exe")]
    [InlineData(@"C:\Tools\Codex.exe")]
    [InlineData(@"C:\Tools\OpenAI.Codex_fake\app\Codex.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1005.1.0_x64__2p2nqsd0c76g0-fake\app\Codex.exe")]
    public void PackageIdentityDoesNotAuthorizeOutsideExecutable(string path) => Assert.False(
        OfficialCodexDesktopIdentity.IsOfficial(OfficialCodexDesktopIdentity.PackageFamily, Root, path));

    [Theory]
    [InlineData("1.0.0.0", "x64")]
    [InlineData("26.1005.20.0", "arm64")]
    [InlineData("100.20.3.9", "x86")]
    public void StrictFallbackAllowsVersionsAndNestedLayouts(string version, string architecture)
    {
        string root = @"D:\Program Files\WindowsApps";
        string path = root + $@"\OpenAI.Codex_{version}_{architecture}__2p2nqsd0c76g0\app\nested\ChatGPT.exe";
        Assert.True(OfficialCodexDesktopIdentity.IsOfficialPackagePath(path, root));
    }

    [Theory]
    [InlineData(@"C:\Fake\OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0\app\Codex.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_fake_x64__2p2nqsd0c76g0\app\Codex.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0\bin\codex.exe")]
    public void StrictFallbackRejectsLookalikes(string path) => Assert.False(
        OfficialCodexDesktopIdentity.IsOfficialPackagePath(path, @"C:\Program Files\WindowsApps"));

    [Fact]
    public void RegisteredManifestExecutableDistinguishesDesktopFromBundledCli()
    {
        Assert.True(OfficialCodexDesktopIdentity.IsOfficial(OfficialCodexDesktopIdentity.PackageFamily,
            Root, Root + @"\app\ChatGPT.exe", @"app\ChatGPT.exe"));
        Assert.False(OfficialCodexDesktopIdentity.IsOfficial(OfficialCodexDesktopIdentity.PackageFamily,
            Root, Root + @"\app\Codex.exe", @"app\ChatGPT.exe"));
    }
}
