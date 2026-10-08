using System.Drawing;
using System.Windows.Forms;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class TrayIconService : IDisposable
{
    private readonly Icon icon;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly Localizer localizer;
    private readonly TrayClickCoordinator clickCoordinator = new();
    private readonly System.Windows.Forms.Timer singleClickTimer = new() { Interval = 60 };
    private IReadOnlyList<ProfileInfo> profiles = [];
    private string? activeProfile;
    private bool overlayVisible;
    private bool startWithWindows;
    private bool disposed;
    private Icon? usageIcon;
    private int? displayedPercent;
    private UsageSnapshot? latestSnapshot;
    private OverlaySettings? latestSettings;
    private readonly System.Windows.Forms.Timer usageTimer = new() { Interval = 30000 };

    public TrayIconService(Localizer localizer)
    {
        this.localizer = localizer;
        icon = AppIcons.CreateNotifyIcon();
        notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Codex Profile Overlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
        notifyIcon.MouseClick += OnMouseClick;
        notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
        singleClickTimer.Tick += (_, _) =>
        {
            if (clickCoordinator.TryConsumeDueSingleClick(DateTimeOffset.UtcNow))
            {
                ToggleOverlayRequested?.Invoke();
            }
        };
        usageTimer.Tick += (_, _) => { if (latestSettings is not null) UpdateUsage(latestSnapshot, latestSettings); };
        usageTimer.Start();
        localizer.LanguageChanged += RebuildMenu;
        RebuildMenu();
    }

    public event Action? ToggleOverlayRequested;

    public event Action? OpenCodexRequested;

    public event Action? SettingsRequested;

    public event Action<bool>? StartWithWindowsChanged;

    public event Action<string>? ProfileSelected;

    public event Action? ExitRequested;

    public void UpdateProfiles(IReadOnlyList<ProfileInfo> newProfiles, string? newActiveProfile)
    {
        if (ReferenceEquals(profiles, newProfiles) && string.Equals(activeProfile, newActiveProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        profiles = newProfiles;
        activeProfile = newActiveProfile;
        RebuildMenu();
    }

    public void UpdateOverlayState(bool isVisible)
    {
        if (overlayVisible == isVisible)
        {
            return;
        }

        overlayVisible = isVisible;
        RebuildMenu();
    }

    public void UpdateStartWithWindows(bool isEnabled)
    {
        if (startWithWindows == isEnabled)
        {
            return;
        }

        startWithWindows = isEnabled;
        RebuildMenu();
    }

    public void UpdateUsage(UsageSnapshot? snapshot, OverlaySettings settings)
    {
        latestSnapshot = snapshot;
        latestSettings = settings;
        int? percent = UsageDisplayFormatter.TrayPercent(snapshot, settings.TrayUsageMode, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(settings.StaleDataThresholdMinutes));
        if (percent == displayedPercent) return;
        displayedPercent = percent;
        Icon? previous = usageIcon;
        usageIcon = null;
        if (percent is { } value)
        {
            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(28, 32, 39));
            using var font = new Font("Segoe UI", value == 100 ? 12 : 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(value.ToString(), font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
            IntPtr handle = bitmap.GetHicon();
            try { using var borrowed = Icon.FromHandle(handle); usageIcon = (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
        }
        notifyIcon.Icon = usageIcon ?? icon;
        notifyIcon.Text = percent is null ? "Codex Profile Overlay" : $"{percent}% {localizer["UsageRemaining"]}";
        previous?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void ShowBalloon(string title, string message)
    {
        notifyIcon.BalloonTipTitle = title;
        notifyIcon.BalloonTipText = message;
        notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        localizer.LanguageChanged -= RebuildMenu;
        singleClickTimer.Stop();
        singleClickTimer.Dispose();
        usageTimer.Dispose();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        usageIcon?.Dispose();
        icon.Dispose();
        menu.Dispose();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            clickCoordinator.RegisterLeftClick(DateTimeOffset.UtcNow);
            singleClickTimer.Start();
        }
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            clickCoordinator.RegisterLeftDoubleClick();
            singleClickTimer.Stop();
            OpenCodexRequested?.Invoke();
        }
    }

    private void RebuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(localizer["OpenCodex"], null, (_, _) => OpenCodexRequested?.Invoke());
        menu.Items.Add(overlayVisible ? localizer["HideSwitcher"] : localizer["ShowSwitcher"], null, (_, _) => ToggleOverlayRequested?.Invoke());

        var profilesMenu = new ToolStripMenuItem(localizer["Profiles"]);
        foreach (ProfileInfo profile in profiles)
        {
            var item = new ToolStripMenuItem(profile.DisplayName)
            {
                Checked = string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase),
                Enabled = !string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase),
                ToolTipText = profile.Name,
            };
            string profileName = profile.Name;
            item.Click += (_, _) => ProfileSelected?.Invoke(profileName);
            profilesMenu.DropDownItems.Add(item);
        }

        if (profilesMenu.DropDownItems.Count == 0)
        {
            profilesMenu.DropDownItems.Add(new ToolStripMenuItem(localizer["NoReadyProfiles"]) { Enabled = false });
        }

        menu.Items.Add(profilesMenu);
        menu.Items.Add(localizer["Settings"], null, (_, _) => SettingsRequested?.Invoke());
        var startup = new ToolStripMenuItem(localizer["StartWithWindows"])
        {
            Checked = startWithWindows,
            CheckOnClick = true,
        };
        startup.CheckedChanged += (_, _) => StartWithWindowsChanged?.Invoke(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(localizer["Exit"], null, (_, _) => ExitRequested?.Invoke());
    }

}
