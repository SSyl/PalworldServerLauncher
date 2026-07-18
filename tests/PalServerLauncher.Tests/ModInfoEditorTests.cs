using System.Text.Json;
using System.Text.Json.Nodes;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ModInfoEditorTests
{
    [Fact]
    public void Adds_IsServer_to_all_rules_when_none_present()
    {
        var json = """{"PackageName":"SmartTransport","InstallRule":[{"Type":"Lua","Targets":["./Scripts"]}]}""";
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, result.Outcome);
        Assert.Equal(new bool?[] { true }, RuleFlags(result.Json!));
    }

    [Fact]
    public void Adds_to_every_rule_when_multiple_and_none_present()
    {
        var json = """{"PackageName":"X","InstallRule":[{"Type":"Lua"},{"Type":"Pak"}]}""";
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, result.Outcome);
        Assert.Equal(new bool?[] { true, true }, RuleFlags(result.Json!));
    }

    [Fact]
    public void Flips_explicit_false_to_true()
    {
        var json = """{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":false}]}""";
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, result.Outcome);
        Assert.Equal(new bool?[] { true }, RuleFlags(result.Json!));
    }

    [Fact]
    public void When_a_key_exists_flips_false_but_never_adds_to_a_keyless_rule()
    {
        // A key exists (the false), so the keyless rule is left keyless; only the false flips.
        var json = """
            {"PackageName":"X","InstallRule":[
                {"Type":"Lua","Targets":["./A"]},
                {"Type":"Pak","IsServer":false,"Targets":["./B"]}
            ]}
            """;
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, result.Outcome);
        Assert.Equal(new bool?[] { null, true }, RuleFlags(result.Json!)); // keyless stays null, false -> true
    }

    [Fact]
    public void Leaves_mod_alone_when_a_rule_is_already_true()
    {
        // The real UE4SS shape: one keyless client rule, one already IsServer:true. Deliberate split - untouched.
        var json = """
            {"PackageName":"UE4SSExperimentalPW","InstallRule":[
                {"Type":"UE4SS","Targets":["."]},
                {"Type":"UE4SS","IsServer":true,"Targets":["."]}
            ]}
            """;
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.AlreadyServer, result.Outcome);
        Assert.Null(result.Json);
    }

    [Fact]
    public void Single_true_rule_is_already_server()
    {
        var json = """{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":true}]}""";
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.AlreadyServer, result.Outcome);
        Assert.Null(result.Json);
    }

    [Theory]
    [InlineData("""{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":"false"}]}""")] // string
    [InlineData("""{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":0}]}""")]        // number
    [InlineData("""{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":null}]}""")]     // null
    public void Non_boolean_IsServer_with_no_true_is_not_applicable(string json) =>
        Assert.Equal(ForceOutcome.NotApplicable, ModInfoEditor.InjectServerFlag(json).Outcome);

    [Theory]
    [InlineData("""{"PackageName":"X"}""")]                          // no InstallRule
    [InlineData("""{"PackageName":"X","InstallRule":"nonsense"}""")] // InstallRule is a string
    [InlineData("""{"PackageName":"X","InstallRule":[{"IsServer":false,"IsServer":false}]}""")] // duplicate key -> ArgumentException, not a throw out of us
    [InlineData("not json")]
    [InlineData("")]
    public void Unforceable_input_is_not_applicable(string json) =>
        Assert.Equal(ForceOutcome.NotApplicable, ModInfoEditor.InjectServerFlag(json).Outcome);

    [Fact]
    public void Is_idempotent()
    {
        var json = """{"PackageName":"X","InstallRule":[{"Type":"Lua"}]}""";
        var once = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, once.Outcome);
        // Second pass over the already-forced output: a true now exists, so nothing to change.
        Assert.Equal(ForceOutcome.AlreadyServer, ModInfoEditor.InjectServerFlag(once.Json!).Outcome);
    }

    [Fact]
    public void Handles_install_rule_as_single_object()
    {
        var json = """{"PackageName":"X","InstallRule":{"Type":"Lua","Targets":["./Scripts"]}}""";
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, result.Outcome);
        var rule = (JsonObject)JsonNode.Parse(result.Json!)!["InstallRule"]!;
        Assert.Equal(JsonValueKind.True, rule["IsServer"]!.GetValueKind());
    }

    [Fact]
    public void Preserves_all_other_fields()
    {
        var json = """
            {"ModName":"Smart Transport","PackageName":"SmartTransport","Version":"1.0.4",
             "Dependencies":["UE4SSExperimentalPW"],"Tags":["UE4SS","Gameplay"],
             "InstallRule":[{"Type":"Lua","Targets":["./Scripts"]}]}
            """;
        var result = ModInfoEditor.InjectServerFlag(json);
        Assert.Equal(ForceOutcome.Forced, result.Outcome);
        var obj = (JsonObject)JsonNode.Parse(result.Json!)!;
        Assert.Equal("SmartTransport", (string?)obj["PackageName"]);
        Assert.Equal("1.0.4", (string?)obj["Version"]);
        Assert.Equal("UE4SSExperimentalPW", (string?)obj["Dependencies"]!.AsArray().Single());
        Assert.Equal(new[] { "UE4SS", "Gameplay" }, obj["Tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal("./Scripts", (string?)obj["InstallRule"]!.AsArray().Single()!["Targets"]!.AsArray().Single());
    }

    /// <summary>Each InstallRule entry's IsServer state: true, false, or null when the key is absent.</summary>
    private static IEnumerable<bool?> RuleFlags(string json) =>
        JsonNode.Parse(json)!["InstallRule"]!.AsArray()
            .Select(rule =>
            {
                if (rule is JsonObject o && o.TryGetPropertyValue("IsServer", out var v))
                    return v?.GetValueKind() == JsonValueKind.True;
                return (bool?)null;
            });
}
