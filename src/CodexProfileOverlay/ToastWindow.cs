using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class ToastWindow : Window
{
    private ToastWindow(string message, bool isError)
    {
        Width = 360;
        Height = 42;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = false;
        Opacity = 0;

        Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("OverlayBackgroundBrush"),
            BorderBrush = isError ? Brushes.IndianRed : (Brush)Application.Current.FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = new TextBlock
            {
                Text = message,
                Foreground = (Brush)Application.Current.FindResource("StrongTextBrush"),
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static void Show(IntPtr owner, string message, bool isError)
    {
        var toast = new ToastWindow(message, isError);
        var helper = new WindowInteropHelper(toast);
        _ = helper.EnsureHandle();
        if (owner != IntPtr.Zero)
        {
            helper.Owner = owner;
            if (TryGetClientBounds(toast, owner, out Rect bounds))
            {
                toast.Left = bounds.Left + (bounds.Width - toast.Width) / 2;
                toast.Top = bounds.Bottom - 86;
            }
        }

        ApplyToolWindowStyle(helper.Handle);
        toast.Show();
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 6 : 3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) => toast.Close();
            toast.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private static bool TryGetClientBounds(Window window, IntPtr hwnd, out Rect bounds)
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

        HwndSource? source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            bounds = new Rect(topLeft.X, topLeft.Y, clientRect.Width, clientRect.Height);
            return true;
        }

        Matrix transform = source.CompositionTarget.TransformFromDevice;
        Point origin = transform.Transform(new Point(topLeft.X, topLeft.Y));
        bounds = new Rect(origin.X, origin.Y, clientRect.Width * transform.M11, clientRect.Height * transform.M22);
        return true;
    }

    private static void ApplyToolWindowStyle(IntPtr handle)
    {
        long exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        exStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        exStyle &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)exStyle);
    }
}
