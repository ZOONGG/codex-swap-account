using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public sealed class SafeLogger
{
    private static readonly Regex SecretLikePattern = new(
        @"(?i)(authorization\s*:\s*bearer\s+)[^\s]+|((?:access|refresh|id)[_-]?token[""'\s:=]+)[^""'\s,}]+|([A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        var causes = new Queue<Exception>();
        if (exception is not null) causes.Enqueue(exception);
        for (int depth = 0; depth < 5 && causes.TryDequeue(out var cause); depth++)
        {
            // Exception messages and stack traces can contain paths, URLs and credential data.
            line += $" | {cause.GetType().Name} [HRESULT=0x{cause.HResult:X8}]";
            if (cause is Win32Exception native) line += $" [NativeErrorCode={native.NativeErrorCode}]";
            if (cause is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions.Take(5)) causes.Enqueue(inner);
            }
            else if (cause.InnerException is { } inner) causes.Enqueue(inner);
        }

        try
        {
            lock (gate)
            {
                File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("Could not write the overlay diagnostic log.");
        }
    }

    private static string Sanitize(string value)
    {
        string singleLine = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return SecretLikePattern.Replace(singleLine, match =>
        {
            if (match.Groups[1].Success)
            {
                return match.Groups[1].Value + "[redacted]";
            }

            if (match.Groups[2].Success)
            {
                return match.Groups[2].Value + "[redacted]";
            }

            return match.Groups[3].Success ? "[redacted-email]" : "[redacted-id]";
        });
    }
}
