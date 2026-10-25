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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);

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
        if (args.Contains("--hold-login-pipes")) { Thread.Sleep(10000); return; }
        if (args.Contains("login") && args.Contains("-c"))
        {
            string mode = File.ReadAllText("fixture-mode.txt");
            if (mode == "wait") Thread.Sleep(10000);
            if (mode == "fail") { Environment.ExitCode = 7; return; }
            if (mode == "success") File.WriteAllText("auth.json", "FAKE TEST AUTH ONLY");
            if (mode == "pipes")
            {
                var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                { Arguments = "--hold-login-pipes", UseShellExecute = false, CreateNoWindow = true })!;
                File.WriteAllText("fixture-child.txt", child.Id.ToString());
                child.Dispose();
            }
            return;
        }
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
            if (args.Contains("--removal-only")) { CheckRemoval(temp); return; }
            if (args.Contains("--login-only")) { CheckLoginRuntime(temp); return; }
            if (args.Contains("--dialogs-only"))
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                    Console.WriteLine($"Monitor: {screen.DeviceName}, bounds={screen.Bounds}, workArea={screen.WorkingArea}, primary={screen.Primary}");
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var language in new[] { LanguagePreference.English, LanguagePreference.Russian, LanguagePreference.Chinese })
            {
                App.ApplyTheme(theme);
                if (args.Contains("--dialogs-only"))
                {
                    foreach (var screen in System.Windows.Forms.Screen.AllScreens) CheckLoginDialogs(language, screen);
                    continue;
                }
                if (args.Contains("--hover-only")) { CheckPopup(temp, language); continue; }
                CheckSettings(temp, theme, language, args.Contains("--localization-only"));
                CheckProfileManager(language);
                CheckAmounts(theme, language);
                CheckPopup(temp, language);
            }
            if (args.Contains("--dialogs-only")) { Console.WriteLine("PASS: prompt modal ownership, progress cancellation/reopen, physical bounds and display-change recovery; EN/RU/ZH, dark/light. Physical multi-monitor scenario still requires manual testing."); return; }
            if (args.Contains("--localization-only")) { Console.WriteLine("PASS: language combo switches EN/RU/ZH, settings retained, profile manager, support amounts and usage cards; dark/light."); return; }
            if (args.Contains("--hover-only")) { Console.WriteLine("PASS: Hover open/leave, popup entry, profile replacement, card click isolation and dimensions, bottom placement in expanded/compact, dark/light and EN/RU/ZH."); return; }
            Console.WriteLine("PASS: Update card portable/installed/ready/progress; hover/bottom popup; auxiliary chrome/close/reopen/maximize; header focus; Stars/Crypto input; dark/light and EN/RU/ZH.");
        }
        finally { app.Shutdown(); Directory.Delete(temp, true); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private static void CheckRemoval(string temp)
    {
        var assembly = typeof(App).Assembly;
        var paths = new AppPaths(temp, temp);
        Directory.CreateDirectory(paths.ProfilesDirectory);
        foreach (string name in new[] { "current", "old" })
        {
            Directory.CreateDirectory(Path.Combine(paths.ProfilesDirectory, name));
            File.WriteAllText(Path.Combine(paths.ProfilesDirectory, name, "auth.json"), "FAKE TEST AUTH");
        }
        var service = new ProfileManagerService(paths, new ProfileDiscoveryService(paths.ProfilesDirectory), new ProfileMetadataStore(paths.ProfilesMetadataFile));
        service.EnsureMetadata();
        var active = new ActiveProfileStore(paths.ActiveProfileFile);
        active.Write("current");
        object controller = Activator.CreateInstance(assembly.GetType("CodexProfileOverlay.OverlayController")!, paths, service, active,
            new SettingsService(paths.SettingsFile), null!, null!, null!, new SafeLogger(paths.LogDirectory), new BackupMaintenanceService(paths))!;
        Window? manager = null;
        Window? settingsWindow = null;
        try
        {
            var localizerType = assembly.GetType("CodexProfileOverlay.Localizer")!;
            object localizer = Activator.CreateInstance(localizerType, LanguagePreference.English)!;
            var ctor = assembly.GetType("CodexProfileOverlay.ProfileManagerWindow")!.GetConstructors().Single();
            object?[] args = ctor.GetParameters().Select(p =>
                p.ParameterType == typeof(IReadOnlyList<ProfileInfo>) ? (object)service.ListProfiles() :
                p.ParameterType == typeof(string) ? "current" :
                p.ParameterType == localizerType ? localizer :
                p.Name == "removeProfile" ? (Action<ProfileInfo>)(profile => Invoke(controller, "RemoveProfile", profile)) :
                p.ParameterType == typeof(Action<ProfileInfo>) ? (Action<ProfileInfo>)(_ => { }) :
                p.ParameterType == typeof(Action<IReadOnlyList<string>>) ? (Action<IReadOnlyList<string>>)(_ => { }) :
                p.ParameterType == typeof(Func<ProfileInfo, AccountAvailability>) ? (Func<ProfileInfo, AccountAvailability>)(_ => default) :
                (object)(Action)(() => { })).ToArray();
            manager = (Window)ctor.Invoke(args);
            controller.GetType().GetField("profileManagerWindow", Private)!.SetValue(controller, manager);
            var settingsCtor = assembly.GetType("CodexProfileOverlay.SettingsWindow")!.GetConstructors().Single();
            object[] settingsArgs = settingsCtor.GetParameters().Select(p =>
                p.ParameterType == typeof(OverlaySettings) ? (object)new OverlaySettings() :
                p.ParameterType == typeof(IReadOnlyList<ProfileInfo>) ? service.ListProfiles() :
                p.ParameterType == localizerType ? localizer :
                p.ParameterType == typeof(ProfileStatusService) ? Field(controller, "statusService") :
                p.ParameterType == typeof(BackupMaintenanceService) ? new BackupMaintenanceService(paths) :
                p.ParameterType == typeof(UpdateService) ? Field(controller, "updates") :
                p.ParameterType == typeof(Action<OverlaySettings>) ? (Action<OverlaySettings>)(_ => { }) :
                p.ParameterType == typeof(Func<string, Task>) ? (Func<string, Task>)(_ => Task.CompletedTask) :
                p.ParameterType == typeof(Func<bool>) ? (Func<bool>)(() => true) :
                p.ParameterType == typeof(Action<bool>) ? (Action<bool>)(_ => { }) : (Action)(() => { })).ToArray();
            settingsWindow = (Window)settingsCtor.Invoke(settingsArgs);
            controller.GetType().GetField("settingsWindow", Private)!.SetValue(controller, settingsWindow);
            settingsWindow.Show();
            manager.Show();
            var activeRow = ((ListBox)Field(manager, "listBox")).Items.OfType<ListBoxItem>().Single(i => ((ProfileInfo)i.Tag).Name == "current");
            Check(!Descendants(activeRow).OfType<Button>().Single().ContextMenu!.Items.OfType<MenuItem>().Last().IsEnabled, "Active profile removal must be disabled");
            int dialogs = 0, errors = 0;
            bool confirm = false;
            bool changeActive = false;
            bool escapeCancel = false;
            var watchdog = System.Diagnostics.Stopwatch.StartNew();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                Check(watchdog.Elapsed < TimeSpan.FromSeconds(15), "Removal UI scenario timed out");
                var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "ConfirmDialog" && w.IsVisible);
                if (dialog is not null)
                {
                    dialogs++;
                    Check(ReferenceEquals(dialog.Owner, manager), "Removal confirmation must belong to profile manager");
                    // A nested UI action must not enter a second removal during confirmation.
                    Invoke(controller, "RemoveProfile", service.ListProfiles().Single(p => p.Name == "current"));
                    if (escapeCancel)
                    {
                        dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(dialog)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                        return;
                    }
                    if (changeActive) active.Write("old");
                    Invoke(Descendants(dialog).OfType<Button>().Single(b => confirm ? b.IsDefault : Equals(b.Content, "Cancel")), "OnClick");
                    return;
                }
                var hwnd = GetActiveWindow();
                if (hwnd != IntPtr.Zero && GetWindow(hwnd, 4) == new System.Windows.Interop.WindowInteropHelper(manager).Handle)
                {
                    errors++;
                    SendMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
            };
            timer.Start();
            try
            {
                var old = service.ListProfiles().Single(p => p.Name == "old");
                string metadata = File.ReadAllText(paths.ProfilesMetadataFile);
                Invoke(controller, "RemoveProfile", old);
                Check(dialogs == 1 && errors == 0 && service.ListProfiles().Count == 2, "Cancel must preserve profiles");
                Check(metadata == File.ReadAllText(paths.ProfilesMetadataFile) && !Directory.Exists(paths.RemovedProfilesDirectory), "Cancel must perform no writes");
                // Closing the confirmation with X/Close is also cancellation.
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Windows.Cast<Window>().Single(w => w.GetType().Name == "ConfirmDialog").Close()));
                Invoke(controller, "RemoveProfile", old);
                Check(metadata == File.ReadAllText(paths.ProfilesMetadataFile) && !Directory.Exists(paths.RemovedProfilesDirectory), "Closing confirmation must preserve data");
                escapeCancel = true;
                Invoke(controller, "RemoveProfile", old);
                escapeCancel = false;
                Check(metadata == File.ReadAllText(paths.ProfilesMetadataFile) && !Directory.Exists(paths.RemovedProfilesDirectory), "Escape must preserve data");
                confirm = true;
                File.WriteAllText(paths.RemovedProfilesDirectory, "blocked");
                Invoke(controller, "RemoveProfile", old);
                Check(errors == 1 && service.ListProfiles().Count == 2 && manager.IsVisible, "File failure must show an owned error and keep app/profile alive");
                File.Delete(paths.RemovedProfilesDirectory);
                changeActive = true;
                Invoke(controller, "RemoveProfile", old);
                Check(errors == 2 && service.ListProfiles().Count == 2, "Active profile must be rechecked after confirmation");
                changeActive = false;
                active.Write("current");
                Invoke(controller, "RemoveProfile", old);
                Check(service.ListProfiles().Count == 1 && ((ListBox)Field(manager, "listBox")).Items.Count == 1, "Successful removal must rebuild the open profile manager");
                Check(((IReadOnlyList<ProfileInfo>)Field(settingsWindow, "profiles")).Count == 1, "Open Settings must receive the updated list");
                Check(!(bool)Field(controller, "accountOperation"), "Removal guard must reset after every outcome");
                Check(Directory.GetFiles(paths.RemovedProfilesDirectory, "auth.json", SearchOption.AllDirectories).Length == 1, "Recovery must preserve saved login");
            }
            finally { timer.Stop(); }
        }
        finally { manager?.Close(); settingsWindow?.GetType().GetMethod("CloseForShutdown")!.Invoke(settingsWindow, null); ((IDisposable)controller).Dispose(); }
        Console.WriteLine("PASS: removal confirmation/cancel, filesystem error, active-marker change, recovery, open Settings/manager refresh and operation guard.");
    }

    private static void CheckLoginRuntime(string temp)
    {
        var runtime = new OfficialProfileLoginRuntime(() => Environment.ProcessPath);
        runtime.EnsureAvailable();
        foreach (string mode in new[] { "fail", "missing", "success", "wait", "pipes", "success" })
        {
            string directory = Path.Combine(temp, mode + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "fixture-mode.txt"), mode);
            using var cancellation = new CancellationTokenSource();
            if (mode == "wait") cancellation.CancelAfter(500);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool result = runtime.LoginAsync(directory, cancellation.Token).GetAwaiter().GetResult();
                Check(mode == "success" && result, "Only successful login may return true");
            }
            catch (LoginProcessFailedException) { Check(mode == "fail", "Typed CLI failure"); }
            catch (LoginAuthMissingException) { Check(mode is "missing" or "pipes", "Typed missing authorization"); }
            catch (OperationCanceledException) { Check(mode == "wait", "Cancellation terminates CLI"); }
            finally
            {
                string childFile = Path.Combine(directory, "fixture-child.txt");
                if (File.Exists(childFile))
                {
                    try { using var child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(childFile))); child.Kill(true); child.WaitForExit(5000); }
                    catch (ArgumentException) { }
                }
            }
            Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Login cleanup must be bounded, even with inherited pipes");
        }
        Console.WriteLine("PASS: real subprocess exit failure, missing auth, success, timeout/cancel, inherited pipes, retry; fake credentials only.");
    }

    private static void CheckLoginDialogs(LanguagePreference language, System.Windows.Forms.Screen screen)
    {
        var assembly = typeof(App).Assembly;
        var localizer = Activator.CreateInstance(assembly.GetType("CodexProfileOverlay.Localizer")!, language)!;
        var placement = assembly.GetType("CodexProfileOverlay.DialogPlacement")!.GetMethod("CenterOnOwnerScreen")!;
        var owner = new Window { Width = 600, Height = 400, ShowInTaskbar = false };
        owner.Show();
        SetWindowPos(new System.Windows.Interop.WindowInteropHelper(owner).Handle, IntPtr.Zero,
            screen.WorkingArea.Left + 100, screen.WorkingArea.Top + 100, 0, 0, 0x15);
        try
        {
            foreach (bool native in new[] { false, true })
            {
                if (native) owner.WindowState = WindowState.Minimized;
                Exception? failure = null;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var prompt = Application.Current.Windows.Cast<Window>().Single(w => w.GetType().Name == "PromptDialog");
                    try
                    {
                        CheckVisible(prompt);
                        Check(System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(prompt).Handle).DeviceName == screen.DeviceName,
                            "Prompt must open on its owner's monitor");
                        Check(!IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(owner).Handle), "ShowDialog must disable the native owner while prompt remains reachable");
                        if (!native) Check(prompt.Owner == owner, "Managed prompt owner must be preserved");
                    }
                    catch (Exception e) { failure = e; }
                    finally { prompt.DialogResult = false; }
                };
                timer.Start();
                var method = assembly.GetType("CodexProfileOverlay.PromptDialog")!.GetMethod("Show", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)!;
                var result = method.Invoke(null, new object?[] { native ? null : owner,
                    native ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero,
                    "Prompt", "Profile", "", "OK", "Cancel" });
                if (failure is not null) throw failure;
                Check(result is null && IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(owner).Handle), "Cancel restores modal owner");
            }
            for (int i = 0; i < 2; i++)
            {
                using var token = new CancellationTokenSource();
                var progress = (Window)Activator.CreateInstance(assembly.GetType("CodexProfileOverlay.LoginProgressWindow")!, localizer, token)!;
                progress.Owner = owner;
                placement.Invoke(null, new object?[] { progress, owner, IntPtr.Zero });
                progress.Show(); progress.UpdateLayout();
                PumpDispatcher(progress);
                CheckVisible(progress);
                Check(System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(progress).Handle).DeviceName == screen.DeviceName,
                    "Login must open on its owner's monitor");
                var hwnd = new System.Windows.Interop.WindowInteropHelper(progress).Handle;
                SetWindowPos(hwnd, IntPtr.Zero, 30000, -30000, 0, 0, 0x15);
                SendMessage(hwnd, 0x007E, IntPtr.Zero, IntPtr.Zero);
                PumpDispatcher(progress);
                CheckVisible(progress);
                progress.Close();
                Check(token.IsCancellationRequested && !progress.IsVisible, "Closing progress cancels and removes the window");
            }
        }
        finally { owner.Close(); }
    }

    private static void PumpDispatcher(Window window)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectPixels { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectPixels rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private static void CheckVisible(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Check(IsWindowVisible(handle) && GetWindowRect(handle, out var _), "Dialog HWND must be visible");
        GetWindowRect(handle, out var rect);
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        Check(rect.Left >= area.Left && rect.Top >= area.Top && rect.Right <= area.Right && rect.Bottom <= area.Bottom,
            "Dialog must fit its physical monitor work area");
    }

    private static void CheckSettings(string temp, AppTheme theme, LanguagePreference language, bool localizationOnly = false)
    {
        var assembly = typeof(App).Assembly;
        var paths = new AppPaths(temp, temp);
        using var status = new ProfileStatusService(new ProfileStatusStore(paths.ProfileStatusFile), new UnavailableUsageProvider(), new SafeLogger(Path.Combine(temp, "logs")));
        using var updates = new UpdateService(Path.Combine(temp, Guid.NewGuid() + "-update.json"), new HttpClient(new UpdateFeed()), "1.1.0");
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
            p.ParameterType == typeof(Action<bool>) ? (Action<bool>)(_ => { }) :
            (Action)(() => { })).ToArray();
        var window = (Window)ctor.Invoke(args);
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
        if (localizationOnly)
        {
            foreach (var selected in new[] { LanguagePreference.English, LanguagePreference.Russian, LanguagePreference.Chinese, language })
            {
                var page = (UIElement)Invoke(window, "BuildLanguagePage")!;
                var host = (Grid)Field(window, "contentHost");
                host.Children.Clear(); host.Children.Add(page); window.UpdateLayout();
                var combo = Descendants(page).OfType<ComboBox>().Single();
                combo.SelectedValue = selected;
                Check(settings.Language == selected, "Language combo must update settings");
                Check((LanguagePreference)localizerType.GetProperty("Language")!.GetValue(localizer)! == selected,
                    "Language change must reach the localizer");
                Check(settings.Theme == theme, "Language changes must retain the theme");
            }
            window.GetType().GetMethod("CloseForShutdown")!.Invoke(window, null);
            return;
        }
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
        CheckUpdateCard(window, updates, language);
        var bar = new ScrollBar { Orientation = Orientation.Vertical, Maximum = 100, ViewportSize = 20 };
        bar.Style = (Style)appResource(typeof(ScrollBar));
        bar.Measure(new Size(10, 300)); bar.Arrange(new Rect(0, 0, 10, 300)); bar.ApplyTemplate();
        Check(bar.ActualHeight == 300 && bar.Template.FindName("PART_Track", bar) is Track, "Scrollbar must fill vertical track");
        window.GetType().GetMethod("CloseForShutdown")!.Invoke(window, null);
    }

    private sealed class UpdateFeed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            const string tag = "v1.2.0";
            byte[] payload = [1, 2, 3]; // Never executed.
            string path = request.RequestUri!.AbsolutePath;
            string text = path.EndsWith("latest") ? System.Text.Json.JsonSerializer.Serialize(new
            {
                tag_name = tag, html_url = UpdateService.RepositoryUrl + "/releases/tag/" + tag,
                published_at = "2026-10-12T00:00:00Z", body = "Test release", draft = false, prerelease = false,
                assets = new[] { UpdateService.InstallerName, "SHA256SUMS.txt" }.Select(name => new
                { name, browser_download_url = UpdateService.RepositoryUrl + "/releases/download/" + tag + "/" + name })
            }) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)) + "  " + UpdateService.InstallerName;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = path.EndsWith(".exe") ? new ByteArrayContent(payload) : new StringContent(text) });
        }
    }

    private static void CheckUpdateCard(Window window, UpdateService updates, LanguagePreference language)
    {
        string Text(string key) => LocalizationCatalog.Text(language, key);
        var host = (Grid)Field(window, "contentHost");
        void RenderCard()
        {
            host.Children.Clear();
            host.Children.Add((UIElement)Invoke(window, "BuildUpdateCard")!);
            window.UpdateLayout();
        }
        RenderCard();
        Check(Descendants(host).OfType<TextBlock>().Any(t => t.Text == Text("UpdatePortable")), "Portable update explanation must be visible");
        Check(!Descendants(host).OfType<CheckBox>().Any(), "Portable mode must not enable background downloads");
        window.GetType().GetField("installed", Private)!.SetValue(window, true); // Isolated UI only; never change registry.
        updates.ConfigureBackgroundDownloads(true, true);
        updates.CheckAndDownloadAsync(false).GetAwaiter().GetResult();
        try
        {
            RenderCard();
            Check(Descendants(host).OfType<CheckBox>().Single().IsChecked == true, "Installed auto-download defaults on");
            Check(Descendants(host).OfType<Button>().Any(b => Equals(b.Content, Text("UpdateRestart"))), "Ready package must offer confirmed installation");
            Check(Descendants(host).OfType<TextBlock>().Any(t => t.Text.Contains("v1.2.0")), "Ready version must be visible");
            updates.GetType().GetProperty("IsDownloading")!.SetValue(updates, true);
            updates.GetType().GetProperty("DownloadProgress")!.SetValue(updates, 42d);
            RenderCard();
            Check(Descendants(host).OfType<ProgressBar>().Single().Value == 42, "Background progress must be visible");
            Check(Descendants(host).OfType<TextBlock>().Any(t => t.Text.Contains("42%")), "Background percentage must be localized");
            Check(Descendants(host).OfType<Button>().Any(b => Equals(b.Content, Text("UpdateCancel"))), "Background download must allow cancellation");
            updates.GetType().GetProperty("IsDownloading")!.SetValue(updates, false);
        }
        finally { if (updates.ReadyInstallerPath is { } path) File.Delete(path); }
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
