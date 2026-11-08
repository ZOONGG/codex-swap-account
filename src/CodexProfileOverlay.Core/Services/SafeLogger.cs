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
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {Sanitize(exception.Message)}";
            if (exception is System.ComponentModel.Win32Exception w32)
            {
                line += $" [NativeErrorCode={w32.NativeErrorCode}]";
            }
            Exception? inner = exception.InnerException;
            int depth = 0;
            while (inner is not null && depth < 4)
            {
                line += $" <- {inner.GetType().Name}: {Sanitize(inner.Message)}";
                if (inner is System.ComponentModel.Win32Exception iw32)
                {
                    line += $" [NativeErrorCode={iw32.NativeErrorCode}]";
                }
                inner = inner.InnerException;
                depth++;
            }
            string? stack = exception.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                string firstFrames = string.Join(" | ", stack.Split('\n').Take(4).Select(s => Sanitize(s.Trim())));
                line += $" || {firstFrames}";
            }
        }

        lock (gate)
        {
            File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
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
