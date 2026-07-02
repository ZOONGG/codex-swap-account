using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;

namespace CodexProfileOverlay;

internal static class AppIcons
{
    private static readonly Uri IconResourceUri = new("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute);
    private static readonly Lazy<ImageSource> WindowIconSource = new(LoadWindowIcon);

    public static ImageSource WindowIcon => WindowIconSource.Value;

    public static Icon CreateNotifyIcon()
    {
        StreamResourceInfo? resource = Application.GetResourceStream(IconResourceUri);
        if (resource is null)
        {
            throw new InvalidOperationException("Application icon resource was not found.");
        }

        using Stream stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private static ImageSource LoadWindowIcon()
    {
        BitmapFrame frame = BitmapFrame.Create(IconResourceUri);
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        return frame;
    }
}
