using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class CodexCliStatusUsageProvider : IUsageProvider
{
    public const string SourceIdentifier = "codex-cli-status";

    private readonly ICodexRateLimitsSource source;

    public CodexCliStatusUsageProvider()
        : this(new CodexAppServerRateLimitsSource())
    {
    }

    public CodexCliStatusUsageProvider(ICodexRateLimitsSource source)
    {
        this.source = source;
    }

    public UsageProviderCapability Capability => source.IsAvailable ? UsageProviderCapability.Supported : UsageProviderCapability.Unavailable;

    public async Task<UsageSnapshot?> GetUsageAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        if (!source.IsAvailable)
        {
            return null;
        }

        CodexRateLimitsCapture? capture = await source.CaptureRateLimitsAsync(profileDirectory, cancellationToken).ConfigureAwait(false);
        return capture is null
            ? null
            : CodexAppServerRateLimitsParser.Parse(capture.StatusOutput, capture.CapturedAt, capture.CodexCliVersion);
    }
}

public sealed class CodexAppServerRateLimitsSource : ICodexRateLimitsSource
{
    public bool IsAvailable => CodexCliLocator.FindExecutable() is not null;

    public async Task<CodexRateLimitsCapture?> CaptureRateLimitsAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        string? executable = CodexCliLocator.FindExecutable();
        if (executable is null)
        {
            return null;
        }

        string fullProfileDirectory = Path.GetFullPath(profileDirectory);
        Directory.CreateDirectory(fullProfileDirectory);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CodexRateLimitsCapture? result = await CaptureOnceAsync(executable, fullProfileDirectory, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            if (attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async Task<CodexRateLimitsCapture?> CaptureOnceAsync(
        string executable,
        string fullProfileDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executable, fullProfileDirectory),
        };

        process.Start();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await WriteMessageAsync(
                process.StandardInput,
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-profile-overlay\",\"version\":\"1\"}}}",
                cancellationToken).ConfigureAwait(false);
            if (await ReadResponseAsync(process.StandardOutput, 1, cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }

            await WriteMessageAsync(process.StandardInput, "{\"method\":\"initialized\"}", cancellationToken).ConfigureAwait(false);
            await WriteMessageAsync(process.StandardInput, "{\"id\":2,\"method\":\"account/rateLimits/read\"}", cancellationToken).ConfigureAwait(false);
            string? response = await ReadResponseAsync(process.StandardOutput, 2, cancellationToken).ConfigureAwait(false);
            return response is null
                ? null
                : new CodexRateLimitsCapture(response, DateTimeOffset.UtcNow, null);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string profileDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        startInfo.Environment["CODEX_HOME"] = profileDirectory;
        return startInfo;
    }

    private static async Task WriteMessageAsync(StreamWriter writer, string message, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt32(out int value)
                    && value == expectedId)
                {
                    return document.RootElement.TryGetProperty("result", out _) ? line : null;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }
}

public sealed record CodexRateLimitsCapture(string StatusOutput, DateTimeOffset CapturedAt, string? CodexCliVersion);

public interface ICodexRateLimitsSource
{
    bool IsAvailable { get; }

