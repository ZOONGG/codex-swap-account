using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace CodexProfileOverlay;

internal sealed class OverlayWindow : Window
{
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(150);
    private readonly OverlaySettings settings;
    private readonly SafeLogger logger;
    private readonly OverlayLayoutService layoutService = new();
    private readonly Border shell = new();
    private readonly Popup compactPopup = new() { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom };
    private readonly List<Button> profileButtons = [];
    private HwndSource? hwndSource;
    private IReadOnlyList<ProfileInfo> profiles = [];
    private string? activeProfile;
    private IntPtr ownerHwnd;
    private bool isDragging;
    private bool isSwitching;
    private Point dragOffset;
    private OverlayDisplayMode resolvedAutoMode = OverlayDisplayMode.Expanded;
    private OverlayDisplayMode currentMode = OverlayDisplayMode.Expanded;

    public OverlayWindow(OverlaySettings settings, SafeLogger logger)
    {
        this.settings = settings;
        this.logger = logger;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ApplyScale();

        Content = shell;
        RebuildContent();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                compactPopup.IsOpen = false;
            }
        };
    }

    public Action<string>? OnSwitchProfile { get; set; }

    public Action? OnRefreshProfiles { get; set; }

    public Action? OnOpenProfilesFolder { get; set; }

    public Action? OnOpenApplicationDataFolder { get; set; }

    public Action? OnOpenSettings { get; set; }

    public Action? OnManageProfiles { get; set; }

    public Action? OnAddProfile { get; set; }

    public Action? OnHideOverlay { get; set; }

    public Action? OnExit { get; set; }

    public Action<OverlaySettings>? OnSettingsChanged { get; set; }

    public Localizer? Localizer { get; set; }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public bool AllowAutoShow { get; set; } = true;

    public void AttachTo(IntPtr codexHwnd)
    {
        ownerHwnd = codexHwnd;
        var helper = new WindowInteropHelper(this);
        _ = helper.EnsureHandle();
        helper.Owner = codexHwnd;
        ApplyToolWindowStyle(helper.Handle);
    }

    public void UpdatePlacement(IntPtr codexHwnd)
    {
        if (!NativeMethods.IsWindowVisible(codexHwnd) || NativeMethods.IsIconic(codexHwnd))
        {
            Hide();
            compactPopup.IsOpen = false;
            return;
        }

        if (!TryGetClientBounds(codexHwnd, out Rect clientBounds))
        {
            Hide();
            compactPopup.IsOpen = false;
            return;
        }

        OverlayDisplayMode nextMode = layoutService.ResolveDisplayMode(settings.DisplayMode, clientBounds.Width, resolvedAutoMode);
        if (settings.DisplayMode == OverlayDisplayMode.Auto)
        {
            resolvedAutoMode = nextMode;
        }

        if (nextMode != currentMode)
        {
            currentMode = nextMode;
            RebuildContent();
        }

        UpdateLayout();
        var placement = layoutService.CalculatePlacement(
            settings.PositionPreset,
            clientBounds.Width,
            clientBounds.Height,
            ActualWidth,
            ActualHeight,
            settings.OffsetX,
            settings.OffsetY);

        if (settings.PositionPreset == PositionPreset.Custom)
        {
            settings.OffsetX = placement.OffsetX;
            settings.OffsetY = placement.OffsetY;
        }

        if (!double.IsFinite(placement.OffsetX) || !double.IsFinite(placement.OffsetY))
        {
            return;
        }

        Left = clientBounds.Left + placement.OffsetX;
        Top = clientBounds.Top + placement.OffsetY;

        if (!IsVisible && settings.ShowAutomaticallyWhenCodexOpens && AllowAutoShow)
        {
            Show();
        }

        var handle = new WindowInteropHelper(this).Handle;
        _ = NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    public void SetProfiles(IReadOnlyList<ProfileInfo> newProfiles, string? newActiveProfile)
    {
        profiles = newProfiles;
        activeProfile = newActiveProfile;
        RebuildContent();
    }

    public void SetSwitching(bool switching)
    {
        isSwitching = switching;
        compactPopup.IsOpen = false;
        foreach (Button button in profileButtons)
        {
            button.IsEnabled = !switching;
        }
    }

    public void ShowNotification(string message) => ToastWindow.Show(ownerHwnd, message, isError: false);

    public void ShowError(string message) => ToastWindow.Show(ownerHwnd, message, isError: true);

    public void ApplySettings()
    {
        ApplyScale();
        RebuildContent();
    }

    private void RebuildContent()
    {
        Width = LogicalWidth * SanitizedScale;
        shell.Height = double.NaN;
        shell.MinHeight = currentMode == OverlayDisplayMode.Compact ? 44 : 44;
        shell.Background = FindBrush("OverlayBackgroundBrush");
        shell.BorderBrush = FindBrush("OverlayBorderBrush");
        shell.BorderThickness = new Thickness(1);
        shell.CornerRadius = new CornerRadius(8);
        shell.Padding = currentMode == OverlayDisplayMode.Compact ? new Thickness(6, 5, 6, 6) : new Thickness(5, 5, 5, 6);
        shell.Child = currentMode == OverlayDisplayMode.Compact ? BuildCompactContent() : BuildExpandedContent();
        compactPopup.Child = BuildCompactPopup();
    }

    private UIElement BuildCompactContent()
    {
        ProfileInfo? active = profiles.FirstOrDefault(profile => string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault();

        var button = new Button
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            IsEnabled = !isSwitching,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 0,
        };
        button.Click += (_, _) =>
        {
            compactPopup.PlacementTarget = button;
            compactPopup.IsOpen = true;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = CreateAvatar(active?.Initials ?? "--", active?.Accent ?? "#A970FF");
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var name = new TextBlock
        {
            Text = isSwitching ? Localizer?["Switching"] ?? "Switching..." : active?.DisplayName ?? Localizer?["NoReadyProfiles"] ?? "No ready profiles",
            Foreground = FindBrush("StrongTextBrush"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = FindBrush("AccentBrush"), Margin = new Thickness(0, 0, 9, 0) });
        right.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2 4 L 7 9 L 12 4"),
            Stroke = FindBrush("MutedTextBrush"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 14,
            Height = 12,
            Stretch = Stretch.Uniform,
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        button.Content = grid;
        button.MouseEnter += (_, _) => shell.Background = FindBrush("TabHoverBrush");
        button.MouseLeave += (_, _) => shell.Background = FindBrush("OverlayBackgroundBrush");
        return button;
    }

    private UIElement BuildExpandedContent()
    {
        profileButtons.Clear();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (profiles.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Localizer?["NoReadyProfiles"] ?? "No ready profiles",
                Foreground = FindBrush("MutedTextBrush"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 14, 0),
            });
        }

        foreach (ProfileInfo profile in profiles)
        {
            Button button = CreateProfileButton(profile, string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase));
            panel.Children.Add(button);
            profileButtons.Add(button);
        }

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true,
            Content = panel,
            Height = 32,
        };
        Grid.SetColumn(scrollViewer, 0);

        Button menuButton = CreateMenuButton();
        Grid.SetColumn(menuButton, 1);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(menuButton);
        return grid;
    }

    private UIElement BuildCompactPopup()
    {
        var border = new Border
        {
            Width = 292,
            Background = FindBrush("OverlayBackgroundBrush"),
            BorderBrush = FindBrush("OverlayBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
        };

        var panel = new StackPanel();
        foreach (ProfileInfo profile in profiles)
        {
            bool isActive = string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase);
            Button item = CreatePopupButton(profile.DisplayName + (isActive ? "  " + (Localizer?["Active"] ?? "Active") : string.Empty), isActive ? "M 2 7 L 6 11 L 14 3" : null);
            item.IsEnabled = !isActive && !isSwitching;
            string name = profile.Name;
            item.Click += (_, _) =>
            {
                compactPopup.IsOpen = false;
                OnSwitchProfile?.Invoke(name);
            };
            panel.Children.Add(item);
        }

        panel.Children.Add(new Separator { Margin = new Thickness(2, 5, 2, 5) });
        panel.Children.Add(CreatePopupCommand(Localizer?["AddProfile"] ?? "Add profile", OnAddProfile));
        panel.Children.Add(CreatePopupCommand(Localizer?["ManageProfiles"] ?? "Manage profiles", OnManageProfiles));
        panel.Children.Add(CreatePopupCommand(Localizer?["Settings"] ?? "Settings", OnOpenSettings));
        panel.Children.Add(CreatePopupCommand(Localizer?["HideSwitcher"] ?? "Hide switcher", OnHideOverlay));
        border.Child = panel;
        return border;
    }

    private Button CreateProfileButton(ProfileInfo profile, bool isActive)
    {
        var avatar = CreateAvatar(profile.Initials, profile.Accent, 22);
        avatar.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
        avatar.BorderBrush = isActive ? FindBrush("StrongTextBrush") : Brushes.Transparent;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(avatar);
        row.Children.Add(new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 13.5,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = isActive ? FindBrush("StrongTextBrush") : FindBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 112,
        });

        var content = new Grid();
        if (isActive)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new Border
            {
                Width = 3,
                Height = 20,
                Background = FindBrush("AccentBrush"),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(row, 1);
        }

        content.Children.Add(row);

        var button = new Button
        {
            Content = content,
            MinWidth = 108,
            MaxWidth = 176,
            Height = 32,
            MinHeight = 0,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = isActive ? new Thickness(7, 0, 10, 0) : new Thickness(9, 0, 9, 0),
            BorderThickness = isActive ? new Thickness(1) : new Thickness(0),
            BorderBrush = isActive ? FindBrush("AccentHoverBrush") : Brushes.Transparent,
            Background = isActive ? FindBrush("TabActiveBrush") : FindBrush("TabBackgroundBrush"),
            Cursor = isActive ? Cursors.Arrow : Cursors.Hand,
            Tag = profile.Name,
            ToolTip = profile.DisplayName,
            IsEnabled = !isSwitching,
        };

        button.Click += (_, _) =>
        {
            if (!isActive)
            {
                OnSwitchProfile?.Invoke(profile.Name);
            }
        };
        button.MouseEnter += (_, _) => AnimateBrush(button, isActive ? "TabActiveBrush" : "TabHoverBrush");
        button.MouseLeave += (_, _) => AnimateBrush(button, isActive ? "TabActiveBrush" : "TabBackgroundBrush");
        return button;
    }

    private Button CreateMenuButton()
    {
        var plus = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 8 2 L 8 14 M 2 8 L 14 8"),
            Stroke = FindBrush("StrongTextBrush"),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true,
        };

        var button = new Button
        {
            Content = plus,
            Width = 34,
            Height = 32,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = FindBrush("TabBackgroundBrush"),
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = Localizer?["AddProfile"] ?? "Add profile",
        };

        var menu = new ContextMenu
        {
            Background = FindBrush("Surface2Brush"),
            BorderBrush = FindBrush("BorderBrush"),
            Foreground = FindBrush("StrongTextBrush"),
            Padding = new Thickness(5),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        menu.Items.Add(CreateMenuItem(Localizer?["AddProfile"] ?? "Add profile", () => OnAddProfile?.Invoke()));
        menu.Items.Add(CreateMenuItem(Localizer?["ManageProfiles"] ?? "Manage profiles", () => OnManageProfiles?.Invoke()));
        menu.Items.Add(CreateMenuItem(Localizer?["Settings"] ?? "Settings", () => OnOpenSettings?.Invoke()));
        menu.Items.Add(CreateMenuItem(Localizer?["HideSwitcher"] ?? "Hide switcher", () => OnHideOverlay?.Invoke()));
        button.ContextMenu = menu;
        button.Click += (_, _) =>
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        };
        button.MouseEnter += (_, _) => AnimateBrush(button, "TabHoverBrush");
        button.MouseLeave += (_, _) => AnimateBrush(button, "TabBackgroundBrush");
        return button;
    }

    private Button CreatePopupCommand(string text, Action? action)
    {
        Button button = CreatePopupButton(text, null);
        button.Click += (_, _) =>
        {
            compactPopup.IsOpen = false;
            action?.Invoke();
        };
        return button;
    }

    private Button CreatePopupButton(string text, string? iconGeometry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (iconGeometry is not null)
        {
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(iconGeometry),
                Stroke = FindBrush("AccentBrush"),
                StrokeThickness = 1.8,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var label = new TextBlock
        {
            Text = text,
            Foreground = FindBrush("StrongTextBrush"),
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        return new Button
        {
            Content = grid,
            Height = 38,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = FindBrush("Surface2Brush"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0),
            Cursor = Cursors.Hand,
        };
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private Border CreateAvatar(string initials, string accent, double size = 24)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(accent)!;
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = brush,
            Child = new TextBlock
            {
                Text = initials,
                Foreground = Brushes.White,
                FontSize = size <= 20 ? 9 : 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        ApplyToolWindowStyle(new WindowInteropHelper(this).Handle);
    }

    private static void ApplyToolWindowStyle(IntPtr handle)
    {
        long exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        exStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        exStyle &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)exStyle);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            return;
        }

        settings.PositionPreset = PositionPreset.Custom;
        isDragging = true;
        dragOffset = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging || ownerHwnd == IntPtr.Zero || !TryGetClientBounds(ownerHwnd, out Rect clientBounds))
        {
            return;
        }

        Point screenPoint = PointToScreen(e.GetPosition(this));
        Point screenDip = DeviceToDip(screenPoint);
        settings.OffsetX = OverlayLayoutService.Clamp(screenDip.X - clientBounds.Left - dragOffset.X, 0, Math.Max(0, clientBounds.Width - ActualWidth));
        settings.OffsetY = OverlayLayoutService.Clamp(screenDip.Y - clientBounds.Top - dragOffset.Y, 0, Math.Max(0, clientBounds.Height - ActualHeight));
        Left = clientBounds.Left + settings.OffsetX;
        Top = clientBounds.Top + settings.OffsetY;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging)
        {
            return;
        }

        isDragging = false;
        ReleaseMouseCapture();
        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try
        {
            OnSettingsChanged?.Invoke(settings);
        }
        catch (Exception exception)
        {
            logger.Error("Failed to save overlay settings.", exception);
        }
    }

    private bool TryGetClientBounds(IntPtr hwnd, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!NativeMethods.GetClientRect(hwnd, out NativeRect clientRect))
        {
            return false;
        }

        var topLeft = new NativePoint { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref topLeft))
        {
            return false;
        }

        Point origin = DeviceToDip(new Point(topLeft.X, topLeft.Y));
        Point size = DeviceToDip(new Point(clientRect.Width, clientRect.Height), treatAsSize: true);
        bounds = new Rect(origin.X, origin.Y, size.X, size.Y);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private Point DeviceToDip(Point point, bool treatAsSize = false)
    {
        if (hwndSource?.CompositionTarget is null)
        {
            return point;
        }

        Matrix transform = hwndSource.CompositionTarget.TransformFromDevice;
        return treatAsSize
            ? new Point(point.X * transform.M11, point.Y * transform.M22)
            : transform.Transform(point);
    }

    private SolidColorBrush FindBrush(string resourceKey)
    {
        return ((SolidColorBrush)Application.Current.FindResource(resourceKey)).Clone();
    }

    private double SanitizedScale => double.IsFinite(settings.Scale) ? Math.Clamp(settings.Scale, 0.8, 1.4) : 1;

    private double LogicalWidth => currentMode == OverlayDisplayMode.Compact ? 286 : 560;

    private void ApplyScale()
    {
        double scale = SanitizedScale;
        shell.LayoutTransform = Math.Abs(scale - 1) < 0.001
            ? Transform.Identity
            : new ScaleTransform(scale, scale);
    }

    private void AnimateBrush(Control control, string resourceKey)
    {
        if (!settings.AnimationsEnabled || Application.Current.FindResource(resourceKey) is not SolidColorBrush target)
        {
            return;
        }

        if (control.Background is not SolidColorBrush current)
        {
            current = new SolidColorBrush(Colors.Transparent);
            control.Background = current;
        }

        current.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target.Color, AnimationDuration));
    }
}
