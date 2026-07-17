using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class MemoryFormatTests
{
    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(512L * 1024 * 1024, "512 MB")]
    [InlineData(1023L * 1024 * 1024, "1023 MB")]      // just under the GB threshold stays MB
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]      // exactly 1 GB flips to GB
    [InlineData(1536L * 1024 * 1024, "1.50 GB")]
    [InlineData(6L * 1024 * 1024 * 1024, "6.00 GB")]
    public void Format_reads_MB_below_a_GB_and_GB_at_or_above(long bytes, string expected) =>
        Assert.Equal(expected, MemoryFormat.Format(bytes));

    [Fact]
    public void Format_rounds_MB_to_whole_numbers() =>
        Assert.Equal("512 MB", MemoryFormat.Format(512L * 1024 * 1024 + 300 * 1024)); // 512.29 MB -> "512 MB"

    [Fact]
    public void Format_uses_a_dot_decimal_regardless_of_culture() =>
        Assert.Contains(".", MemoryFormat.Format(1536L * 1024 * 1024));

    [Fact]
    public void Format_treats_negative_as_zero() =>
        Assert.Equal("0 MB", MemoryFormat.Format(-1));
}
