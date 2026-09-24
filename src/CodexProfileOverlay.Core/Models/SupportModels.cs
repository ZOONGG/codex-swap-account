using System.Text.Json.Serialization;

namespace CodexProfileOverlay.Core.Models;

public sealed record SupportMethodsResponse(
    [property: JsonPropertyName("methods")] IReadOnlyList<SupportMethod> Methods);

public sealed record SupportMethod(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("presets")] IReadOnlyList<int> Presets,
    [property: JsonPropertyName("custom")] SupportCustomAmount Custom,
    [property: JsonPropertyName("assets")] IReadOnlyList<SupportAsset>? Assets = null);

public sealed record SupportAsset(
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("networkName")] string NetworkName,
    [property: JsonPropertyName("decimals")] int Decimals,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("paymentUri")] bool PaymentUri);

public sealed record SupportCustomAmount(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int? Max = null);

public sealed record SupportCheckoutRequest(
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("termsAccepted")] bool TermsAccepted,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonIgnore] SupportAsset? Asset = null);

public sealed record SupportCheckoutResponse(
    [property: JsonPropertyName("paymentIntentId")] string PaymentIntentId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("checkoutUrl")] string? CheckoutUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("statusToken")] string StatusToken,
    [property: JsonPropertyName("usdAmount"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? UsdAmount = null,
    [property: JsonPropertyName("asset")] string? Asset = null,
    [property: JsonPropertyName("network")] string? Network = null,
    [property: JsonPropertyName("networkName")] string? NetworkName = null,
    [property: JsonPropertyName("cryptoAmount")] string? CryptoAmount = null,
    [property: JsonPropertyName("walletAddress")] string? WalletAddress = null,
    [property: JsonPropertyName("paymentUri")] string? PaymentUri = null);

public sealed record SupportCheckoutView(
    string PaymentIntentId,
    string Provider,
    string Currency,
    decimal Amount,
    string CheckoutUrl,
    DateTimeOffset ExpiresAt,
    string? Asset = null,
    string? Network = null,
    string? NetworkName = null,
    string? CryptoAmount = null,
    string? WalletAddress = null,
    string? PaymentUri = null);

public sealed record PublicPaymentStatus(
    [property: JsonPropertyName("paymentIntentId")] string PaymentIntentId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Amount,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("paidAt")] DateTimeOffset? PaidAt);

public sealed record SupportApiErrorEnvelope(
    [property: JsonPropertyName("error")] SupportApiError Error);

public sealed record SupportApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record SupportStatusResult(PublicPaymentStatus Payment, TimeSpan RetryAfter);

public enum SupportFlowState
{
    Closed,
    LoadingMethods,
    ChoosingAmount,
    ChoosingAsset,
    CreatingCheckout,
    Waiting,
    ConnectivityIssue,
    Paid,
    Expired,
    Refunded,
    Failed,
}
