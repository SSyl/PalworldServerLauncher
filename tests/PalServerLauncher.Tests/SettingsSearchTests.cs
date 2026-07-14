using System.Linq;
using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class SettingsSearchTests
{
    // The setting the owner used to spec the feature: one key reachable three different ways.
    private static GameSetting RecreateInHardcore =>
        GameSettingsCatalog.All.First(s => s.Key == "bCharacterRecreateInHardcore");

    private static bool MatchesCatalog(string query, GameSetting s) =>
        SettingsSearch.Matches(query, s.Key, s.Label, s.Description);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_query_matches_everything(string? query) =>
        Assert.True(SettingsSearch.Matches(query, "AnyKey", "Any label", "Any description"));

    // The three ways the owner expects bCharacterRecreateInHardcore to be found.
    [Fact]
    public void Matches_by_description_word() =>
        Assert.True(MatchesCatalog("Death", RecreateInHardcore)); // "...recreate your character upon death..."

    [Fact]
    public void Matches_by_raw_key_prefix() =>
        Assert.True(MatchesCatalog("bCharacter", RecreateInHardcore));

    [Fact]
    public void Matches_by_multiple_tokens_spanning_fields() =>
        // "Recreate" is in the label/key, "Character" is in the key/label, order and field do not matter.
        Assert.True(MatchesCatalog("Recreate Character", RecreateInHardcore));

    [Fact]
    public void Is_case_insensitive() =>
        Assert.True(MatchesCatalog("DEATH", RecreateInHardcore));

    [Fact]
    public void All_tokens_must_be_present()
    {
        // "death" is present but "banana" is not, so the AND fails.
        Assert.False(MatchesCatalog("death banana", RecreateInHardcore));
    }

    [Fact]
    public void Unrelated_query_does_not_match() =>
        Assert.False(MatchesCatalog("Crossplay", RecreateInHardcore));

    [Fact]
    public void Token_matches_across_key_and_description_boundary_is_not_allowed()
    {
        // A token cannot straddle two fields: "keydesc" must not match key="key", desc="desc".
        Assert.False(SettingsSearch.Matches("keydesc", "key", "label", "desc"));
    }

    [Fact]
    public void Searches_localized_label_and_description_text()
    {
        // The matcher is culture-agnostic: the dialog passes the current-culture label/description, so a
        // Japanese description is searched in Japanese while the English key still works.
        const string key = "bCharacterRecreateInHardcore";
        const string jaLabel = "キャラクター再作成 (ハードコア)";
        const string jaDesc = "ハードコアモードで死亡時にキャラクターを再作成できるかどうか。";

        Assert.True(SettingsSearch.Matches("死亡", key, jaLabel, jaDesc));   // Japanese "death" in the description
        Assert.True(SettingsSearch.Matches("キャラクター", key, jaLabel, jaDesc)); // Japanese "character"
        Assert.True(SettingsSearch.Matches("bCharacter", key, jaLabel, jaDesc)); // English key still searchable
        Assert.False(SettingsSearch.Matches("Hardcore mode", key, jaLabel, jaDesc)); // English desc words are gone
    }
}
