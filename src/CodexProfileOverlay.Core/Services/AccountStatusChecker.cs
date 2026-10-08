using System.Text.Json;

namespace CodexProfileOverlay.Core.Services;

public enum AccountAvailability { Unknown, Available, SignInRequired }

public sealed class AccountStatusChecker(AppPaths paths,
    Func<string, CancellationToken, Task<string?>>? readAccount = null)
{
    public async Task<AccountAvailability> CheckAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        string staging = Path.Combine(paths.ApplicationDataDirectory, "login-staging", Guid.NewGuid().ToString("N"));
        try
        {
            // Read-only inspection: never rotate a live account's refresh token while Codex is running.
            Directory.CreateDirectory(staging);
            File.Copy(Path.Combine(profileDirectory, "auth.json"), Path.Combine(staging, "auth.json"));
            File.WriteAllText(Path.Combine(staging, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string? response = await (readAccount ?? CodexAppServerRateLimitsSource.ReadAccountAsync)(staging, timeout.Token).ConfigureAwait(false);
            return Classify(response);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        {
            return AccountAvailability.Unknown;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static AccountAvailability Classify(string? response)
    {
        if (response is null) return AccountAvailability.Unknown;
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("account", out var account)) return AccountAvailability.Unknown;
            if (account.ValueKind == JsonValueKind.Null)
                return result.TryGetProperty("requiresOpenaiAuth", out var required) && required.ValueKind == JsonValueKind.True
                    ? AccountAvailability.SignInRequired : AccountAvailability.Unknown;
            // A locally loaded account is not proof of valid server authorization.
            return AccountAvailability.Unknown;
        }
        catch (JsonException) { return AccountAvailability.Unknown; }
    }
}
