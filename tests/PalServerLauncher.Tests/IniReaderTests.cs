using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class IniReaderTests
{
    // A representative single-line OptionSettings blob (trimmed subset of the real ~90 keys).
    private const string SampleIni = """
        [/Script/Pal.PalGameWorldSettings]
        OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ServerName="Syl's Server, EU",ServerDescription="",AdminPassword="p=ss,word",ServerPassword="",PublicPort=8211,PublicIP="",RCONEnabled=False,RCONPort=25575,bUseAuth=True,RESTAPIEnabled=True,RESTAPIPort=8212)
        """;

    [Fact]
    public void Parse_extracts_core_keys()
    {
        var s = IniReader.Parse(SampleIni);

        Assert.Equal(8212, s.RestApiPort);
        Assert.Equal(8211, s.PublicPort);
        Assert.True(s.RestApiEnabled);
        Assert.Equal(25575, s.RconPort);
        Assert.False(s.RconEnabled);
    }

    [Fact]
    public void Parse_reads_rcon_enabled_and_port()
    {
        var s = IniReader.Parse("""OptionSettings=(RCONEnabled=True,RCONPort=9999)""");

        Assert.True(s.RconEnabled);
        Assert.Equal(9999, s.RconPort);
    }

    [Fact]
    public void Parse_handles_commas_and_equals_inside_quoted_values()
    {
        var s = IniReader.Parse(SampleIni);

        // Password contains both a comma and an equals sign inside quotes - must survive splitting.
        Assert.Equal("p=ss,word", s.AdminPassword);
    }

    [Fact]
    public void RestApiUsable_true_when_enabled_and_password_set()
    {
        var s = IniReader.Parse(SampleIni);
        Assert.True(s.RestApiUsable);
    }

    [Fact]
    public void RestApiUsable_false_when_password_blank()
    {
        var ini = """OptionSettings=(AdminPassword="",RESTAPIEnabled=True,RESTAPIPort=8212)""";
        var s = IniReader.Parse(ini);

        Assert.False(s.RestApiUsable);
        Assert.Equal("", s.AdminPassword);
        Assert.True(s.RestApiEnabled);
    }

    [Fact]
    public void RestApiUsable_false_when_api_disabled()
    {
        var ini = """OptionSettings=(AdminPassword="secret",RESTAPIEnabled=False)""";
        var s = IniReader.Parse(ini);

        Assert.False(s.RestApiUsable);
        Assert.False(s.RestApiEnabled);
    }

    [Fact]
    public void Key_lookup_is_case_insensitive()
    {
        var ini = """OptionSettings=(restapiport=9999,restapienabled=true)""";
        var s = IniReader.Parse(ini);

        Assert.Equal(9999, s.RestApiPort);
        Assert.True(s.RestApiEnabled);
    }

    [Fact]
    public void Missing_keys_yield_nulls_and_defaults()
    {
        var s = IniReader.Parse("OptionSettings=(Difficulty=None)");

        Assert.Null(s.RestApiPort);
        Assert.Null(s.PublicPort);
        Assert.Null(s.RestApiEnabled);
        Assert.Null(s.AdminPassword);
        Assert.Null(s.RconPort);
        Assert.Equal(8212, s.RestApiPortOrDefault);
        Assert.Equal(8211, s.PublicPortOrDefault);
        Assert.Equal(25575, s.RconPortOrDefault);
    }

    [Fact]
    public void Garbage_or_empty_text_yields_all_nulls()
    {
        var s = IniReader.Parse("not an ini at all");

        Assert.Null(s.RestApiPort);
        Assert.Null(s.AdminPassword);
        Assert.False(s.RestApiUsable);
    }

    [Fact]
    public void ReadFile_returns_empty_settings_for_missing_file()
    {
        var s = IniReader.ReadFile(@"Z:\does\not\exist\PalWorldSettings.ini");
        Assert.Null(s.RestApiPort);
        Assert.False(s.RestApiUsable);
    }
}
