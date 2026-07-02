using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class SafeLoggerTests
{
    [Fact]
    public void Error_RedactsTokenLikeValuesAndEmails()
    {
        using var temp = new TempDirectory();
        var logger = new SafeLogger(temp.Path);

        string message = "Authorization" + ": Bearer " + "sk-secret-value " + "refresh" + "_token=very-secret " + "user" + "@example.com";
        logger.Error(message);

        string log = File.ReadAllText(Directory.EnumerateFiles(temp.Path).Single());
        Assert.DoesNotContain("sk-secret-value", log, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", log, StringComparison.Ordinal);
    }
}
