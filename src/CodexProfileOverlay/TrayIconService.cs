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
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
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
