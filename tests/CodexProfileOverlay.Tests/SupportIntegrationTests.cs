using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class SupportIntegrationTests
{
    private static readonly Uri BaseUri = new("https://support.example.test/");

    [Fact]
    public async Task MethodsUseCodexSwapAccountProjectRoute()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, MethodsJson));
        using var http = new HttpClient(handler);

        SupportMethodsResponse response = await new SupportApiClient(http, BaseUri).GetMethodsAsync(default);

        Assert.Equal("/v1/projects/codex-swap-account/support/methods", handler.Requests.Single().Uri.AbsolutePath);
        Assert.DoesNotContain("moonrise", handler.Requests.Single().Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, response.Methods.Count);
        Assert.Equal(11, response.Methods.Single(method => method.Id == "direct_crypto").Assets?.Count);
    }

    [Fact]
    public async Task StarsCheckoutUsesProjectRouteAndExpectedPayload()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.Created,
            """{"paymentIntentId":"pi-1","provider":"telegram_stars","currency":"XTR","amount":50,"checkoutUrl":"https://t.me/$invoice","expiresAt":"2099-09-19T12:00:00Z","statusToken":"ephemeral"}"""));
        using var http = new HttpClient(handler);
        var request = new SupportCheckoutRequest(50, true, "app", "en");

        _ = await new SupportApiClient(http, BaseUri).CreateCheckoutAsync("telegram_stars", request, default);

        RecordedRequest recorded = handler.Requests.Single();
        Assert.Equal("/v1/projects/codex-swap-account/support/telegram-stars/checkout", recorded.Uri.AbsolutePath);
        Assert.Contains("\"amount\":50", recorded.Body, StringComparison.Ordinal);
        Assert.Contains("\"termsAccepted\":true", recorded.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("statusToken", recorded.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CryptoCheckoutUsesServerAssetAndUsdShape()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.Created,
            """{"paymentIntentId":"pi-2","provider":"direct_crypto","currency":"USD","amount":5,"checkoutUrl":null,"expiresAt":"2099-09-19T12:00:00Z","statusToken":"ephemeral","usdAmount":5,"asset":"BTC","network":"bitcoin","networkName":"Bitcoin","cryptoAmount":"0.0001","walletAddress":"bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq","paymentUri":"bitcoin:bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"}"""));
        using var http = new HttpClient(handler);
        var asset = new SupportAsset("BTC", "Bitcoin", "bitcoin", "Bitcoin", 8, "native", true);
        var request = new SupportCheckoutRequest(5, true, "app", "ru", asset);

        _ = await new SupportApiClient(http, BaseUri).CreateCheckoutAsync("direct_crypto", request, default);

        RecordedRequest recorded = handler.Requests.Single();
        Assert.Equal("/v1/projects/codex-swap-account/support/direct-crypto/checkout", recorded.Uri.AbsolutePath);
        Assert.Contains("\"usdAmount\":5", recorded.Body, StringComparison.Ordinal);
        Assert.Contains("\"asset\":\"BTC\"", recorded.Body, StringComparison.Ordinal);
        Assert.Contains("\"network\":\"bitcoin\"", recorded.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusTokenIsOnlySentAsBearerAuthorization()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            """{"paymentIntentId":"pi-1","provider":"telegram_stars","currency":"XTR","amount":50,"status":"paid","expiresAt":"2099-09-19T12:00:00Z","paidAt":"2099-09-19T11:45:00Z"}"""));
        using var http = new HttpClient(handler);

        _ = await new SupportApiClient(http, BaseUri).GetPaymentStatusAsync("pi-1", "memory-only-token", default);

        RecordedRequest recorded = handler.Requests.Single();
        Assert.Equal("Bearer", recorded.Authorization?.Scheme);
        Assert.Equal("memory-only-token", recorded.Authorization?.Parameter);
        Assert.DoesNotContain("memory-only-token", recorded.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("memory-only-token", recorded.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://support.example.test", true)]
    [InlineData("http://127.0.0.1:8787", true)]
    [InlineData("http://support.example.test", false)]
    [InlineData("file:///tmp/support", false)]
    [InlineData("https://user@example.test", false)]
    public void ConfigurationAllowsOnlyHttpsOrLoopbackHttp(string value, bool expected)
    {
        Assert.Equal(expected, SupportApiConfiguration.IsAllowedBaseUri(new Uri(value)));
    }

    [Fact]
    public void ReleaseBuildEmbedsProductionSupportEndpoint()
    {
        string? endpoint = typeof(SupportApiConfiguration).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == SupportApiConfiguration.AssemblyMetadataKey)?.Value;

        Assert.Equal("https://support-backend-production-8891.up.railway.app", endpoint);
    }

    [Theory]
    [InlineData("https://t.me/$invoice", true)]
    [InlineData("https://t.me/invoice", false)]
    [InlineData("http://t.me/$invoice", false)]
    [InlineData("https://evil.example/$invoice", false)]
    [InlineData("https://user@t.me/$invoice", false)]
    public void TelegramCheckoutUrlIsStrictlyValidated(string value, bool expected)
    {
        Assert.Equal(expected, CheckoutUrlValidator.TryValidateTelegramCheckout(value, out _));
    }

    [Theory]
    [InlineData("bitcoin:bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq", true)]
    [InlineData("ethereum:0x1111111111111111111111111111111111111111", true)]
    [InlineData("dogecoin:D9abcdefghijklmnopqrstuvwxy123456", true)]
    [InlineData("https://wallet.example/checkout", false)]
    [InlineData("javascript:alert(1)", false)]
    public void CryptoPaymentUriUsesAllowlistedSchemes(string value, bool expected)
    {
        Assert.Equal(expected, CheckoutUrlValidator.TryValidateDirectCrypto(value, out _));
    }

    [Theory]
    [InlineData("1.49", 1)]
    [InlineData("1.5", 2)]
    [InlineData("0", 1)]
    [InlineData("10001", 10000)]
    public void StarsAmountIsRoundedAndClamped(string value, int expected)
    {
        Assert.True(SupportAmountNormalizer.TryNormalize(value, 1, 10000, CultureInfo.InvariantCulture, out int amount));
        Assert.Equal(expected, amount);
    }

    [Fact]
    public async Task FlowUsesBackendMethodsAndTransitionsToPaid()
    {
        var api = new FakeApi(status: "paid");
        using var controller = new SupportFlowController(api);

        await controller.LoadAsync();
        Assert.Equal(SupportFlowState.ChoosingAmount, controller.State);
        Assert.Equal([50, 100, 250, 500], controller.Method?.Presets);

        await controller.BeginCheckoutAsync(50, "en");
        Assert.NotNull(controller.ActivePollingTask);
        await controller.ActivePollingTask!;

        Assert.Equal(SupportFlowState.Paid, controller.State);
        Assert.Equal(1, controller.StatusPollCount);
        Assert.Equal("pi-stars", controller.Checkout?.PaymentIntentId);
    }

    [Fact]
    public async Task DirectCryptoRequiresBackendAdvertisedAsset()
    {
        var api = new FakeApi(status: "paid");
        using var controller = new SupportFlowController(api);
        await controller.LoadAsync();
        Assert.True(controller.SelectMethod("direct_crypto"));

        await controller.BeginCheckoutAsync(5, "en");
        Assert.Equal(SupportFlowState.ChoosingAsset, controller.State);

        SupportAsset asset = Assert.Single(controller.Method!.Assets!);
        await controller.BeginDirectCryptoCheckoutAsync(asset);
        await controller.ActivePollingTask!;

        Assert.Equal(SupportFlowState.Paid, controller.State);
        Assert.Equal(asset, api.LastRequest?.Asset);
    }

    [Fact]
    public async Task FlowRejectsMismatchedPaymentSession()
    {
        var api = new FakeApi(status: "paid", statusPaymentIntentId: "other-payment");
        using var controller = new SupportFlowController(api);
        await controller.LoadAsync();

        await controller.BeginCheckoutAsync(50, "en");
        await controller.ActivePollingTask!;

        Assert.Equal(SupportFlowState.Expired, controller.State);
        Assert.Equal("INVALID_CHECKOUT_SESSION", controller.LastErrorCode);
        Assert.Null(controller.Checkout);
    }

    [Fact]
    public async Task FlowRejectsUnsafeTelegramCheckout()
    {
        var api = new FakeApi(status: "paid", checkoutUrl: "https://evil.example/$invoice");
        using var controller = new SupportFlowController(api);
        await controller.LoadAsync();

        await controller.BeginCheckoutAsync(50, "en");

        Assert.Equal(SupportFlowState.Failed, controller.State);
        Assert.Equal("UNSAFE_CHECKOUT_URL", controller.LastErrorCode);
        Assert.Null(controller.ActivePollingTask);
    }

    [Fact]
    public void QrServiceCreatesPngForApprovedPayloads()
    {
        byte[] stars = SupportQrCodeService.CreatePng("telegram_stars", "https://t.me/$invoice", 2);
        byte[] crypto = SupportQrCodeService.CreatePng("direct_crypto", "0x1111111111111111111111111111111111111111", 2);

        Assert.Equal([137, 80, 78, 71], stars.Take(4));
        Assert.Equal([137, 80, 78, 71], crypto.Take(4));
    }

    [Fact]
    public void QrServiceRejectsUnapprovedPayload()
    {
        Assert.Throws<InvalidDataException>(() =>
            SupportQrCodeService.CreatePng("telegram_stars", "https://evil.example/$invoice"));
    }

    [Fact]
    public void SupportUiKeepsPixelSafeRoundedGeometry()
    {
        string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CodexProfileOverlay", "SupportWindow.xaml"));

        Assert.Contains("UseLayoutRounding=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SnapsToDevicePixels=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Grid Margin=\"1\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ClipToBounds=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportUiOmitsForbiddenWalletActions()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CodexProfileOverlay", "SupportWindow.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CodexProfileOverlay", "SupportWindow.xaml"));

        Assert.DoesNotContain("Open in wallet", source + xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy exact", source + xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy amount", source + xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bot menu", source + xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsExposeRepositoryAndNativeSupportWindow()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CodexProfileOverlay", "SettingsWindow.cs"));

        Assert.Contains("https://github.com/ZOONGG/codex-swap-account", source, StringComparison.Ordinal);
        Assert.Contains("new SupportWindow(settings.Theme, localizer.Language)", source, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexProfileOverlay.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private const string MethodsJson = """
        {
          "methods": [
            {"id":"telegram_stars","enabled":true,"currency":"XTR","presets":[50,100,250,500],"custom":{"enabled":true,"min":1,"max":10000}},
            {"id":"direct_crypto","enabled":true,"currency":"USD","presets":[1,5,10,25],"custom":{"enabled":true,"min":1,"max":1000000},"assets":[
              {"asset":"BTC","displayName":"Bitcoin","network":"bitcoin","networkName":"Bitcoin","decimals":8,"type":"native","paymentUri":true},
              {"asset":"ETH","displayName":"Ethereum","network":"ethereum","networkName":"Ethereum","decimals":18,"type":"native","paymentUri":true},
              {"asset":"USDT","displayName":"Tether USD","network":"ethereum","networkName":"Ethereum (ERC-20)","decimals":6,"type":"token","paymentUri":true},
              {"asset":"USDC","displayName":"USD Coin","network":"ethereum","networkName":"Ethereum (ERC-20)","decimals":6,"type":"token","paymentUri":true},
              {"asset":"SOL","displayName":"Solana","network":"solana","networkName":"Solana","decimals":9,"type":"native","paymentUri":true},
              {"asset":"DOGE","displayName":"Dogecoin","network":"dogecoin","networkName":"Dogecoin","decimals":8,"type":"native","paymentUri":true},
              {"asset":"LTC","displayName":"Litecoin","network":"litecoin","networkName":"Litecoin","decimals":8,"type":"native","paymentUri":true},
              {"asset":"BNB","displayName":"BNB","network":"bsc","networkName":"BNB Smart Chain","decimals":18,"type":"native","paymentUri":true},
              {"asset":"LINK","displayName":"Chainlink","network":"ethereum","networkName":"Ethereum (ERC-20)","decimals":18,"type":"token","paymentUri":true},
              {"asset":"TRX","displayName":"TRON","network":"tron","networkName":"Tron","decimals":6,"type":"native","paymentUri":false},
              {"asset":"DAI","displayName":"Dai","network":"ethereum","networkName":"Ethereum (ERC-20)","decimals":18,"type":"token","paymentUri":true}
            ]}
          ]
        }
        """;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers.Authorization, body));
            return responder(request);
        }
    }

    private sealed record RecordedRequest(Uri Uri, AuthenticationHeaderValue? Authorization, string Body);

    private sealed class FakeApi(
        string status,
        string statusPaymentIntentId = "pi-stars",
        string checkoutUrl = "https://t.me/$invoice") : ISupportApiClient
    {
        private string activeProvider = "telegram_stars";
        private string activeCurrency = "XTR";
        private decimal activeAmount = 50;
        private string activePaymentIntentId = "pi-stars";

        public SupportCheckoutRequest? LastRequest { get; private set; }

        public Task<SupportMethodsResponse> GetMethodsAsync(CancellationToken cancellationToken)
        {
            var btc = new SupportAsset("BTC", "Bitcoin", "bitcoin", "Bitcoin", 8, "native", true);
            return Task.FromResult(new SupportMethodsResponse(
            [
                new SupportMethod("telegram_stars", true, "XTR", [50, 100, 250, 500], new SupportCustomAmount(true, 1, 10000)),
                new SupportMethod("direct_crypto", true, "USD", [1, 5, 10, 25], new SupportCustomAmount(true, 1, 1000000), [btc]),
            ]));
        }

        public Task<SupportCheckoutResponse> CreateCheckoutAsync(
            string methodId,
            SupportCheckoutRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            activeProvider = methodId;
            activeAmount = request.Amount;
            if (methodId == "direct_crypto")
            {
                activeCurrency = "BTC";
                activePaymentIntentId = "pi-crypto";
                return Task.FromResult(new SupportCheckoutResponse(
                    activePaymentIntentId,
                    methodId,
                    "USD",
                    request.Amount,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    "memory-only-token",
                    request.Amount,
                    "BTC",
                    "bitcoin",
                    "Bitcoin",
                    "0.0001",
                    "bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq",
                    "bitcoin:bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"));
            }

            activeCurrency = "XTR";
            activePaymentIntentId = "pi-stars";
            return Task.FromResult(new SupportCheckoutResponse(
                activePaymentIntentId,
                methodId,
                activeCurrency,
                request.Amount,
                checkoutUrl,
                DateTimeOffset.UtcNow.AddMinutes(15),
                "memory-only-token"));
        }

        public Task<SupportStatusResult> GetPaymentStatusAsync(
            string paymentIntentId,
            string statusToken,
            CancellationToken cancellationToken)
        {
            string responseId = statusPaymentIntentId == "pi-stars" ? activePaymentIntentId : statusPaymentIntentId;
            return Task.FromResult(new SupportStatusResult(
                new PublicPaymentStatus(
                    responseId,
                    activeProvider,
                    activeCurrency,
                    activeAmount,
                    status,
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    status == "paid" ? DateTimeOffset.UtcNow : null),
                TimeSpan.FromSeconds(1)));
        }
    }
}
