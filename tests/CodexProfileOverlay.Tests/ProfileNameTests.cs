using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class ProfileNameTests
{
    [Theory]
    [InlineData("zonng")]
    [InlineData("grille")]
    [InlineData("ormazamoh")]
    [InlineData("work-profile")]
    public void IsValid_AcceptsSafeDirectoryNames(string value)
    {
        Assert.True(ProfileName.IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../escape")]
    [InlineData("escape\\name")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" trailing")]
    [InlineData("trailing ")]
    public void IsValid_RejectsUnsafeNames(string value)
    {
        Assert.False(ProfileName.IsValid(value));
    }
}
