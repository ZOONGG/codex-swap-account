using System.Globalization;
using System.Net;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class SupportFlowController : IDisposable
{
    private readonly ISupportApiClient apiClient;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Func<DateTimeOffset> utcNow;
    private CancellationTokenSource? flowCancellation;
    private SupportCheckoutSession? session;
    private string? lastAcceptedPaymentIntentId;
    private decimal? pendingDirectAmount;
    private string? pendingDirectLocale;
    private int generation;

    public SupportFlowController(
        ISupportApiClient apiClient,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.apiClient = apiClient;
        this.delayAsync = delayAsync ?? Task.Delay;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? StateChanged;

    public SupportFlowState State { get; private set; } = SupportFlowState.Closed;

    public IReadOnlyList<SupportMethod> Methods { get; private set; } = [];

    public SupportMethod? Method { get; private set; }

    public SupportCheckoutView? Checkout { get; private set; }

    public string? LastErrorCode { get; private set; }

    public Task? ActivePollingTask { get; private set; }

    public int StatusPollCount { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        StopActiveCheckout(clearCheckout: true);
        SetState(SupportFlowState.LoadingMethods);
        try
        {
            SupportMethodsResponse response = await apiClient.GetMethodsAsync(cancellationToken);
            Methods = response.Methods.Where(IsSupportedMethod).ToArray();
            Method = Methods.FirstOrDefault(item => item.Enabled &&
                string.Equals(item.Id, "telegram_stars", StringComparison.OrdinalIgnoreCase))
                ?? Methods.FirstOrDefault(item => item.Enabled);
            if (Methods.Count == 0)
            {
                LastErrorCode = "METHOD_UNAVAILABLE";
                SetState(SupportFlowState.Failed);
                return;
            }

            LastErrorCode = null;
            SetState(SupportFlowState.ChoosingAmount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(SupportFlowState.Closed);
        }
        catch (SupportApiException exception)
        {
            LastErrorCode = exception.Code;
            SetState(SupportFlowState.Failed);
        }
        catch (HttpRequestException)
        {
            LastErrorCode = "NETWORK_UNAVAILABLE";
            SetState(SupportFlowState.Failed);
        }
    }

    public bool SelectMethod(string methodId)
    {
        SupportMethod? selected = Methods.FirstOrDefault(item =>
            item.Enabled && string.Equals(item.Id, methodId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return false;
        }

        StopActiveCheckout(clearCheckout: true);
        Method = selected;
        LastErrorCode = null;
        SetState(SupportFlowState.ChoosingAmount);
        return true;
    }

    public bool TryNormalizeAmount(string? customText, CultureInfo culture, out decimal amount) =>
        TryResolveAmountSelection(customText, culture, out amount, out _);

    public bool TryResolveAmountSelection(
        string? customText,
        CultureInfo culture,
        out decimal amount,
        out int? selectedPreset)
    {
        amount = 0;
        selectedPreset = null;
        if (Method is not { } method)
        {
            return false;
        }

        if (string.Equals(method.Id, "direct_crypto", StringComparison.OrdinalIgnoreCase))
        {
            if (!decimal.TryParse(customText, NumberStyles.Number, culture, out amount) ||
                amount < method.Custom.Min || decimal.Round(amount, 2) != amount)
            {
                return false;
            }

            if (method.Custom.Max is { } directMaximum && amount > directMaximum)
            {
                amount = directMaximum;
            }

            foreach (int preset in method.Presets)
            {
                if (amount == preset)
                {
                    selectedPreset = preset;
                    break;
                }
            }

            return method.Custom.Enabled || selectedPreset is not null;
        }

        if (method.Custom.Max is not { } maximum ||
            !SupportAmountNormalizer.TryResolveSelection(
                customText,
                method.Custom.Min,
                maximum,
                method.Presets,
                culture,
                out int integerAmount,
                out selectedPreset))
        {
            return false;
        }

        amount = integerAmount;
        return method.Custom.Enabled || selectedPreset is not null;
    }

    public bool TryGetPresetInput(int preset, CultureInfo culture, out string input)
    {
        input = string.Empty;
        if (Method is not { } method || !method.Presets.Contains(preset))
        {
            return false;
        }

        input = Math.Clamp(preset, method.Custom.Min, method.Custom.Max ?? int.MaxValue).ToString(culture);
        return true;
    }

    public async Task BeginCheckoutAsync(
        decimal amount,
        string locale,
        CancellationToken cancellationToken = default,
        SupportAsset? asset = null)
    {
        if (Method is null)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        bool direct = string.Equals(Method.Id, "direct_crypto", StringComparison.OrdinalIgnoreCase);
        if (direct && Method.Custom.Max is { } directMaximum && amount > directMaximum)
        {
            amount = directMaximum;
        }

        if (amount < Method.Custom.Min ||
            decimal.Round(amount, direct ? 2 : 0) != amount ||
            (!direct && (Method.Custom.Max is not { } maximum || amount > maximum)))
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        StopActiveCheckout(clearCheckout: true);
        if (direct && asset is null)
        {
            pendingDirectAmount = amount;
            pendingDirectLocale = locale;
            SetState(SupportFlowState.ChoosingAsset);
            return;
        }

        if (asset is not null &&
            Method.Assets?.Any(item => item.Asset == asset.Asset && item.Network == asset.Network) != true)
        {
            throw new ArgumentOutOfRangeException(nameof(asset));
        }

        int activeGeneration = ++generation;
        var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        flowCancellation = activeCancellation;
        StatusPollCount = 0;
        SetState(SupportFlowState.CreatingCheckout);
        try
        {
            SupportCheckoutResponse response = await apiClient.CreateCheckoutAsync(
                Method.Id,
                new SupportCheckoutRequest(amount, true, "app", locale, asset),
                activeCancellation.Token);
            if (activeGeneration != generation || activeCancellation.IsCancellationRequested)
            {
                return;
            }

            decimal? checkoutAmount = direct ? response.UsdAmount : response.Amount;
            string? checkoutCurrency = direct ? response.Asset : response.Currency;
            string? checkoutPayload = direct ? response.PaymentUri ?? response.WalletAddress : response.CheckoutUrl;
            if (string.IsNullOrWhiteSpace(response.PaymentIntentId) ||
                string.Equals(response.PaymentIntentId, lastAcceptedPaymentIntentId, StringComparison.Ordinal) ||
                !string.Equals(response.Provider, Method.Id, StringComparison.OrdinalIgnoreCase) ||
                checkoutAmount != amount ||
                string.IsNullOrWhiteSpace(checkoutCurrency) ||
                string.IsNullOrWhiteSpace(checkoutPayload) ||
                (direct && (response.Asset != asset?.Asset ||
                    response.Network != asset?.Network ||
                    string.IsNullOrWhiteSpace(response.CryptoAmount) ||
                    string.IsNullOrWhiteSpace(response.WalletAddress))))
            {
                LastErrorCode = "INVALID_RESPONSE";
                SetState(SupportFlowState.Failed);
                return;
            }

            if ((!direct && !CheckoutUrlValidator.TryValidateCheckout(Method.Id, checkoutPayload, out _)) ||
                (direct && response.PaymentUri is not null &&
                    !CheckoutUrlValidator.TryValidateDirectCrypto(response.PaymentUri, out _)))
            {
                LastErrorCode = "UNSAFE_CHECKOUT_URL";
                SetState(SupportFlowState.Failed);
                return;
            }

            if (string.IsNullOrWhiteSpace(response.StatusToken))
            {
                LastErrorCode = "INVALID_RESPONSE";
                SetState(SupportFlowState.Failed);
                return;
            }

            if (response.ExpiresAt <= utcNow())
            {
                LastErrorCode = "PAYMENT_EXPIRED";
                SetState(SupportFlowState.Expired);
                return;
            }

            Checkout = new SupportCheckoutView(
                response.PaymentIntentId,
                response.Provider,
                checkoutCurrency,
                checkoutAmount!.Value,
                checkoutPayload,
                response.ExpiresAt,
                response.Asset,
                response.Network,
                response.NetworkName,
                response.CryptoAmount,
                response.WalletAddress,
                response.PaymentUri);
            lastAcceptedPaymentIntentId = response.PaymentIntentId;
            var activeSession = new SupportCheckoutSession(response.PaymentIntentId, response.StatusToken);
            session = activeSession;
            LastErrorCode = null;
            var pollingStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ActivePollingTask = PollAsync(activeSession, activeGeneration, pollingStart.Task, activeCancellation.Token);
            SetState(SupportFlowState.Waiting);
            pollingStart.SetResult();
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested)
        {
        }
        catch (SupportApiException exception)
        {
            LastErrorCode = exception.Code;
            SetState(SupportFlowState.Failed);
        }
        catch (HttpRequestException)
        {
            LastErrorCode = "NETWORK_UNAVAILABLE";
            SetState(SupportFlowState.Failed);
        }
    }

    public Task BeginDirectCryptoCheckoutAsync(
        SupportAsset asset,
        CancellationToken cancellationToken = default)
    {
        if (pendingDirectAmount is not { } amount || pendingDirectLocale is not { } locale)
        {
            throw new InvalidOperationException("Direct crypto amount has not been selected.");
        }

        pendingDirectAmount = null;
        pendingDirectLocale = null;
        return BeginCheckoutAsync(amount, locale, cancellationToken, asset);
    }

    public void ChangeAmount()
    {
        StopActiveCheckout(clearCheckout: true);
        LastErrorCode = null;
        pendingDirectAmount = null;
        pendingDirectLocale = null;
        SetState(SupportFlowState.ChoosingAmount);
    }

    public bool TryGetActiveCheckout(out SupportCheckoutView? checkout)
    {
        checkout = Checkout;
        if (checkout is null || State is not (SupportFlowState.Waiting or SupportFlowState.ConnectivityIssue))
        {
            checkout = null;
            return false;
        }

        if (checkout.ExpiresAt > utcNow())
        {
            return true;
        }

        StopActiveCheckout(clearCheckout: true);
        LastErrorCode = "PAYMENT_EXPIRED";
        SetState(SupportFlowState.Expired);
        checkout = null;
        return false;
    }

    public void Close()
    {
        StopActiveCheckout(clearCheckout: true);
        Method = null;
        Methods = [];
        pendingDirectAmount = null;
        pendingDirectLocale = null;
        LastErrorCode = null;
        SetState(SupportFlowState.Closed);
    }

    public void Dispose() => Close();

    private async Task PollAsync(
        SupportCheckoutSession activeSession,
        int activeGeneration,
        Task pollingStart,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = TimeSpan.Zero;
        int failureCount = 0;
        try
        {
            await pollingStart;
            while (IsCurrent(activeSession, activeGeneration, cancellationToken))
            {
                if (Checkout is not { } activeCheckout || activeCheckout.ExpiresAt <= utcNow())
                {
                    TransitionToExpired(activeSession);
                    return;
                }

                if (delay > TimeSpan.Zero)
                {
                    TimeSpan untilExpiry = activeCheckout.ExpiresAt - utcNow();
                    if (untilExpiry <= TimeSpan.Zero)
                    {
                        TransitionToExpired(activeSession);
                        return;
                    }

                    await delayAsync(delay < untilExpiry ? delay : untilExpiry, cancellationToken);
                }

                if (!IsCurrent(activeSession, activeGeneration, cancellationToken))
                {
                    return;
                }

                if (Checkout is not { } checkoutBeforePoll || checkoutBeforePoll.ExpiresAt <= utcNow())
                {
                    TransitionToExpired(activeSession);
                    return;
                }

                SupportStatusResult result;
                try
                {
                    StatusPollCount++;
                    result = await activeSession.GetStatusAsync(apiClient, cancellationToken);
                    failureCount = 0;
                }
                catch (SupportApiException exception) when (IsTransientStatusFailure(exception))
                {
                    if (!IsCurrent(activeSession, activeGeneration, cancellationToken))
                    {
                        return;
                    }

                    LastErrorCode = exception.Code;
                    SetState(SupportFlowState.ConnectivityIssue);
                    failureCount++;
                    delay = exception.RetryAfter ?? Backoff(failureCount);
                    continue;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    LastErrorCode = "NETWORK_TIMEOUT";
                    SetStateIfCurrent(SupportFlowState.ConnectivityIssue, activeSession, activeGeneration);
                    delay = Backoff(++failureCount);
                    continue;
                }
                catch (HttpRequestException)
                {
                    LastErrorCode = "NETWORK_UNAVAILABLE";
                    SetStateIfCurrent(SupportFlowState.ConnectivityIssue, activeSession, activeGeneration);
                    delay = Backoff(++failureCount);
                    continue;
                }
                catch (SupportApiException exception) when (
                    exception.Code == "PAYMENT_NOT_FOUND" || exception.StatusCode == HttpStatusCode.NotFound)
                {
                    if (!IsCurrent(activeSession, activeGeneration, cancellationToken))
                    {
                        return;
                    }

                    LastErrorCode = exception.Code;
                    TransitionToExpired(activeSession);
                    return;
                }
                catch (SupportApiException exception)
                {
                    if (!IsCurrent(activeSession, activeGeneration, cancellationToken))
                    {
                        return;
                    }

                    LastErrorCode = exception.Code;
                    CompleteSession(activeSession, clearCheckout: true);
                    SetState(SupportFlowState.Failed);
                    return;
                }

                if (!IsCurrent(activeSession, activeGeneration, cancellationToken))
                {
                    return;
                }

                if (!activeSession.Matches(result.Payment) ||
                    Checkout is not { } currentCheckout ||
                    result.Payment.Amount != currentCheckout.Amount ||
                    !string.Equals(result.Payment.Provider, currentCheckout.Provider, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(result.Payment.Currency, currentCheckout.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    LastErrorCode = "INVALID_CHECKOUT_SESSION";
                    TransitionToExpired(activeSession);
                    return;
                }

                LastErrorCode = null;
                delay = result.RetryAfter;
                switch (result.Payment.Status.Trim().ToLowerInvariant())
                {
                    case "pending":
                        SetState(SupportFlowState.Waiting);
                        break;
                    case "paid":
                        CompleteSession(activeSession, clearCheckout: false);
                        SetState(SupportFlowState.Paid);
                        return;
                    case "expired":
                        LastErrorCode = "PAYMENT_EXPIRED";
                        TransitionToExpired(activeSession);
                        return;
                    case "refunded":
                        CompleteSession(activeSession, clearCheckout: true);
                        SetState(SupportFlowState.Refunded);
                        return;
                    default:
                        LastErrorCode = "PAYMENT_FAILED";
                        CompleteSession(activeSession, clearCheckout: true);
                        SetState(SupportFlowState.Failed);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException)
        {
            if (!IsCurrent(activeSession, activeGeneration, cancellationToken))
            {
                return;
            }

            LastErrorCode = "POLLING_FAILED";
            CompleteSession(activeSession, clearCheckout: true);
            SetState(SupportFlowState.Failed);
        }
    }

    private void StopActiveCheckout(bool clearCheckout)
    {
        generation++;
        CancellationTokenSource? activeCancellation = flowCancellation;
        SupportCheckoutSession? activeSession = session;
        flowCancellation = null;
        session = null;
        ActivePollingTask = null;
        activeCancellation?.Cancel();
        activeSession?.Dispose();
        activeCancellation?.Dispose();
        if (clearCheckout)
        {
            Checkout = null;
        }
    }

    private bool IsCurrent(
        SupportCheckoutSession activeSession,
        int activeGeneration,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        activeGeneration == generation &&
        ReferenceEquals(session, activeSession);

    private void SetStateIfCurrent(
        SupportFlowState state,
        SupportCheckoutSession activeSession,
        int activeGeneration)
    {
        if (activeGeneration == generation && ReferenceEquals(session, activeSession))
        {
            SetState(state);
        }
    }

    private void CompleteSession(SupportCheckoutSession activeSession, bool clearCheckout)
    {
        if (!ReferenceEquals(session, activeSession))
        {
            return;
        }

        session = null;
        activeSession.Dispose();
        flowCancellation?.Dispose();
        flowCancellation = null;
        if (clearCheckout)
        {
            Checkout = null;
        }
    }

    private void TransitionToExpired(SupportCheckoutSession activeSession)
    {
        CompleteSession(activeSession, clearCheckout: true);
        SetState(SupportFlowState.Expired);
    }

    private static TimeSpan Backoff(int failureCount) =>
        TimeSpan.FromSeconds(Math.Min(15, 2 * Math.Pow(2, Math.Min(failureCount - 1, 3))));

    private static bool IsTransientStatusFailure(SupportApiException exception) =>
        exception.Code == "INVALID_RESPONSE" ||
        exception.StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsSupportedMethod(SupportMethod method) =>
        method.Id.ToLowerInvariant() switch
        {
            "telegram_stars" => string.Equals(method.Currency, "XTR", StringComparison.OrdinalIgnoreCase) &&
                method.Custom.Max is { } maximum && maximum >= method.Custom.Min,
            "direct_crypto" => string.Equals(method.Currency, "USD", StringComparison.OrdinalIgnoreCase) &&
                method.Assets?.Count > 0,
            _ => false,
        } && method.Custom.Min > 0;

    private void SetState(SupportFlowState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SupportCheckoutSession(string paymentIntentId, string statusToken) : IDisposable
    {
        private string? statusToken = statusToken;

        public Task<SupportStatusResult> GetStatusAsync(
            ISupportApiClient client,
            CancellationToken cancellationToken)
        {
            string token = statusToken ?? throw new ObjectDisposedException(nameof(SupportCheckoutSession));
            return client.GetPaymentStatusAsync(paymentIntentId, token, cancellationToken);
        }

        public bool Matches(PublicPaymentStatus payment) =>
            string.Equals(payment.PaymentIntentId, paymentIntentId, StringComparison.Ordinal);

        public void Dispose() => statusToken = null;
    }
}
