using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Button = System.Windows.Controls.Button;

namespace CodexProfileOverlay;

public partial class SupportWindow : Window
{
    private Action? embeddedClose;
    private FrameworkElement? embeddedPanel;
    private readonly AppTheme theme;
    private readonly bool russian;
    private HttpClient? httpClient;
    private SupportFlowController? flow;
    private CancellationTokenSource? dialogCancellation;
    private bool showingMethods = true;
    private string? renderedQrPayload;
    private int copyToastGeneration;

    public SupportWindow(AppTheme theme, LanguagePreference language)
    {
        this.theme = theme;
        russian = LocalizationCatalog.Resolve(language) == LanguagePreference.Russian;
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowCaptionThemeService.Apply(this, theme);
        ApplyLocalizedText();
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    internal FrameworkElement CreateEmbeddedPanel(Action close, double availableHeight)
    {
        embeddedClose = close;
        BodyScroll.MaxHeight = Math.Max(100, availableHeight - 160);
        var panel = (FrameworkElement)Content;
        Content = null;
        panel.Resources = Resources;
        panel.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "StrongTextBrush");
        panel.PreviewKeyDown += OnPreviewKeyDown;
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(panel, KeyboardNavigationMode.Cycle);
        panel.Loaded += OnLoaded;
        embeddedPanel = panel;
        return panel;
    }

    internal void UpdateEmbeddedHeight(double availableHeight) => BodyScroll.MaxHeight = Math.Max(100, availableHeight - 160);

    internal void DisposeEmbedded()
    {
        if (embeddedPanel is { } panel)
        {
            panel.Loaded -= OnLoaded;
            panel.PreviewKeyDown -= OnPreviewKeyDown;
        }
        OnClosed(this, EventArgs.Empty);
        Close();
    }

    private void RequestClose()
    {
        if (embeddedClose is { } close) close();
        else Close();
    }

    private CultureInfo AmountCulture => russian
        ? CultureInfo.GetCultureInfo("ru-RU")
        : CultureInfo.GetCultureInfo("en-US");

