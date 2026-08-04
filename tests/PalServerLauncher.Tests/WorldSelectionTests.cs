using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class WorldSelectionTests
{
    private const string WorldA = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
    private const string WorldB = "B8E329B545526510A239EBAE9C65DE24";

    [Fact]
    public void Evaluate_configured_world_that_exists_is_ok()
    {
        var verdict = WorldSelection.Evaluate(WorldA, [WorldA]);

        Assert.Equal(WorldSelectionState.Ok, verdict.State);
        Assert.Equal(WorldA, verdict.WorldId);
    }

    [Fact]
    public void Evaluate_ignores_case_and_surrounding_space()
    {
        Assert.Equal(WorldSelectionState.Ok, WorldSelection.Evaluate($"  {WorldA.ToLowerInvariant()} ", [WorldA]).State);
    }

    [Fact]
    public void Evaluate_no_saves_yet_is_nothing_to_do()
    {
        Assert.Equal(WorldSelectionState.NoWorlds, WorldSelection.Evaluate(null, []).State);
        Assert.Equal(WorldSelectionState.NoWorlds, WorldSelection.Evaluate(WorldA, []).State);
    }

    [Theory]
    [InlineData(null)]           // no GameUserSettings.ini at all (restored backup, imported save)
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(WorldB)]         // names a world that isn't there
    public void Evaluate_single_save_is_recoverable(string? configured)
    {
        var verdict = WorldSelection.Evaluate(configured, [WorldA]);

        Assert.Equal(WorldSelectionState.Recoverable, verdict.State);
        Assert.Equal(WorldA, verdict.WorldId);
    }

    [Fact]
    public void Evaluate_several_saves_and_no_match_is_ambiguous()
    {
        var verdict = WorldSelection.Evaluate("SOMETHINGELSE", [WorldA, WorldB]);

        Assert.Equal(WorldSelectionState.Ambiguous, verdict.State);
        Assert.Null(verdict.WorldId);
    }

    [Fact]
    public void Evaluate_several_saves_with_a_match_is_ok()
    {
        Assert.Equal(WorldSelectionState.Ok, WorldSelection.Evaluate(WorldB, [WorldA, WorldB]).State);
    }
}

public class GameUserSettingsFileTests
{
    private const string WorldA = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
    private const string WorldB = "B8E329B545526510A239EBAE9C65DE24";

    // Trimmed from a real GameUserSettings.ini, CRLF like the server writes it.
    private const string RealFile =
        "[/Script/Pal.PalGameLocalSettings]\r\n" +
        "GraphicsLevel=None\r\n" +
        "bRunedBenchMark=False\r\n" +
        "DedicatedServerName=" + WorldA + "\r\n" +
        "AntiAliasingType=AAM_TSR\r\n" +
        "\r\n" +
        "[ScalabilityGroups]\r\n" +
        "sg.ResolutionQuality=100\r\n";

    [Fact]
    public void ReadWorldId_finds_the_key() =>
        Assert.Equal(WorldA, GameUserSettingsFile.ReadWorldId(RealFile));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[/Script/Pal.PalGameLocalSettings]\r\nGraphicsLevel=None\r\n")]
    [InlineData("[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName=\r\n")]
    [InlineData("[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName=   \r\n")]
    public void ReadWorldId_is_null_when_absent_or_blank(string? iniText) =>
        Assert.Null(GameUserSettingsFile.ReadWorldId(iniText));

    [Fact]
    public void SetWorldId_rewrites_only_that_line()
    {
        var updated = GameUserSettingsFile.SetWorldId(RealFile, WorldB);

        Assert.Equal(WorldB, GameUserSettingsFile.ReadWorldId(updated));
        Assert.Equal(RealFile.Replace(WorldA, WorldB, StringComparison.Ordinal), updated);
        Assert.DoesNotContain(WorldA, updated, StringComparison.Ordinal);
    }

    [Fact]
    public void SetWorldId_keeps_crlf_on_the_rewritten_line()
    {
        var updated = GameUserSettingsFile.SetWorldId(RealFile, WorldB);

        Assert.Contains($"DedicatedServerName={WorldB}\r\n", updated, StringComparison.Ordinal);
        Assert.Equal(RealFile.Length, updated.Length); // same GUID length, so a lost CR would show up here
    }

