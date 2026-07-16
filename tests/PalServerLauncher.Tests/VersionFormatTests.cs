using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class VersionFormatTests
{
    [Theory]
    [InlineData("v1.0.0.100427", "v1.0.0")]
    [InlineData("v1.0.0", "v1.0.0")]
    [InlineData("v0.3.4.12345", "v0.3.4")]
    [InlineData("1.2.3.4", "v1.2.3")]        // no leading 'v'
    [InlineData("V1.0.0.9", "v1.0.0")]        // uppercase V
    [InlineData("v2.1", "v2.1")]              // fewer than three segments passes through
    [InlineData("v1", "v1")]
    [InlineData("  v1.0.0.5  ", "v1.0.0")]    // trimmed
    public void ShortVersion_truncates_to_three_segments(string raw, string expected) =>
        Assert.Equal(expected, VersionFormat.ShortVersion(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("REST off")]
    [InlineData("unknown")]
    public void ShortVersion_is_null_for_non_versions(string? raw) =>
        Assert.Null(VersionFormat.ShortVersion(raw));

    [Fact]
    public void Label_shows_version_and_build_when_version_known() =>
        Assert.Equal("v1.0.1 (24181105)", VersionFormat.Label("v1.0.1", "24181105", "build {0}"));

    [Fact]
    public void Label_falls_back_to_build_only_when_no_version() =>
        Assert.Equal("build 24181105", VersionFormat.Label(null, "24181105", "build {0}"));

    [Fact]
    public void Label_falls_back_to_build_only_for_blank_version() =>
        Assert.Equal("build 24181105", VersionFormat.Label("", "24181105", "build {0}"));

    [Fact]
    public void Label_shows_placeholder_for_missing_build() =>
        Assert.Equal("build ?", VersionFormat.Label(null, null, "build {0}"));
}
