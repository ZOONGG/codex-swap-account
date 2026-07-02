using System.Text;

namespace CodexProfileOverlay.Core.Services;

public sealed class SafeLogger
{
    private readonly string logFile;
    private readonly object gate = new();

    public SafeLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        logFile = Path.Combine(logDirectory, $"overlay-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        string sanitized = Sanitize(message);
        string line = $"{DateTimeOffset.Now:O} {level} {sanitized}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {Sanitize(exception.Message)}";
        }

        lock (gate)
        {
            File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private static string Sanitize(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