    Task<CodexRateLimitsCapture?> CaptureRateLimitsAsync(string profileDirectory, CancellationToken cancellationToken);
}

public static class CodexAppServerRateLimitsParser
{
    public static UsageSnapshot? Parse(
        string appServerOutput,
        DateTimeOffset capturedAt,
        string? codexCliVersion)
    {
        if (string.IsNullOrWhiteSpace(appServerOutput))
        {
            return null;
        }

        foreach (string line in appServerOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("\"rateLimits", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument? document = TryParseJsonDocument(line);
            if (document is null
                || !TryGetProperty(document.RootElement, "result", out JsonElement result)
                || !TryGetRateLimitsElement(result, out JsonElement rateLimits))
            {
                continue;
            }

            var windows = new List<UsageLimitWindow>();
            if (TryGetProperty(rateLimits, "primary", out JsonElement primary)
                && TryBuildAppServerWindow(primary, "5h", TimeSpan.FromHours(5), out UsageLimitWindow primaryWindow))
            {
                windows.Add(primaryWindow);
            }

            if (TryGetProperty(rateLimits, "secondary", out JsonElement secondary)
                && TryBuildAppServerWindow(secondary, "Weekly", TimeSpan.FromDays(7), out UsageLimitWindow secondaryWindow))
            {
                windows.Add(secondaryWindow);
            }

            if (windows.Count == 0)
            {
                continue;
            }

            string? reachedType = TryGetString(rateLimits, "rateLimitReachedType");
            bool isExhausted = windows.Any(window => window.RemainingPercent == 0)
                || !string.IsNullOrWhiteSpace(reachedType);

            return new UsageSnapshot
            {
                Windows = windows,
                CapturedAt = capturedAt.ToUniversalTime(),
                Source = CodexCliStatusUsageProvider.SourceIdentifier,
                CodexCliVersion = string.IsNullOrWhiteSpace(codexCliVersion) ? null : codexCliVersion.Trim(),
                IsExhausted = isExhausted,
            };
        }

        return null;
    }

    private static JsonDocument? TryParseJsonDocument(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetRateLimitsElement(JsonElement result, out JsonElement rateLimits)
    {
        if (TryGetProperty(result, "rateLimitsByLimitId", out JsonElement byLimitId)
            && byLimitId.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(byLimitId, "codex", out rateLimits))
            {
                return true;
            }

            foreach (JsonProperty property in byLimitId.EnumerateObject())
            {
                rateLimits = property.Value;
                return true;
            }
        }

        if (TryGetProperty(result, "rateLimits", out rateLimits))
        {
            return true;
        }

        rateLimits = default;
        return false;
    }

    private static bool TryBuildAppServerWindow(
        JsonElement value,
        string fallbackName,
        TimeSpan? fallbackDuration,
        out UsageLimitWindow window)
    {
        if (!TryGetNumber(value, "usedPercent", out double usedPercent))
        {
            window = null!;
            return false;
        }

        usedPercent = Math.Clamp(usedPercent, 0, 100);
        TimeSpan? duration = TryGetNumber(value, "windowDurationMins", out double durationMinutes)
            ? TimeSpan.FromMinutes(durationMinutes)
            : fallbackDuration;

        window = new UsageLimitWindow
        {
            Name = FormatAppServerWindowName(duration, fallbackName),
            Duration = duration,
            RemainingPercent = (int)Math.Round(100 - usedPercent, MidpointRounding.AwayFromZero),
            ResetAt = TryGetUnixSeconds(value, "resetsAt", out long resetsAt)
                ? DateTimeOffset.FromUnixTimeSeconds(resetsAt)
                : null,
        };
        return true;
    }

    private static string FormatAppServerWindowName(TimeSpan? duration, string fallbackName)
    {
        if (duration is null)
        {
            return fallbackName;
        }

        if (duration.Value == TimeSpan.FromHours(5))
        {
            return "5h";
        }

        if (duration.Value == TimeSpan.FromDays(7))
        {
            return "Weekly";
        }

        if (duration.Value.TotalHours >= 1 && duration.Value.TotalHours % 1 == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.Value.TotalHours:0}h");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{duration.Value.TotalMinutes:0}m");
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property))
        {
            return true;
        }

        property = default;
        return false;
    }

    private static string? TryGetString(JsonElement value, string name)
    {
        if (!TryGetProperty(value, name, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool TryGetNumber(JsonElement value, string name, out double number)
    {
        if (TryGetProperty(value, name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryGetUnixSeconds(JsonElement value, string name, out long unixSeconds)
    {
        if (TryGetProperty(value, name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out unixSeconds))
        {
            return true;
        }

        unixSeconds = 0;
        return false;
    }

}
