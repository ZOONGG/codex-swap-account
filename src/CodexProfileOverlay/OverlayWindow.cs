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

namespace CodexProfileOverlay;

internal sealed class OverlayWindow : Window
{
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(150);
    private readonly OverlaySettings settings;
    private readonly SafeLogger logger;
    private readonly StackPanel profilePanel = new() { Orientation = Orientation.Horizontal };
    private readonly Border notificationBorder;
    private readonly TextBlock notificationText;
    private readonly List<Button> profileButtons = [];
    private HwndSource? hwndSource;
    private IntPtr ownerHwnd;
    private bool isDragging;
    private Point dragOffset;

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

        notificationText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        notificationBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(47, 43, 58)),
            BorderBrush = FindBrush("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            Child = notificationText,
        };

        Content = BuildContent();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        SourceInitialized += OnSourceInitialized;
    }

    public Action<string>? OnSwitchProfile { get; set; }

    public Action? OnRefreshProfiles { get; set; }

    public Action? OnOpenProfilesFolder { get; set; }

    public Action? OnOpenApplicationDataFolder { get; set; }

    public Action? OnExit { get; set; }

    public Action<OverlaySettings>? OnSettingsChanged { get; set; }

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
            return;
        }

        if (!TryGetClientBounds(codexHwnd, out Rect clientBounds))
        {
            Hide();
            return;
        }

        settings.OffsetX = Clamp(settings.OffsetX, 0, Math.Max(0, clientBounds.Width - ActualWidth));
        settings.OffsetY = Clamp(settings.OffsetY, 0, Math.Max(0, clientBounds.Height - ActualHeight));
        Left = clientBounds.Left + settings.OffsetX;
        Top = clientBounds.Top + settings.OffsetY;

        if (!IsVisible)
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
            NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    public void SetProfiles(IReadOnlyList<ProfileInfo> profiles, string? activeProfile)
    {
        profilePanel.Children.Clear();
        profileButtons.Clear();

        foreach (ProfileInfo profile in profiles)
        {
            Button button = CreateProfileButton(profile.Name, string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase));
            profilePanel.Children.Add(button);
            profileButtons.Add(button);
        }
    }

    public void SetSwitching(bool isSwitching)
    {
        foreach (Button button in profileButtons)
        {
            button.IsEnabled = !isSwitching;
        }
    }

    public void ShowNotification(string message) => ShowTransientMessage(message, isError: false);

    public void ShowError(string message) => ShowTransientMessage(message, isError: true);

    private UIElement BuildContent()
    {
        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
        };

        var shell = new Border
        {
            Height = 46,
            Background = FindBrush("OverlayBackgroundBrush"),
            BorderBrush = FindBrush("OverlayBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Effect = null,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true,
            Content = profilePanel,
        };
        Grid.SetColumn(scrollViewer, 0);

        Button menuButton = CreateMenuButton();
        Grid.SetColumn(menuButton, 1);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(menuButton);
        shell.Child = grid;
        root.Children.Add(shell);
        root.Children.Add(notificationBorder);
        return root;
    }

    private Button CreateProfileButton(string profileName, bool isActive)
    {
        var text = new TextBlock
        {
            Text = profileName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = isActive ? FindBrush("StrongTextBrush") : FindBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 118,
        };

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = FindBrush("AccentBrush"),
            Visibility = isActive ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dot, text },
        };

        var underline = new Border
        {
            Height = 2,
            Background = FindBrush("AccentBrush"),
            CornerRadius = new CornerRadius(1),
            Opacity = isActive ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var content = new Grid();
        content.Children.Add(row);
        content.Children.Add(underline);

        var button = new Button
        {
            Content = content,
            MinWidth = 76,
            MaxWidth = 148,
            Height = 34,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(0),
            Background = isActive ? FindBrush("TabActiveBrush") : FindBrush("TabBackgroundBrush"),
            Cursor = Cursors.Hand,
            Tag = profileName,
            ToolTip = profileName,
        };

        button.Click += (_, _) => OnSwitchProfile?.Invoke(profileName);
        button.MouseEnter += (_, _) => AnimateBrush(button, isActive ? "TabActiveBrush" : "TabHoverBrush");
        button.MouseLeave += (_, _) => AnimateBrush(button, isActive ? "TabActiveBrush" : "TabBackgroundBrush");
        return button;
    }

    private Button CreateMenuButton()
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 8 2 L 8 14 M 2 8 L 14 8"),
            Stroke = FindBrush("StrongTextBrush"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        };

        var button = new Button
        {
            Content = path,
            Width = 34,
            Height = 34,
            BorderThickness = new Thickness(0),
            Background = FindBrush("TabBackgroundBrush"),
            Cursor = Cursors.Hand,
            ToolTip = "Menu",
        };

        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Refresh profiles", () => OnRefreshProfiles?.Invoke()));
        menu.Items.Add(CreateMenuItem("Open profiles folder", () => OnOpenProfilesFolder?.Invoke()));
        menu.Items.Add(CreateMenuItem("Open application data folder", () => OnOpenApplicationDataFolder?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit overlay", () => OnExit?.Invoke()));
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

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowTransientMessage(string message, bool isError)
    {
        notificationText.Text = message;
        notificationBorder.BorderBrush = isError ? Brushes.IndianRed : FindBrush("AccentBrush");
        notificationBorder.Visibility = Visibility.Visible;
        notificationBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(1, AnimationDuration));

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 6 : 3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var animation = new DoubleAnimation(0, AnimationDuration);
            animation.Completed += (_, _) => notificationBorder.Visibility = Visibility.Collapsed;
            notificationBorder.BeginAnimation(OpacityProperty, animation);
        };
        timer.Start();
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
        settings.OffsetX = Clamp(screenDip.X - clientBounds.Left - dragOffset.X, 0, Math.Max(0, clientBounds.Width - ActualWidth));
        settings.OffsetY = Clamp(screenDip.Y - clientBounds.Top - dragOffset.Y, 0, Math.Max(0, clientBounds.Height - ActualHeight));
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

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(Math.Max(value, minimum), maximum);
    }

    private static void AnimateBrush(Control control, string resourceKey)
    {
        if (Application.Current.FindResource(resourceKey) is not SolidColorBrush target)
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
