using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class CodexCliLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "codex-cli-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindsFixedOfficialLocationBeforePathFallback()
    {
        string fixedPath = Add("Programs/OpenAI/Codex/bin");
        _ = Add("OpenAI/Codex/bin/9.0.0");
        Assert.Equal(fixedPath, CodexCliLocator.FindExecutable(root, null));
    }

    [Fact]
    public void FindsCurrentBundledExecutable()
    {
        string bundled = Add("OpenAI/Codex/bin");
        Assert.Equal(bundled, CodexCliLocator.FindExecutable(root, null));
    }

    [Fact]
    public void NumericVersionOutranksLexicalFolderNameAndTimestamp()
    {
        string older = Add("OpenAI/Codex/bin/0.9.0");
        string newer = Add("OpenAI/Codex/bin/0.100.0");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(1));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(newer, CodexCliLocator.FindExecutable(root, null));
    }

    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-beta.10", "1.0.0")]
    [InlineData("1.0.0-1", "1.0.0-beta")]
    [InlineData("1.0.0", "v2.0.0+build")]
    public void SemanticReleaseAndPrereleaseOrdering(string olderVersion, string newerVersion)
    {
        _ = Add("OpenAI/Codex/bin/" + olderVersion);
        string newer = Add("OpenAI/Codex/bin/" + newerVersion);
        Assert.Equal(newer, CodexCliLocator.FindExecutable(root, null));
    }

    [Fact]
    public void ExecutableMetadataOutranksDirectoryVersionLabel()
    {
        string misleading = Add("OpenAI/Codex/bin/99.0.0");
        File.Copy(typeof(CodexCliLocator).Assembly.Location, misleading, overwrite: true); // Metadata version is 1.0.0.
        string actualNewer = Add("OpenAI/Codex/bin/2.0.0");
        Assert.Equal(actualNewer, CodexCliLocator.FindExecutable(root, null));
    }

    [Fact]
    public void UnknownVersionUsesTimestampWithStablePathTieBreak()
    {
        string first = Add("OpenAI/Codex/bin/alpha");
        string second = Add("OpenAI/Codex/bin/zeta");
        DateTime timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, timestamp);
        File.SetLastWriteTimeUtc(second, timestamp.AddHours(1));
        Assert.Equal(second, CodexCliLocator.FindExecutable(root, null));
        File.SetLastWriteTimeUtc(second, timestamp);
        Assert.Equal(first, CodexCliLocator.FindExecutable(root, null));
    }

    [Fact]
    public void ExplicitPathFallbackStillWorksAndIgnoresUnlistedFiles()
    {
        _ = Add("untrusted");
        string fallback = Add("on-path");
        Assert.Null(CodexCliLocator.FindExecutable(root, null));
        Assert.Equal(fallback, CodexCliLocator.FindExecutable(root, Path.GetDirectoryName(fallback)));
    }

    [Fact]
    public void MissingLocalAppDataDoesNotSearchCurrentDirectory()
    {
        Assert.Null(CodexCliLocator.FindExecutable("", null));
        string fallback = Add("on-path");
        Assert.Equal(fallback, CodexCliLocator.FindExecutable("", Path.GetDirectoryName(fallback)));
    }

    [Fact]
    public void DoesNotSearchNestedUnrelatedExecutables()
    {
        _ = Add("OpenAI/Codex/bin/1.0.0/other");
        _ = Add("OtherApp/1.0.0");
        Assert.Null(CodexCliLocator.FindExecutable(root, null));
    }

    private string Add(string directory)
    {
        string fullDirectory = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(fullDirectory);
        string path = Path.Combine(fullDirectory, "codex.exe");
        File.WriteAllText(path, "disposable-test-executable");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
