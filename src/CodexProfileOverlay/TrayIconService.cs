using System.Drawing;
using System.Windows.Forms;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay;

internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu = new();
    private IReadOnlyList<ProfileInfo> profiles = [];
    private string? activeProfile;
    private bool overlayVisible;
    private bool startWithWindows;
    private bool disposed;

    public TrayIconService()
    {
        notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Codex Profile Overlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
        notifyIcon.MouseClick += OnMouseClick;
        notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
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
        profiles = newProfiles;
        activeProfile = newActiveProfile;
        RebuildMenu();
    }

    public void UpdateOverlayState(bool isVisible)
    {
        overlayVisible = isVisible;
        RebuildMenu();
    }

    public void UpdateStartWithWindows(bool isEnabled)
    {
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
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ToggleOverlayRequested?.Invoke();
        }
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            OpenCodexRequested?.Invoke();
        }
    }

    private void RebuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add("Open Codex", null, (_, _) => OpenCodexRequested?.Invoke());
        menu.Items.Add(overlayVisible ? "Hide switcher" : "Show switcher", null, (_, _) => ToggleOverlayRequested?.Invoke());

        var profilesMenu = new ToolStripMenuItem("Profiles");
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
            profilesMenu.DropDownItems.Add(new ToolStripMenuItem("No ready profiles") { Enabled = false });
        }

        menu.Items.Add(profilesMenu);
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        var startup = new ToolStripMenuItem("Start with Windows")
        {
            Checked = startWithWindows,
            CheckOnClick = true,
        };
        startup.CheckedChanged += (_, _) => StartWithWindowsChanged?.Invoke(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(32, 33, 36));
        using var accent = new SolidBrush(Color.FromArgb(169, 112, 255));
        using var border = new Pen(Color.FromArgb(92, 86, 112), 2);
        graphics.FillEllipse(background, 3, 3, 26, 26);
        graphics.DrawEllipse(border, 3, 3, 26, 26);
        graphics.FillEllipse(accent, 10, 9, 12, 12);
        graphics.FillRectangle(accent, 9, 21, 14, 3);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
