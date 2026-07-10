using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class OptionSettingsBlobTests
{
    // A representative slice of the real Palworld blob: quoted strings (with a comma inside a value),
    // ints, floats, bools, and an enum token - inside the [/Script/...] section with other lines around.
    private const string Ini =
        "[/Script/Pal.PalGameWorldSettings]\r\n" +
        "OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ServerName=\"My, Server\",ServerPlayerMaxNum=32,ServerPassword=\"\",AdminPassword=\"pw\",RESTAPIEnabled=False,RESTAPIPort=8212,bIsUseBackupSaveData=True)\r\n";

    [Fact]
    public void Load_then_Render_with_no_edits_is_byte_identical()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        Assert.True(blob.HasOptionSettings);
        Assert.Equal(Ini, blob.Render());
    }

    [Fact]
    public void Reads_typed_values()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        Assert.Equal("None", blob.GetValue("Difficulty"));
        Assert.Equal("My, Server", blob.GetValue("ServerName")); // comma inside quotes not split
        Assert.Equal(32, blob.GetInt("ServerPlayerMaxNum"));
        Assert.Equal(1.0, blob.GetFloat("DayTimeSpeedRate"));
        Assert.False(blob.GetBool("RESTAPIEnabled"));
        Assert.True(blob.GetBool("bIsUseBackupSaveData"));
        Assert.Equal("pw", blob.GetValue("AdminPassword"));
    }

    [Fact]
    public void Editing_one_key_changes_only_that_key()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        blob.SetBool("RESTAPIEnabled", true);

        var result = blob.Render();
        Assert.Contains("RESTAPIEnabled=True", result);
        // Everything else preserved:
        Assert.Contains("ServerName=\"My, Server\"", result);
        Assert.Contains("AdminPassword=\"pw\"", result);
        Assert.Contains("RESTAPIPort=8212", result);
        Assert.Contains("[/Script/Pal.PalGameWorldSettings]", result);
        Assert.DoesNotContain("RESTAPIEnabled=False", result);
    }

    [Fact]
    public void SetString_quotes_and_escapes()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        blob.SetString("ServerName", "He said \"hi\"");
        Assert.Equal("He said \"hi\"", blob.GetValue("ServerName")); // round-trips back through unquote
        Assert.Contains("ServerName=\"He said \\\"hi\\\"\"", blob.Render());
    }

    [Fact]
    public void Setting_an_absent_key_appends_it()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        blob.SetInt("BaseCampWorkerMaxNum", 20);
        Assert.EndsWith("bIsUseBackupSaveData=True,BaseCampWorkerMaxNum=20)\r\n", blob.Render());
    }

    [Fact]
    public void SetFloat_uses_palworld_six_decimal_style()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        blob.SetFloat("ExpRate", 1.5);
        Assert.Contains("ExpRate=1.500000", blob.Render());
    }

    [Fact]
    public void Value_with_a_quote_survives_render_then_reparse_without_absorbing_other_keys()
    {
        var blob = OptionSettingsBlob.Load(Ini);
        blob.SetString("ServerName", "\"quoted\" name");

        // Render -> re-Load is exactly what happens when the settings dialog reopens. A naive splitter
        // desyncs on the escaped \" and lets ServerName swallow every following key (the live bug).
        var reloaded = OptionSettingsBlob.Load(blob.Render());

        Assert.Equal("\"quoted\" name", reloaded.GetValue("ServerName")); // value intact, not swallowed
        Assert.Equal(8212, reloaded.GetInt("RESTAPIPort"));               // keys after it still parse
        Assert.False(reloaded.GetBool("RESTAPIEnabled"));
        Assert.True(reloaded.GetBool("bIsUseBackupSaveData"));
    }

    // Palworld's real blob includes a NESTED parenthesized value (CrossplayPlatforms) whose internal
    // commas must not be treated as top-level separators. Missing this splits the tuple apart and drops
    // its closing paren, which corrupted a live server ("Missing closing parenthesis"). Regression guard.
    private const string IniWithNestedTuple =
        "[/Script/Pal.PalGameWorldSettings]\r\n" +
        "OptionSettings=(Difficulty=None,ExpRate=1.000000,CrossplayPlatforms=(Steam,Xbox,PS5,Mac),ServerName=\"My Server\",bIsUseBackupSaveData=True,RESTAPIEnabled=True)\r\n";

    [Fact]
    public void Nested_paren_value_round_trips_identically()
    {
        var blob = OptionSettingsBlob.Load(IniWithNestedTuple);
        Assert.True(blob.HasOptionSettings);
        Assert.Equal(IniWithNestedTuple, blob.Render());
    }

    [Fact]
    public void Nested_paren_value_is_kept_whole_not_split_into_keys()
    {
        var blob = OptionSettingsBlob.Load(IniWithNestedTuple);
        Assert.Equal("(Steam,Xbox,PS5,Mac)", blob.GetRaw("CrossplayPlatforms")?.Trim());
        // The tokens inside the tuple must NOT be parsed as their own keys.
        Assert.DoesNotContain("Xbox", blob.Keys);
        Assert.DoesNotContain("Mac", blob.Keys);
    }

    [Fact]
    public void Editing_a_key_preserves_a_nested_paren_value_elsewhere()
    {
        var blob = OptionSettingsBlob.Load(IniWithNestedTuple);
        blob.SetFloat("ExpRate", 2.5);

        var result = blob.Render();
        Assert.Contains("ExpRate=2.500000", result);
        Assert.Contains("CrossplayPlatforms=(Steam,Xbox,PS5,Mac)", result); // tuple untouched + intact
        Assert.EndsWith(")\r\n", result); // whole-blob closing paren still present
    }

    [Fact]
    public void No_option_settings_line_is_a_noop()
    {
        var blob = OptionSettingsBlob.Load("[/Script/Pal.PalGameWorldSettings]\r\n");
        Assert.False(blob.HasOptionSettings);
        blob.SetBool("RESTAPIEnabled", true); // tolerated but not rendered without a line
        Assert.Equal("[/Script/Pal.PalGameWorldSettings]\r\n", blob.Render());
    }
}
