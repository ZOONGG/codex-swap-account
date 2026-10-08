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

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [STAThread]
    private static void Main()
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
                CheckSettings(temp, theme, language);
                CheckAmounts(theme, language);
                CheckPopup(temp, language);
            }
            Console.WriteLine("PASS: Popup profile anchoring in expanded/compact modes; Settings X/minimize/reopen; Stars/Crypto presets/manual input; input rendering; scrollbar dark/light and RU/EN.");
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
        window.WindowState = WindowState.Minimized;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(!window.IsVisible && !IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(window).Handle) && window.WindowState == WindowState.Normal, "Settings minimize must hide native window and normalize");
        window.Show(); window.Activate(); window.UpdateLayout();
        Check(window.IsVisible && window.Width == width && window.Height == height && window.Left == left && window.Top == top, $"Settings must retain geometry on reopen: {left},{top} {width}x{height} -> {window.Left},{window.Top} {window.Width}x{window.Height}");
        var bar = new ScrollBar { Orientation = Orientation.Vertical, Maximum = 100, ViewportSize = 20 };
        bar.Style = (Style)appResource(typeof(ScrollBar));
        bar.Measure(new Size(10, 300)); bar.Arrange(new Rect(0, 0, 10, 300)); bar.ApplyTemplate();
        Check(bar.ActualHeight == 300 && bar.Template.FindName("PART_Track", bar) is Track, "Scrollbar must fill vertical track");
        window.GetType().GetMethod("CloseForShutdown")!.Invoke(window, null);
    }

    private static void CheckPopup(string temp, LanguagePreference language)
    {
        var type = typeof(App).Assembly.GetType("CodexProfileOverlay.OverlayWindow")!;
        var window = (Window)Activator.CreateInstance(type, new OverlaySettings(), new SafeLogger(Path.Combine(temp, "logs")))!;
        var localizerType = typeof(App).Assembly.GetType("CodexProfileOverlay.Localizer")!;
        type.GetProperty("Localizer")!.SetValue(window, Activator.CreateInstance(localizerType, language));
        var profile = new ProfileInfo("test", temp, "unused");
        window.Left = 400; window.Top = 200;
        window.Show();
        foreach (bool compact in new[] { false, true })
        {
            var account = new Button { Content = "G Grille", Width = 120, Height = 32 };
            var row = (DockPanel)Invoke(window, "ProfileWithDetails", profile, account)!;
            var shell = (Border)Field(window, "shell");
            var compactPopup = (Popup)Field(window, "compactPopup");
            if (compact)
            {
                shell.Child = new Button { Content = "Profiles" };
                compactPopup.PlacementTarget = shell;
                compactPopup.Child = row;
                compactPopup.IsOpen = true;
            }
            else shell.Child = row;
            row.UpdateLayout();
            ((Button)row.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var popup = (Popup)Field(window, "usagePopup");
            Check(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, account), "Usage popup must anchor to selected profile");
            Check(!compact || compactPopup.IsOpen, "Compact profile anchor must remain visible");
            var panel = (FrameworkElement)popup.Child;
            panel.UpdateLayout();
            var position = panel.PointToScreen(new Point());
            Check(position.X >= 0 && position.Y >= 0 && position.X + panel.ActualWidth <= SystemParameters.PrimaryScreenWidth + 1 && position.Y + panel.ActualHeight <= SystemParameters.PrimaryScreenHeight + 1, "Usage popup must fit screen");
            popup.IsOpen = false;
        }
        window.Close();
    }

    private static object appResource(object key) => Application.Current.FindResource(key);

    private static void CheckAmounts(AppTheme theme, LanguagePreference language)
    {
        var window = new SupportWindow(theme, language);
        using var http = new HttpClient();
        using var flow = new SupportFlowController(new SupportApiClient(http, new Uri("https://support.example.test/")));
        window.GetType().GetField("flow", Private)!.SetValue(window, flow);
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
