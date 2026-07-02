using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class ActiveProfileStoreTests
{
    [Fact]
    public void WriteAndRead_RoundTripsProfileName()
    {
        using var temp = new TempDirectory();
        var store = new ActiveProfileStore(Path.Combine(temp.Path, "active-profile.txt"));

        store.Write("zonng");

        Assert.Equal("zonng", store.Read());
    }

    [Fact]
    public void Read_ReturnsNullForInvalidPersistedName()
    {
        using var temp = new TempDirectory();
        string file = Path.Combine(temp.Path, "active-profile.txt");
        File.WriteAllText(file, "..");
        var store = new ActiveProfileStore(file);

        Assert.Null(store.Read());
    }
}
