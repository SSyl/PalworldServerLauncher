using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class AppInfoTests
{
    [Theory]
    // A source build stamps the commit onto the informational version, which nobody wants in a log banner.
    [InlineData("1.1.0+9a3f1c2e4b", null, "1.1.0")]
    [InlineData("1.1.0", null, "1.1.0")]
    [InlineData("2.0.0-beta.1+abc", null, "2.0.0-beta.1")]
    public void Resolve_strips_build_metadata(string info, string? assembly, string expected) =>
        Assert.Equal(expected, AppInfo.Resolve(info, assembly));

    [Theory]
    [InlineData(null, "1.1.0", "1.1.0")]
    [InlineData("", "1.1.0", "1.1.0")]
    [InlineData("   ", "1.1.0", "1.1.0")]
    public void Resolve_falls_back_to_the_assembly_version(string? info, string assembly, string expected) =>
        Assert.Equal(expected, AppInfo.Resolve(info, assembly));

    [Fact]
    public void Resolve_returns_a_placeholder_when_neither_is_available() =>
        Assert.Equal("?", AppInfo.Resolve(null, null));

    [Fact]
    public void Version_is_populated_from_the_running_assembly()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.DoesNotContain('+', AppInfo.Version);
    }
}
