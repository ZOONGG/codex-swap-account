using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Tests;

public sealed class HotkeyGestureTests
{
    [Fact]
    public void ToString_UsesHumanReadableLetters()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

        Assert.Equal("Ctrl + Alt + C", gesture.ToString());
    }

    [Fact]
    public void ToString_UsesHumanReadableDigitsAndFunctionKeys()
    {
        Assert.Equal("Ctrl + Alt + 1", new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31).ToString());
        Assert.Equal("Alt + Shift + F5", new HotkeyGesture(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x74).ToString());
    }
}
