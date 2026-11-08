using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class SafeLoggerTests
{
    [Fact]
    public void Error_RedactsTokenLikeValuesAndEmails()
    {
        using var temp = new TempDirectory();
        var logger = new SafeLogger(temp.Path);

        string message = "Authorization" + ": Bearer " + "sk-secret-value " + "refresh" + "_token=very-secret " + "user" + "@example.com session 00000000-0000-0000-0000-000000000000";
        logger.Error(message, new AggregateException(
            new InvalidOperationException(@"C:\private\session\auth.json"),
            new System.ComponentModel.Win32Exception(5, "https://private.invalid/example")));

        string log = File.ReadAllText(Directory.EnumerateFiles(temp.Path).Single());
        Assert.DoesNotContain("sk-secret-value", log, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", log, StringComparison.Ordinal);
        Assert.Contains("[redacted-id]", log, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("NativeErrorCode=5", log, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.json", log, StringComparison.Ordinal);
        Assert.DoesNotContain("private.invalid", log, StringComparison.Ordinal);
    }
}
