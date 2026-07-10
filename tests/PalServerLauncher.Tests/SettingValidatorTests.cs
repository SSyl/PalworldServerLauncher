using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class SettingValidatorTests
{
    [Theory]
    [InlineData(SettingType.Int, '5', true)]
    [InlineData(SettingType.Int, '.', false)]
    [InlineData(SettingType.Int, 'a', false)]
    [InlineData(SettingType.Float, '.', true)]
    [InlineData(SettingType.Float, '5', true)]
    [InlineData(SettingType.Float, 'a', false)]
    [InlineData(SettingType.Text, 'a', true)]
    [InlineData(SettingType.Text, '"', false)]
    [InlineData(SettingType.Text, '\\', false)]
    public void IsCharAllowed_gates_by_type(SettingType type, char c, bool expected) =>
        Assert.Equal(expected, SettingValidator.IsCharAllowed(type, c));

    [Fact]
    public void Float_rejects_non_numeric_text()
    {
        var (ok, reason) = SettingValidator.Validate(SettingType.Float, "Two point five");
        Assert.False(ok);
        Assert.Equal("a number", reason);
    }

    [Fact]
    public void Float_rejects_malformed_double_dot()
    {
        var (ok, _) = SettingValidator.Validate(SettingType.Float, "1..5");
        Assert.False(ok);
    }

    [Fact]
    public void Int_range_enforced()
    {
        Assert.False(SettingValidator.Validate(SettingType.Int, "70000", min: 1, max: 65535).Ok);
        Assert.False(SettingValidator.Validate(SettingType.Int, "0", min: 1, max: 65535).Ok);
        Assert.True(SettingValidator.Validate(SettingType.Int, "8211", min: 1, max: 65535).Ok);
    }

    [Fact]
    public void Range_reason_reads_naturally()
    {
        Assert.Equal("a whole number between 1 and 65535",
            SettingValidator.Validate(SettingType.Int, "70000", min: 1, max: 65535).Reason);
        Assert.Equal("a number 0 or greater",
            SettingValidator.Validate(SettingType.Float, "-3", min: 0).Reason);
    }

    [Fact]
    public void Text_rejects_quotes_and_backslashes_but_allows_commas_and_parens()
    {
        Assert.False(SettingValidator.Validate(SettingType.Text, "a\"b").Ok);
        Assert.False(SettingValidator.Validate(SettingType.Text, "a\\b").Ok);
        Assert.True(SettingValidator.Validate(SettingType.Text, "My, Server (EU)").Ok);
    }

    [Fact]
    public void Empty_is_allowed_unless_required()
    {
        Assert.True(SettingValidator.Validate(SettingType.Text, "").Ok);
        Assert.True(SettingValidator.Validate(SettingType.Int, "  ").Ok);
        Assert.False(SettingValidator.Validate(SettingType.Int, "", required: true).Ok);
    }

    [Fact]
    public void Bool_and_enum_are_always_valid()
    {
        Assert.True(SettingValidator.Validate(SettingType.Bool, "whatever").Ok);
        Assert.True(SettingValidator.Validate(SettingType.Enum, "None").Ok);
    }

    [Theory]
    [InlineData('5', true)]
    [InlineData('.', true)]
    [InlineData('\'', false)]
    [InlineData('(', false)]
    [InlineData('[', false)]
    [InlineData('a', false)]
    public void IpAddress_only_allows_digits_and_dots(char c, bool expected) =>
        Assert.Equal(expected, SettingValidator.IsCharAllowed(SettingType.IpAddress, c));

    [Theory]
    [InlineData("203.0.113.5", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("", true)]           // blank = auto-detect
    [InlineData("1.2.3", false)]     // too few octets
    [InlineData("1.2.3.4.5", false)] // too many octets
    [InlineData("256.0.0.1", false)] // octet out of range
    [InlineData("1..2.3", false)]    // empty octet
    public void IpAddress_validates_ipv4_or_blank(string text, bool ok) =>
        Assert.Equal(ok, SettingValidator.Validate(SettingType.IpAddress, text).Ok);

    // --- ValuesEqual: typed comparison so untouched non-canonical values aren't rewritten on Save ---

    [Theory]
    [InlineData(SettingType.Bool, "False", "false", true)]   // checkbox canonical vs hand-edited lowercase
    [InlineData(SettingType.Bool, "True", "true", true)]
    [InlineData(SettingType.Bool, "True", "1", true)]        // 1 counts as true (matches the ini writer)
    [InlineData(SettingType.Bool, "True", "False", false)]   // genuinely different
    [InlineData(SettingType.Float, "1.0", "1.000000", true)] // Palworld's fixed 6-decimal form
    [InlineData(SettingType.Float, "1.5", "1.000000", false)]
    [InlineData(SettingType.Int, "5", "05", true)]           // leading zero
    [InlineData(SettingType.Int, "5", "6", false)]
    [InlineData(SettingType.Enum, "All", "all", true)]       // enum casing
    [InlineData(SettingType.Enum, "All", "None", false)]
    [InlineData(SettingType.Text, "My Server", "my server", false)] // text formatting is meaningful
    [InlineData(SettingType.Text, "Name", "Name", true)]
    public void ValuesEqual_compares_by_type(SettingType type, string a, string b, bool expected) =>
        Assert.Equal(expected, SettingValidator.ValuesEqual(type, a, b));

    [Fact]
    public void ValuesEqual_falls_back_to_literal_when_numeric_does_not_parse()
    {
        // A malformed original must not be silently treated as equal to a valid new value.
        Assert.False(SettingValidator.ValuesEqual(SettingType.Int, "5", "abc"));
        Assert.True(SettingValidator.ValuesEqual(SettingType.Int, "abc", "abc"));
    }

    [Theory]
    [InlineData("(Steam,Xbox,PS5,Mac)", true)]           // a normal tuple
    [InlineData("(\"PALBOX\",\"RepairBench\")", true)]   // quoted-ID list
    [InlineData("", true)]                                // empty is fine (e.g. DenyTechnologyList=)
    [InlineData("Steam", true)]                           // bare scalar
    [InlineData("(Steam,Xbox", false)]                   // unbalanced parenthesis
    [InlineData("Steam,Xbox", false)]                    // top-level comma would split the value
    [InlineData("(Steam))", false)]                      // parenthesis underflow
    [InlineData("(\"a)", false)]                         // unbalanced quote
    public void Validate_raw_flags_malformed_tuples(string value, bool ok) =>
        Assert.Equal(ok, SettingValidator.Validate(SettingType.Raw, value).Ok);
}
