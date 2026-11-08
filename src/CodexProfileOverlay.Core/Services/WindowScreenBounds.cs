namespace CodexProfileOverlay.Core.Services;

/// <summary>Dialog placement in physical desktop pixels, including negative monitor origins.</summary>
public static class WindowScreenBounds
{
    public static (int X, int Y, int Width, int Height) Place(int left, int top, int areaWidth, int areaHeight,
        int x, int y, int width, int height, bool center)
    {
        width = Math.Clamp(width, 1, Math.Max(1, areaWidth));
        height = Math.Clamp(height, 1, Math.Max(1, areaHeight));
        return (center ? left + (areaWidth - width) / 2 : Math.Clamp(x, left, left + areaWidth - width),
            center ? top + (areaHeight - height) / 2 : Math.Clamp(y, top, top + areaHeight - height), width, height);
    }
}