    private string T(string ru, string en) => russian ? ru : en;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (embeddedClose is null) ClampToWorkArea();
        CloseButton.Focus();
        dialogCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = dialogCancellation.Token;
        SupportApiConfiguration configuration = SupportApiConfiguration.Load();
        if (!configuration.IsConfigured)
        {
            ShowTerminal(
                T("Поддержка пока недоступна", "Support is not available yet"),
                T("Сервис поддержки не настроен в этой сборке.", configuration.Error ?? "The support service is not configured in this build."),
                canCreateNew: false);
            return;
        }

        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        flow = new SupportFlowController(new SupportApiClient(httpClient, configuration.BaseUri!));
        flow.StateChanged += Flow_StateChanged;
        try
        {
            await flow.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PreviewKeyDown -= OnPreviewKeyDown;
        dialogCancellation?.Cancel();
        dialogCancellation?.Dispose();
        dialogCancellation = null;
        if (flow is not null)
        {
            flow.StateChanged -= Flow_StateChanged;
            flow.Dispose();
            flow = null;
        }

        httpClient?.Dispose();
        httpClient = null;
        QrImage.Source = null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestClose();
            e.Handled = true;
        }
    }

    private void ClampToWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 32));
    }

    private void ApplyLocalizedText()
    {
        AutomationProperties.SetName(CloseButton, T("Закрыть поддержку", "Close support"));
        Title = T("Поддержать Codex Swap Account", "Support Codex Swap Account");
        SupportTitle.Text = T("Поддержать Codex Swap Account", "Support Codex Swap Account");
        SupportSubtitle.Text = T("Приложение бесплатно и имеет открытый исходный код.", "The app is free and open source.");
        LoadingText.Text = T("Загрузка способов оплаты…", "Loading payment methods…");
        IntroText.Text = T("Если проект вам полезен, вы можете поддержать его развитие.", "If the project helps you, you can support its development.");
        TelegramMethodTitle.Text = T("Звёзды Telegram", "Telegram Stars");
        CryptoMethodTitle.Text = T("Криптовалюта", "Crypto");
        ChooseAmountTitle.Text = T("Выберите сумму", "Choose amount");
        TermsText.Text = T("Продолжая, вы принимаете условия добровольной поддержки.", "By continuing, you accept the voluntary support terms.");
        TermsHint.Text = T("Поддержка не открывает платные функции и не включает регулярные платежи.", "Support does not unlock paid features or start recurring payments.");
        BackToMethodsButton.Content = T("Назад", "Back");
        ContinueButton.Content = T("Продолжить", "Continue");
        ChooseAssetTitle.Text = T("Выберите криптовалюту", "Choose an asset");
        ChooseAssetHint.Text = T("Сеть указана под названием каждой монеты.", "The network is shown under each asset.");
        BackToAmountButton.Content = T("Назад", "Back");
        OpenTelegramButton.Content = T("Открыть Telegram", "Open Telegram");
        AddressLabel.Text = T("Адрес кошелька", "Wallet address");
        CopyAddressButton.ToolTip = T("Копировать адрес", "Copy address");
        AutomationProperties.SetName(CopyAddressButton, T("Копировать адрес кошелька", "Copy wallet address"));
        WaitingText.Text = T("Ожидаем оплату", "Waiting for payment");
        ThankYouTitle.Text = T("Спасибо за поддержку!", "Thank you for your support!");
        ThankYouText.Text = T(
            "Ваша поддержка помогает развивать Codex Swap Account и сохранять его бесплатным.",
            "Your support helps keep Codex Swap Account free and moving forward.");
        DoneButton.Content = T("Готово", "Done");
    }

    internal async Task ApplyVisualQaStateAsync(string state)
    {
        if (flow is null)
        {
            return;
        }

        switch (state.ToLowerInvariant())
        {
            case "stars-amount":
                _ = flow.SelectMethod("telegram_stars");
                showingMethods = false;
                RenderAmounts();
                ShowOnly(AmountPanel);
                break;
            case "crypto-amount":
                _ = flow.SelectMethod("direct_crypto");
                showingMethods = false;
                RenderAmounts();
                ShowOnly(AmountPanel);
                break;
            case "assets":
                _ = flow.SelectMethod("direct_crypto");
                await flow.BeginCheckoutAsync(5, russian ? "ru" : "en");
                RenderAssets();
                ShowOnly(AssetPanel);
                break;
            case "stars-checkout":
                _ = flow.SelectMethod("telegram_stars");
                RenderCheckout(new SupportCheckoutView(
                    "visual-qa-stars",
                    "telegram_stars",
                    "XTR",
                    100,
                    "https://t.me/$visualqa",
                    DateTimeOffset.UtcNow.AddMinutes(15)));
                ShowOnly(CheckoutPanel);
                break;
            case "crypto-checkout":
            case "toast":
                _ = flow.SelectMethod("direct_crypto");
                RenderCheckout(new SupportCheckoutView(
                    "visual-qa-crypto",
                    "direct_crypto",
                    "BTC",
                    5,
                    "bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq",
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    "BTC",
                    "bitcoin",
                    "Bitcoin",
                    "0.00008142",
                    "bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq",
                    "bitcoin:bc1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"));
                ShowOnly(CheckoutPanel);
                if (state.Equals("toast", StringComparison.OrdinalIgnoreCase))
                {
                    CopyToastText.Text = T("Адрес скопирован", "Address copied");
                    CopyToast.Visibility = Visibility.Visible;
                }

                break;
            case "success":
                ThankYouAmountText.Text = "$5";
                ShowOnly(SuccessPanel);
                break;
            case "error":
                ShowTerminal(
                    T("Срок платёжной сессии истёк", "This payment session expired"),
                    T("Создайте новый платёж, чтобы продолжить.", "Create a new payment to continue."),
                    canCreateNew: true);
                break;
        }
    }

    private void Flow_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateSupportState);
            return;
        }

        UpdateSupportState();
    }

    private void UpdateSupportState()
    {
        if (!(embeddedPanel?.IsLoaded ?? IsLoaded) || flow is null)
        {
            return;
        }

        switch (flow.State)
        {
            case SupportFlowState.LoadingMethods:
                ShowOnly(LoadingPanel);
                break;
            case SupportFlowState.ChoosingAmount:
                if (showingMethods)
                {
                    RenderMethods();
                    ShowOnly(MethodsPanel);
                    (TelegramMethodButton.IsEnabled ? TelegramMethodButton : CryptoMethodButton).Focus();
                }
                else
                {
                    RenderAmounts();
                    ShowOnly(AmountPanel);
                }

                break;
            case SupportFlowState.ChoosingAsset:
                RenderAssets();
                ShowOnly(AssetPanel);
                break;
            case SupportFlowState.CreatingCheckout:
                ClearCheckoutVisuals();
                ShowOnly(AmountPanel);
                SetAmountControlsEnabled(false);
                ContinueButton.Content = T("Создание платежа…", "Creating payment…");
                break;
            case SupportFlowState.Waiting:
            case SupportFlowState.ConnectivityIssue:
                if (flow.TryGetActiveCheckout(out SupportCheckoutView? checkout) && checkout is not null)
                {
                    RenderCheckout(checkout);
                    ShowOnly(CheckoutPanel);
                    WaitingText.Text = flow.State == SupportFlowState.ConnectivityIssue
                        ? T("Связь прервана. Повторяем попытку…", "Connection interrupted. Retrying…")
                        : T("Ожидаем оплату", "Waiting for payment");
                }

                break;
            case SupportFlowState.Paid:
                RenderSuccess();
                ShowOnly(SuccessPanel);
                DoneButton.Focus();
                break;
            case SupportFlowState.Expired:
                ShowTerminal(
                    T("Срок платёжной сессии истёк", "This payment session expired"),
                    T("Создайте новый платёж, чтобы продолжить.", "Create a new payment to continue."),
                    canCreateNew: true);
                break;
            case SupportFlowState.Refunded:
                ShowTerminal(
                    T("Платёж возвращён", "This payment was refunded"),
                    T("Эта платёжная сессия завершена.", "This payment session is complete."),
                    canCreateNew: true);
                break;
            case SupportFlowState.Failed:
                ShowFailure(flow.LastErrorCode);
                break;
        }
    }

    private void RenderMethods()
    {
        SupportMethod? stars = flow?.Methods.FirstOrDefault(item =>
            string.Equals(item.Id, "telegram_stars", StringComparison.OrdinalIgnoreCase));
        SupportMethod? crypto = flow?.Methods.FirstOrDefault(item =>
            string.Equals(item.Id, "direct_crypto", StringComparison.OrdinalIgnoreCase));
        TelegramMethodButton.IsEnabled = stars?.Enabled == true;
        TelegramMethodHint.Text = stars?.Enabled == true
            ? T("Быстрая оплата в Telegram", "Fast checkout in Telegram")
            : T("Сейчас недоступно", "Currently unavailable");
        CryptoMethodButton.IsEnabled = crypto?.Enabled == true;
        CryptoMethodHint.Text = crypto?.Enabled == true
            ? T("Оплата из криптокошелька", "Pay from a crypto wallet")
            : T("Сейчас недоступно", "Currently unavailable");
    }

    private void RenderAmounts()
    {
        if (flow?.Method is not { } method)
        {
            return;
        }

        SetAmountControlsEnabled(true);
        ContinueButton.Content = T("Продолжить", "Continue");
        AmountSuffixText.Content = IsCrypto(method.Id) ? new TextBlock { Text = "$" } : SupportIcons.Vector(SupportIcons.Star, new SolidColorBrush(Color.FromRgb(245, 184, 46)), 20);
        string range = method.Custom.Max is { } maximum
            ? $"{method.Custom.Min:N0}–{maximum:N0} {method.Currency}"
            : $"≥ {method.Custom.Min:N0} {method.Currency}";
        CustomAmountLabel.Text = T($"Своя сумма ({range})", $"Custom amount ({range})");
        CustomAmountTextBox.IsEnabled = method.Custom.Enabled;
        PresetPanel.Children.Clear();
        foreach (int amount in method.Presets)
        {
            var button = new RadioButton
            {
                Tag = amount,
                Content = AmountContent(amount, IsCrypto(method.Id)),
                GroupName = "SupportAmount",
                Style = (Style)FindResource("SupportAmountSelector"),
            };
            AutomationProperties.SetName(button, FormatAmount(amount));
            button.Checked += PresetButton_Checked;
            PresetPanel.Children.Add(button);
        }

        RefreshAmountSelection();
    }

    private UIElement AmountContent(int amount, bool crypto)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = crypto ? $"${amount:N0}" : $"{amount:N0}", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        if (!crypto)
        {
            var star = SupportIcons.Vector(SupportIcons.Star, new SolidColorBrush(Color.FromRgb(245, 184, 46)), 18);
            star.Margin = new Thickness(8, 0, 0, 0);
            content.Children.Add(star);
        }
        return content;
    }

    private void RenderAssets()
    {
        AssetButtons.Children.Clear();
        AssetButtons.RowDefinitions.Clear();
        AssetButtons.ColumnDefinitions.Clear();
        const int columns = 3;
        for (int column = 0; column < columns; column++)
        {
            AssetButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        SupportAsset[] assets = (flow?.Method?.Assets ?? []).ToArray();
        int rows = Math.Max(1, (assets.Length + columns - 1) / columns);
        for (int row = 0; row < rows; row++)
        {
            AssetButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int index = 0; index < assets.Length; index++)
        {
            SupportAsset asset = assets[index];
            var icon = SupportIcons.Coin(asset.Asset);
            var labels = new StackPanel
            {
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            labels.Children.Add(new TextBlock { Text = asset.Asset, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            labels.Children.Add(new TextBlock
            {
                Text = asset.NetworkName,
                FontSize = 10,
                Foreground = Brush("MutedTextBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(labels);
            var button = new Button
            {
                Tag = asset,
                Height = 72,
                Margin = new Thickness(4),
                Padding = new Thickness(10, 8, 8, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = (Style)FindResource("SupportOutlineButton"),
                Content = content,
            };
            AutomationProperties.SetName(button, $"{asset.DisplayName}, {asset.NetworkName}");
            button.Click += AssetButton_Click;
            Grid.SetRow(button, index / columns);
            Grid.SetColumn(button, index % columns);
            AssetButtons.Children.Add(button);
        }
    }

    private void TelegramMethodButton_Click(object sender, RoutedEventArgs e)
    {
        if (flow?.SelectMethod("telegram_stars") == true)
        {
            showingMethods = false;
            CustomAmountTextBox.Text = string.Empty;
            TermsCheckBox.IsChecked = false;
            UpdateSupportState();
        }
    }

    private void CryptoMethodButton_Click(object sender, RoutedEventArgs e)
    {
        if (flow?.SelectMethod("direct_crypto") == true)
        {
            showingMethods = false;
            CustomAmountTextBox.Text = string.Empty;
            TermsCheckBox.IsChecked = false;
            UpdateSupportState();
        }
    }

    private void BackToMethodsButton_Click(object sender, RoutedEventArgs e)
    {
        showingMethods = true;
        UpdateSupportState();
    }

    private void BackToAmountButton_Click(object sender, RoutedEventArgs e) => flow?.ChangeAmount();

    private void PresetButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int amount } &&
            flow?.TryGetPresetInput(amount, AmountCulture, out string? input) == true)
        {
            CustomAmountTextBox.Text = input;
            CustomAmountTextBox.CaretIndex = input.Length;
        }
    }

    private void CustomAmountTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAmountSelection();

    private void CustomAmountTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => NormalizeAmount();

    private void CustomAmountTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NormalizeAmount();
        }
    }

    private void TermsCheckBox_Changed(object sender, RoutedEventArgs e) => RefreshAmountSelection();

    private void RefreshAmountSelection()
    {
        if (ContinueButton is null || flow?.Method is not { } method)
        {
            return;
        }

        bool hasInput = !string.IsNullOrWhiteSpace(CustomAmountTextBox.Text);
        bool valid = flow.TryResolveAmountSelection(
            CustomAmountTextBox.Text,
            AmountCulture,
            out decimal amount,
            out int? selectedPreset);
        foreach (RadioButton button in PresetPanel.Children.OfType<RadioButton>())
        {
            button.IsChecked = selectedPreset == (int)button.Tag;
        }

        AmountValidationText.Text = !hasInput || valid
            ? string.Empty
            : T("Введите допустимую сумму числом.", "Enter a valid numeric amount.");
        ContinueButton.IsEnabled = valid && TermsCheckBox.IsChecked == true && method.Enabled;
        if (valid)
        {
            AutomationProperties.SetHelpText(ContinueButton, FormatAmount(amount));
        }
    }

    private void NormalizeAmount()
    {
        if (flow?.TryNormalizeAmount(CustomAmountTextBox.Text, AmountCulture, out decimal amount) != true)
        {
            return;
        }

        string normalized = amount.ToString(AmountCulture);
        if (!string.Equals(CustomAmountTextBox.Text, normalized, StringComparison.Ordinal))
        {
            CustomAmountTextBox.Text = normalized;
            CustomAmountTextBox.CaretIndex = normalized.Length;
        }

        RefreshAmountSelection();
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        NormalizeAmount();
        if (flow is null ||
            !flow.TryNormalizeAmount(CustomAmountTextBox.Text, AmountCulture, out decimal amount) ||
            TermsCheckBox.IsChecked != true)
        {
            RefreshAmountSelection();
            return;
        }

        await flow.BeginCheckoutAsync(
            amount,
            russian ? "ru" : "en",
            dialogCancellation?.Token ?? default);
    }

    private async void AssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SupportAsset asset } || flow is null)
        {
            return;
        }

        await flow.BeginDirectCryptoCheckoutAsync(asset, dialogCancellation?.Token ?? default);
    }

    private void RenderCheckout(SupportCheckoutView checkout)
    {
        bool crypto = IsCrypto(checkout.Provider);
        SupportAsset? selectedAsset = crypto
            ? flow?.Method?.Assets?.FirstOrDefault(item =>
                string.Equals(item.Asset, checkout.Asset, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Network, checkout.Network, StringComparison.OrdinalIgnoreCase))
            : null;
        CheckoutMethodText.Text = crypto
            ? selectedAsset?.DisplayName ?? checkout.Asset ?? T("Криптовалюта", "Crypto")
            : T("Звёзды Telegram", "Telegram Stars");
        CheckoutAmountText.Text = crypto
            ? checkout.CryptoAmount ?? string.Empty
            : checkout.Amount.ToString("N0", AmountCulture);
        CheckoutAmountSuffix.Content = crypto ? SupportIcons.Coin(checkout.Asset ?? "", 26) : SupportIcons.Vector(SupportIcons.Star, new SolidColorBrush(Color.FromRgb(245, 184, 46)), 26);
        QrFrame.Width = crypto ? 220 : 248;
        QrFrame.Height = crypto ? 220 : 248;
        OpenTelegramButton.Visibility = crypto ? Visibility.Collapsed : Visibility.Visible;
        OpenTelegramButton.IsEnabled = !crypto;
        CryptoDetailsPanel.Visibility = crypto ? Visibility.Visible : Visibility.Collapsed;
        ScanText.Text = crypto
            ? T("QR содержит адрес кошелька. Отправьте точную сумму, указанную выше.", "The QR contains the wallet address. Send the exact amount shown above.")
            : T("Отсканируйте телефоном или откройте Telegram", "Scan with your phone or open Telegram");
        AddressText.Text = checkout.WalletAddress ?? string.Empty;
        AddressText.ToolTip = checkout.WalletAddress;
        NetworkWarningText.Text = T(
            $"Сеть: {checkout.NetworkName ?? checkout.Network}. Отправляйте средства только в этой сети.",
            $"Network: {checkout.NetworkName ?? checkout.Network}. Send funds only on this network.");

        string? qrPayload = crypto ? checkout.WalletAddress : checkout.CheckoutUrl;
        if (string.IsNullOrWhiteSpace(qrPayload) || string.Equals(renderedQrPayload, qrPayload, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            byte[] png = SupportQrCodeService.CreatePng(checkout.Provider, qrPayload);
            using var stream = new MemoryStream(png, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            QrImage.Source = image;
            renderedQrPayload = qrPayload;
        }
        catch (InvalidDataException)
        {
            QrImage.Source = null;
            OpenTelegramButton.IsEnabled = false;
        }
    }

    private void OpenTelegramButton_Click(object sender, RoutedEventArgs e)
    {
        if (flow?.TryGetActiveCheckout(out SupportCheckoutView? checkout) != true || checkout is null ||
            !CheckoutUrlValidator.TryValidateTelegramCheckout(checkout.CheckoutUrl, out _))
        {
            ShowTerminal(
                T("Не удалось открыть оплату", "Could not open checkout"),
                T("Сервис вернул небезопасную ссылку.", "The service returned an unsafe checkout link."),
                canCreateNew: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = checkout.CheckoutUrl, UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ShowTerminal(
                T("Не удалось открыть Telegram", "Could not open Telegram"),
                T("Повторите попытку позже.", "Please try again later."),
                canCreateNew: true);
        }
    }

    private async void CopyAddressButton_Click(object sender, RoutedEventArgs e)
    {
        if (flow?.Checkout?.WalletAddress is not { Length: > 0 } address)
        {
            return;
        }

        try
        {
            Clipboard.SetText(address);
            await ShowCopyToastAsync(T("Адрес скопирован", "Address copied"));
        }
        catch (ExternalException)
        {
            CopyToast.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowCopyToastAsync(string message)
    {
        int activeGeneration = ++copyToastGeneration;
        CopyToastText.Text = message;
        CopyToast.Visibility = Visibility.Visible;
        await Task.Delay(1400);
        if (activeGeneration == copyToastGeneration)
        {
            CopyToast.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderSuccess()
    {
        if (flow?.Checkout is not { } checkout)
        {
            return;
        }

        ThankYouAmountText.Text = IsCrypto(checkout.Provider)
            ? $"${checkout.Amount.ToString("0.##", AmountCulture)}"
            : $"{checkout.Amount.ToString("N0", AmountCulture)} ★";
    }

    private void ShowFailure(string? code)
    {
        string text = code switch
        {
            "PROVIDER_NOT_CONFIGURED" or "METHOD_UNAVAILABLE" =>
                T("Этот способ оплаты сейчас недоступен.", "This payment method is currently unavailable."),
            "UNSAFE_CHECKOUT_URL" =>
                T("Сервис вернул небезопасную ссылку. Оплата не была открыта.", "The service returned an unsafe link. Checkout was not opened."),
            "NETWORK_UNAVAILABLE" or "NETWORK_TIMEOUT" =>
                T("Не удалось подключиться к сервису поддержки.", "Could not connect to the support service."),
            "PAYMENT_NOT_FOUND" =>
                T("Платёжная сессия больше недоступна.", "The payment session is no longer available."),
            _ => T("Не удалось продолжить платёж. Попробуйте ещё раз позже.", "Could not continue the payment. Please try again later."),
        };
        ShowTerminal(T("Что-то пошло не так", "Something went wrong"), text, flow?.Method is not null);
    }

    private void ShowTerminal(string title, string text, bool canCreateNew)
    {
        TerminalTitle.Text = title;
        TerminalText.Text = text;
        CreateNewButton.Visibility = canCreateNew ? Visibility.Visible : Visibility.Collapsed;
        CreateNewButton.Content = flow?.Method is null
            ? T("Повторить", "Retry")
            : T("Создать новый платёж", "Create new payment");
        ShowOnly(TerminalPanel);
        if (canCreateNew)
        {
            CreateNewButton.Focus();
        }
    }

    private async void CreateNewButton_Click(object sender, RoutedEventArgs e)
    {
        if (flow?.Method is null)
        {
            ShowOnly(LoadingPanel);
            if (flow is not null)
            {
                await flow.LoadAsync(dialogCancellation?.Token ?? default);
            }

            return;
        }

        CustomAmountTextBox.Text = string.Empty;
        TermsCheckBox.IsChecked = false;
        ClearCheckoutVisuals();
        showingMethods = false;
        flow.ChangeAmount();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void ShowOnly(FrameworkElement panel)
    {
        foreach (FrameworkElement item in new FrameworkElement[]
                 {
                     LoadingPanel,
                     MethodsPanel,
                     AmountPanel,
                     AssetPanel,
                     CheckoutPanel,
                     SuccessPanel,
                     TerminalPanel,
                 })
        {
            item.Visibility = ReferenceEquals(item, panel) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ClearCheckoutVisuals()
    {
        renderedQrPayload = null;
        QrImage.Source = null;
        copyToastGeneration++;
        CopyToast.Visibility = Visibility.Collapsed;
        OpenTelegramButton.IsEnabled = false;
        OpenTelegramButton.Visibility = Visibility.Collapsed;
        CryptoDetailsPanel.Visibility = Visibility.Collapsed;
    }

    private void SetAmountControlsEnabled(bool enabled)
    {
        PresetPanel.IsEnabled = enabled;
        CustomAmountTextBox.IsEnabled = enabled && flow?.Method?.Custom.Enabled == true;
        TermsCheckBox.IsEnabled = enabled;
        if (!enabled)
        {
            ContinueButton.IsEnabled = false;
        }
    }

    private string FormatAmount(decimal amount) => IsCrypto(flow?.Method?.Id)
        ? $"${amount.ToString("0.##", AmountCulture)}"
        : $"{amount.ToString("N0", AmountCulture)} Stars";

    private static bool IsCrypto(string? methodId) =>
        string.Equals(methodId, "direct_crypto", StringComparison.OrdinalIgnoreCase);

    private Brush Brush(string key) => (Brush)FindResource(key);
}
