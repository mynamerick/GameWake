using GameWake.App.Services;

namespace GameWake.Tests.App;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.0.0", "v1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0+abc", "v1.0.0", 0)]
    [InlineData("1.0.0-dev", "1.0.0", 0)]
    public void Compare_strips_prefixes_and_orders_releases(string current, string latest, int expectedSign)
    {
        var compared = UpdateChecker.Compare(current, latest);
        Assert.Equal(expectedSign, Math.Sign(compared));
    }

    [Fact]
    public void Normalize_drops_v_prefix_and_build_metadata()
    {
        Assert.Equal("1.0.0", UpdateChecker.Normalize("v1.0.0"));
        Assert.Equal("1.0.0", UpdateChecker.Normalize("1.0.0+deadbeef"));
    }
}
