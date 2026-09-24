using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public interface ISupportApiClient
{
    Task<SupportMethodsResponse> GetMethodsAsync(CancellationToken cancellationToken);

    Task<SupportCheckoutResponse> CreateCheckoutAsync(
        string methodId,
        SupportCheckoutRequest request,
        CancellationToken cancellationToken);

    Task<SupportStatusResult> GetPaymentStatusAsync(
        string paymentIntentId,
        string statusToken,
        CancellationToken cancellationToken);
}

public sealed class SupportApiClient(HttpClient httpClient, Uri baseUri) : ISupportApiClient
{
    public const string ProjectSupportPath = "v1/projects/codex-swap-account/support";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri baseUri = baseUri;

    public async Task<SupportMethodsResponse> GetMethodsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri($"{ProjectSupportPath}/methods"));
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return await ReadAsync<SupportMethodsResponse>(response, cancellationToken);
    }

    public async Task<SupportCheckoutResponse> CreateCheckoutAsync(
        string methodId,
        SupportCheckoutRequest checkout,
        CancellationToken cancellationToken)
    {
        string route = methodId.ToLowerInvariant() switch
        {
            "telegram_stars" => $"{ProjectSupportPath}/telegram-stars/checkout",
            "direct_crypto" => $"{ProjectSupportPath}/direct-crypto/checkout",
            _ => throw new ArgumentOutOfRangeException(nameof(methodId)),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(route))
        {
            Content = methodId.Equals("direct_crypto", StringComparison.OrdinalIgnoreCase)
                ? JsonContent.Create(new
                {
                    usdAmount = checkout.Amount,
                    asset = checkout.Asset?.Asset,
                    network = checkout.Asset?.Network,
                    termsAccepted = checkout.TermsAccepted,
                    source = checkout.Source,
                    locale = checkout.Locale,
                }, options: JsonOptions)
                : JsonContent.Create(checkout, options: JsonOptions),
        };
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return await ReadAsync<SupportCheckoutResponse>(response, cancellationToken);
    }

    public async Task<SupportStatusResult> GetPaymentStatusAsync(
        string paymentIntentId,
        string statusToken,
        CancellationToken cancellationToken)
    {
        string path = $"{ProjectSupportPath}/payments/{Uri.EscapeDataString(paymentIntentId)}/status";
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", statusToken);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        PublicPaymentStatus payment = await ReadAsync<PublicPaymentStatus>(response, cancellationToken);
        TimeSpan retryAfter = ReadRetryAfter(response) ?? TimeSpan.FromSeconds(2);
        return new SupportStatusResult(payment, ClampRetryAfter(retryAfter));
    }

    private Uri BuildUri(string relativePath) => new(baseUri, relativePath);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }

        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                ?? throw new SupportApiException(
                    "INVALID_RESPONSE",
                    "The support service returned an empty response.",
                    response.StatusCode,
                    null);
        }
        catch (JsonException exception)
        {
            throw new SupportApiException(
                "INVALID_RESPONSE",
                "The support service returned an invalid response.",
                response.StatusCode,
                null,
                exception);
        }
    }

    private static async Task<SupportApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        SupportApiError? error = null;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            error = (await JsonSerializer.DeserializeAsync<SupportApiErrorEnvelope>(
                stream,
                JsonOptions,
                cancellationToken))?.Error;
        }
        catch (JsonException)
        {
        }

        TimeSpan? retryAfter = ReadRetryAfter(response);
        return new SupportApiException(
            string.IsNullOrWhiteSpace(error?.Code) ? "REQUEST_FAILED" : error.Code,
            string.IsNullOrWhiteSpace(error?.Message) ? "The support service is temporarily unavailable." : error.Message,
            response.StatusCode,
            retryAfter is null ? null : ClampRetryAfter(retryAfter.Value));
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }

    private static TimeSpan ClampRetryAfter(TimeSpan value) =>
        TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 1, 60));
}

public sealed class SupportApiException : Exception
{
    public SupportApiException(
        string code,
        string message,
        HttpStatusCode statusCode,
        TimeSpan? retryAfter,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public string Code { get; }

    public HttpStatusCode StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