    [Fact]
    public void SetWorldId_adds_the_key_under_an_existing_section()
    {
        const string noKey = "[/Script/Pal.PalGameLocalSettings]\r\nGraphicsLevel=None\r\n\r\n[ScalabilityGroups]\r\n";

        var updated = GameUserSettingsFile.SetWorldId(noKey, WorldA);

        Assert.Equal(
            $"[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName={WorldA}\r\nGraphicsLevel=None\r\n\r\n[ScalabilityGroups]\r\n",
            updated);
    }

    [Fact]
    public void SetWorldId_creates_the_section_when_the_file_has_none()
    {
        var updated = GameUserSettingsFile.SetWorldId("[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\n", WorldA);

        Assert.Equal(WorldA, GameUserSettingsFile.ReadWorldId(updated));
        Assert.Contains("[/Script/Pal.PalGameLocalSettings]\r\n", updated, StringComparison.Ordinal);
        Assert.StartsWith("[ScalabilityGroups]", updated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SetWorldId_writes_a_whole_file_from_nothing(string? iniText)
    {
        var updated = GameUserSettingsFile.SetWorldId(iniText, WorldA);

        Assert.Equal($"[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName={WorldA}\r\n", updated);
    }

    [Fact]
    public void SetWorldId_handles_lf_only_files()
    {
        var updated = GameUserSettingsFile.SetWorldId("[/Script/Pal.PalGameLocalSettings]\nGraphicsLevel=None\n", WorldA);

        Assert.Equal($"[/Script/Pal.PalGameLocalSettings]\nDedicatedServerName={WorldA}\nGraphicsLevel=None\n", updated);
        Assert.DoesNotContain("\r", updated, StringComparison.Ordinal);
    }

    /// <summary>A $ in the new value must not be read as a regex substitution (the replace uses a MatchEvaluator).</summary>
    [Fact]
    public void SetWorldId_writes_a_value_containing_regex_syntax_verbatim()
    {
        const string awkward = "My$1World$&Save";

        var updated = GameUserSettingsFile.SetWorldId(RealFile, awkward);

        Assert.Equal(awkward, GameUserSettingsFile.ReadWorldId(updated));
    }

    /// <summary>A hand-edited file can end up with the key twice. Rewriting only the first would leave the two
    /// disagreeing, and the read-back check (which also reads the first) couldn't tell.</summary>
    [Fact]
    public void SetWorldId_rewrites_every_occurrence_of_the_key()
    {
        var duplicated =
            $"[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName={WorldA}\r\nGraphicsLevel=None\r\nDedicatedServerName={WorldB}\r\n";

        var updated = GameUserSettingsFile.SetWorldId(duplicated, WorldA);

        Assert.Equal(2, updated.Split($"DedicatedServerName={WorldA}").Length - 1);
        Assert.DoesNotContain(WorldB, updated, StringComparison.Ordinal);
    }

    [Fact]
    public void SetWorldId_appends_to_a_file_that_has_no_trailing_newline()
    {
        var updated = GameUserSettingsFile.SetWorldId("[ScalabilityGroups]\r\nsg.ShadowQuality=3", WorldA);

        Assert.Equal($"[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\n[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName={WorldA}\r\n", updated);
    }

    [Fact]
    public void SetWorldId_handles_a_section_header_with_nothing_after_it()
    {
        var updated = GameUserSettingsFile.SetWorldId("[/Script/Pal.PalGameLocalSettings]", WorldA);

        Assert.Equal(WorldA, GameUserSettingsFile.ReadWorldId(updated));
        Assert.StartsWith("[/Script/Pal.PalGameLocalSettings]", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void SetWorldId_tolerates_spacing_around_the_key()
    {
        var updated = GameUserSettingsFile.SetWorldId($"[/Script/Pal.PalGameLocalSettings]\r\n  DedicatedServerName = {WorldA}\r\n", WorldB);

        Assert.Equal(WorldB, GameUserSettingsFile.ReadWorldId(updated));
        Assert.DoesNotContain(WorldA, updated, StringComparison.Ordinal);
    }
}
