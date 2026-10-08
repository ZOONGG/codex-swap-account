using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodexProfileOverlay;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowStyle(IntPtr handle, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    private static void CheckChrome(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        long style = GetWindowStyle(handle, -16).ToInt64();
        Check((style & 0x20000) == 0 && (style & 0x10000) != 0 && (style & 0x40000) != 0, "Auxiliary chrome must disable minimize and preserve maximize/resize");
        SendMessage(handle, 0x112, (IntPtr)0xF020, IntPtr.Zero);
        Check(window.IsVisible && window.WindowState != WindowState.Minimized, "Native minimize command must not create desktop strip");
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            source.Root!.Element(ns + "Application.Resources")!.Nodes());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        string temp = Path.Combine(Path.GetTempPath(), "overlay-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var language in new[] { LanguagePreference.English, LanguagePreference.Russian })
            {
                App.ApplyTheme(theme);
                if (args.Contains("--hover-only")) { CheckPopup(temp, language); continue; }
                CheckSettings(temp, theme, language);
                CheckProfileManager(language);
                CheckAmounts(theme, language);
                CheckPopup(temp, language);
            }
            if (args.Contains("--hover-only")) { Console.WriteLine("PASS: Hover open/leave, popup entry, profile replacement, card click isolation and dimensions, bottom placement in expanded/compact, dark/light and RU/EN."); return; }
            Console.WriteLine("PASS: Hover/bottom popup in expanded/compact modes; auxiliary chrome/close/reopen/maximize; header focus; Stars/Crypto presets/manual input; input rendering; scrollbar dark/light and RU/EN.");
        }
        finally { app.Shutdown(); Directory.Delete(temp, true); }
    }

    private static void CheckSettings(string temp, AppTheme theme, LanguagePreference language)
    {
        var assembly = typeof(App).Assembly;
        var paths = new AppPaths(temp, temp);
        using var status = new ProfileStatusService(new ProfileStatusStore(paths.ProfileStatusFile), new UnavailableUsageProvider(), new SafeLogger(Path.Combine(temp, "logs")));
        using var updates = new UpdateService(Path.Combine(temp, "update.json"));
        var settings = new OverlaySettings { Theme = theme };
        var localizerType = assembly.GetType("CodexProfileOverlay.Localizer")!;
        object localizer = Activator.CreateInstance(localizerType, language)!;
        var ctor = assembly.GetType("CodexProfileOverlay.SettingsWindow")!.GetConstructors().Single();
        object[] args = ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(OverlaySettings) ? (object)settings :
            p.ParameterType == typeof(IReadOnlyList<ProfileInfo>) ? Array.Empty<ProfileInfo>() :
            p.ParameterType == localizerType ? localizer :
            p.ParameterType == typeof(ProfileStatusService) ? status :
            p.ParameterType == typeof(BackupMaintenanceService) ? new BackupMaintenanceService(paths) :
            p.ParameterType == typeof(UpdateService) ? updates :
            p.ParameterType == typeof(Action<OverlaySettings>) ? (Action<OverlaySettings>)(_ => { }) :
            p.ParameterType == typeof(Func<string, Task>) ? (Func<string, Task>)(_ => Task.CompletedTask) :
            p.ParameterType == typeof(Func<bool>) ? (Func<bool>)(() => true) :
            (Action)(() => { })).ToArray();
        var window = (Window)ctor.Invoke(args);
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
        double width = window.Width, height = window.Height, left = window.Left, top = window.Top;
        window.Close();
        Check(!window.IsVisible && !IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(window).Handle), "Settings X must hide native window");
        window.Show();
        Check(window.IsVisible, "Settings must reopen after X");
        CheckChrome(window);
        window.WindowState = WindowState.Maximized;
        window.Close();
        window.Show();
        Check(window.IsVisible && window.WindowState == WindowState.Maximized, "Settings must reopen maximized");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Check(window.Width == width && window.Height == height && window.Left == left && window.Top == top, "Settings must retain restore geometry");
        foreach (string name in new[] { "githubButton", "supportButton", "themeButton" })
        {
            var button = (Button)Field(window, name);
            button.Focus(); button.ApplyTemplate();
            var chrome = (Border)button.Template.FindName("Chrome", button);
            Check(Equals(chrome.BorderBrush, button.BorderBrush), "Programmatic/mouse focus must not look selected: " + name);
            Check(button.FocusVisualStyle is not null, "Keyboard focus must retain accessible visual: " + name);
        }
        Invoke(window, "OpenSupport");
        ((Action)Field(window, "dismissSupport"))();
        Check(!((Grid)Field(window, "modalHost")).IsVisible, "Support dismissal must restore settings");
        var bar = new ScrollBar { Orientation = Orientation.Vertical, Maximum = 100, ViewportSize = 20 };
        bar.Style = (Style)appResource(typeof(ScrollBar));
        bar.Measure(new Size(10, 300)); bar.Arrange(new Rect(0, 0, 10, 300)); bar.ApplyTemplate();
        Check(bar.ActualHeight == 300 && bar.Template.FindName("PART_Track", bar) is Track, "Scrollbar must fill vertical track");
        window.GetType().GetMethod("CloseForShutdown")!.Invoke(window, null);
    }

    private static void CheckProfileManager(LanguagePreference language)
    {
        var assembly = typeof(App).Assembly;
        var localizerType = assembly.GetType("CodexProfileOverlay.Localizer")!;
        var ctor = assembly.GetType("CodexProfileOverlay.ProfileManagerWindow")!.GetConstructors().Single();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            object?[] args = ctor.GetParameters().Select(p =>
                p.ParameterType == typeof(IReadOnlyList<ProfileInfo>) ? (object)Array.Empty<ProfileInfo>() :
                p.ParameterType == typeof(string) ? null :
                p.ParameterType == localizerType ? Activator.CreateInstance(localizerType, language) :
                p.ParameterType == typeof(Action<ProfileInfo>) ? (Action<ProfileInfo>)(_ => { }) :
                p.ParameterType == typeof(Action<IReadOnlyList<string>>) ? (Action<IReadOnlyList<string>>)(_ => { }) :
                p.ParameterType == typeof(Func<ProfileInfo, AccountAvailability>) ? (Func<ProfileInfo, AccountAvailability>)(_ => default) :
                (object)(Action)(() => { })).ToArray();
            var window = (Window)ctor.Invoke(args);
            window.ShowInTaskbar = false;
            window.Show();
            CheckChrome(window);
            window.Hide();
            Check(!IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(window).Handle), "Profile hide must hide native window");
            window.Show();
            Check(window.IsVisible && window.WindowState == WindowState.Normal, "Profile manager must reopen normally");
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            window.Close();
            Check(!IsWindowVisible(handle), "Profile close must destroy native window");
        }
    }

    private static void CheckPopup(string temp, LanguagePreference language)
    {
        var type = typeof(App).Assembly.GetType("CodexProfileOverlay.OverlayWindow")!;
        var window = (Window)Activator.CreateInstance(type, new OverlaySettings(), new SafeLogger(Path.Combine(temp, "logs")))!;
        var localizerType = typeof(App).Assembly.GetType("CodexProfileOverlay.Localizer")!;
        type.GetProperty("Localizer")!.SetValue(window, Activator.CreateInstance(localizerType, language));
        var profile = new ProfileInfo("test", temp, "unused");
        int refreshCalls = 0;
        type.GetProperty("OnRefreshUsage")!.SetValue(window, (Func<string, Task>)(_ => { refreshCalls++; return Task.CompletedTask; }));
        window.Left = 400; window.Top = 200;
        window.Show();
        foreach (bool compact in new[] { false, true })
        foreach (bool active in new[] { false, true })
        {
            var account = compact
                ? (Button)Invoke(window, "CreateProfilePopupButton", profile, "", active)!
                : (Button)Invoke(window, "CreateProfileButton", profile, active)!;
            if (compact) account.Width = 266;
            account.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double originalWidth = account.DesiredSize.Width;
            var row = (Button)Invoke(window, "ProfileWithDetails", profile, account)!;
            Check(ReferenceEquals(row, account), "Info must be embedded in the original profile card");
            Check(!Descendants(account).OfType<Button>().Any(), "Card must contain no nested info button");
            int accountClicks = 0;
            account.Click += (_, _) => accountClicks++;
            var shell = (Border)Field(window, "shell");
            var compactPopup = (Popup)Field(window, "compactPopup");
            var otherAccount = (Button)Invoke(window, "CreateProfileButton", new ProfileInfo("other", temp, "unused"), false)!;
            Invoke(window, "ProfileWithDetails", new ProfileInfo("other", temp, "unused"), otherAccount);
            var cards = new StackPanel();
            cards.Children.Add(row);
            cards.Children.Add(otherAccount);
            if (compact)
            {
                shell.Child = new Button { Content = "Profiles" };
                compactPopup.PlacementTarget = shell;
                compactPopup.Child = cards;
                compactPopup.IsOpen = true;
            }
            else shell.Child = cards;
            row.UpdateLayout();
            Check(Math.Abs(account.ActualWidth + account.Margin.Left + account.Margin.Right - originalWidth) < 1, "Embedding info must preserve profile width");
            account.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Check(!((Popup)Field(window, "usagePopup")).IsOpen, "Hover must wait before opening");
            Invoke(window, "OpenPendingUsage");
            Check(accountClicks == 0, "Hover must never trigger account click");
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var popup = (Popup)Field(window, "usagePopup");
            Check(popup.Placement == PlacementMode.Bottom, "Usage popup must retain original bottom placement");
            Check(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, account), "Usage popup must anchor to selected profile");
            Check(!compact || compactPopup.IsOpen, "Compact profile anchor must remain visible");
            var panel = (FrameworkElement)popup.Child;
            panel.UpdateLayout();
            var position = panel.PointToScreen(new Point());
            Check(position.X >= 0 && position.Y >= 0 && position.X + panel.ActualWidth <= SystemParameters.PrimaryScreenWidth + 1 && position.Y + panel.ActualHeight <= SystemParameters.PrimaryScreenHeight + 1, "Usage popup must fit screen");
            account.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Check(popup.IsOpen, "Leave must allow time to cross into popup");
            panel.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Check(!((System.Windows.Threading.DispatcherTimer)Field(window, "usageCloseTimer")).IsEnabled, "Entering popup must cancel close");
            Invoke(window, "ScheduleUsage", "other", otherAccount);
            Check(!popup.IsOpen, "Entering adjacent card must immediately hide stale panel");
            Invoke(window, "OpenPendingUsage");
            Check(ReferenceEquals(popup.PlacementTarget, otherAccount) && (string)Field(window, "usageProfile") == "other", "Hover must replace profile data");
            Invoke(window, "CloseUsageIfOutside");
            Check(!popup.IsOpen, "Leaving both surfaces must close popup");
            popup.IsOpen = false;
            account.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(accountClicks == 1, "Remaining card must stay clickable");
            Check(refreshCalls == 0, "Hover must use cache without refreshing limits");
            Invoke(window, "ScheduleUsage", "test", account);
            Invoke(window, "ScheduleUsageClose");
            Check(!((System.Windows.Threading.DispatcherTimer)Field(window, "usageOpenTimer")).IsEnabled, "Rapid leave must cancel pending opening");
            Invoke(window, "CloseUsageIfOutside");
        }
        window.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static object appResource(object key) => Application.Current.FindResource(key);

    private static void CheckAmounts(AppTheme theme, LanguagePreference language)
    {
        var window = new SupportWindow(theme, language);
        using var http = new HttpClient();
        using var flow = new SupportFlowController(new SupportApiClient(http, new Uri("https://support.example.test/")));
        window.GetType().GetField("flow", Private)!.SetValue(window, flow);
        // Exercise loaded method UI without contacting a support service.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            window.GetType().GetMethod("OnLoaded", Private | BindingFlags.DeclaredOnly)!);
        typeof(SupportFlowController).GetProperty("Methods")!.SetValue(flow, new[]
        {
            new SupportMethod("telegram_stars", true, "XTR", new[] { 50 }, new SupportCustomAmount(true, 1, 10000)),
            new SupportMethod("direct_crypto", true, "USD", new[] { 25 }, new SupportCustomAmount(true, 1, 10000)),
        });
        typeof(SupportFlowController).GetProperty("State")!.SetValue(flow, SupportFlowState.ChoosingAmount);
        window.Show();
        for (int opening = 0; opening < 2; opening++)
        {
            Invoke(window, "UpdateSupportState");
            window.UpdateLayout();
            Check(((Button)Field(window, "CloseButton")).IsKeyboardFocused, "Method chooser must focus close rather than a payment method");
            foreach (string name in new[] { "TelegramMethodButton", "CryptoMethodButton" })
            {
                var methodButton = (Button)Field(window, name);
                methodButton.ApplyTemplate();
                var surface = (Border)methodButton.Template.FindName("Surface", methodButton);
                Check(!methodButton.IsKeyboardFocused && Equals(surface.BorderBrush, methodButton.BorderBrush), "Payment method must start neutral: " + name);
                Check(methodButton.FocusVisualStyle is not null, "Payment methods must retain keyboard focus indication");
            }
            window.Hide(); window.Show();
        }
        foreach (var pair in new[] { ("telegram_stars", 50), ("direct_crypto", 25) })
        {
            var method = new SupportMethod(pair.Item1, true, pair.Item1 == "direct_crypto" ? "USD" : "XTR", new[] { pair.Item2 }, new SupportCustomAmount(true, 1, 10000));
            typeof(SupportFlowController).GetProperty("Method")!.SetValue(flow, method);
            Invoke(window, "RenderAmounts");
            var input = (TextBox)Field(window, "CustomAmountTextBox");
            var presets = (WrapPanel)Field(window, "PresetPanel");
            ((RadioButton)presets.Children[0]).IsChecked = true;
            Check(input.Text == pair.Item2.ToString(), "Preset must populate input");
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                input.LayoutTransform = new ScaleTransform(scale, scale);
                input.ApplyTemplate(); input.Measure(new Size(400 * scale, 60 * scale));
                input.Arrange(new Rect(0, 0, 400 * scale, 50 * scale)); input.UpdateLayout();
                var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                Check(host.ViewportHeight >= 18, $"Amount text viewport must not clip digits: host={host.ActualHeight}, viewport={host.ViewportHeight}, extent={host.ExtentHeight}, scale={scale}, padding={host.Padding}");
            }
            input.Text = "37";
            Check(((RadioButton)presets.Children[0]).IsChecked != true, "Manual editing must clear preset");
            Check(flow.TryNormalizeAmount(input.Text, System.Globalization.CultureInfo.InvariantCulture, out decimal amount) && amount == 37, "Manual amount must remain valid");
        }
        window.Close();
    }
}
