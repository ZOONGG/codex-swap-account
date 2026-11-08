using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexProfileOverlay;

internal sealed class LoginProgressWindow : Window
{
    public LoginProgressWindow(Localizer localizer, CancellationTokenSource cancellation, string title = "BrowserLogin", string message = "LoginWaiting")
    {
        Title = localizer[title];
        Icon = AppIcons.WindowIcon;
        Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("StrongTextBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = localizer[message], TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        var cancel = new System.Windows.Controls.Button { Content = localizer["Cancel"], Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120 };
        cancel.Click += (_, _) => { cancel.IsEnabled = false; cancellation.Cancel(); };
        panel.Children.Add(cancel);
        Content = panel;
        Closing += (_, _) => cancellation.Cancel();
        WindowCaptionThemeService.ApplyCurrent(this);
    }
}
