using System.Reflection;
using System.Text.Json;

namespace CodexProfileOverlay.Core.Services;

public sealed record SupportApiConfiguration(Uri? BaseUri, string? Error)
{
    public const string EnvironmentVariable = "CODEX_SWAP_ACCOUNT_SUPPORT_API_BASE_URL";
    public const string AppContextKey = "CodexSwapAccount.SupportApiBaseUrl";
    public const string AssemblyMetadataKey = "CodexSwapSupportApiBaseUrl";
    public const string ConfigurationFileName = "codex-swap-account-support.json";

    public bool IsConfigured => BaseUri is not null;

    public static SupportApiConfiguration Load()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = AppContext.GetData(AppContextKey) as string;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);
            try
            {
                if (File.Exists(filePath))
                {
                    if (new FileInfo(filePath).Length > 4096)
                    {
                        return new(null, "The support configuration file is too large.");
                    }

                    value = JsonSerializer.Deserialize<SupportApiFileConfiguration>(
                        File.ReadAllText(filePath),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                        })?.BaseUrl;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(null, "The support configuration file is invalid.");
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = typeof(SupportApiConfiguration).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(
                    attribute.Key,
                    AssemblyMetadataKey,
                    StringComparison.Ordinal))?.Value;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return new(null, "The support service is not configured in this build.");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return new(null, "The support service address is invalid.");
        }

        if (!IsAllowedBaseUri(uri))
        {
            return new(null, "The support service must use HTTPS (loopback HTTP is allowed for development).");
        }

        return new(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute), null);
    }

    public static bool IsAllowedBaseUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
    }

    private sealed record SupportApiFileConfiguration(string BaseUrl);
}
