using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PalServerLauncher.Core;

/// <summary>The outcome of a <see cref="ModInfoEditor.InjectServerFlag"/> pass, so the caller can log and act.</summary>
public enum ForceOutcome
{
    /// <summary>We added or flipped a flag; <see cref="ForceResult.Json"/> holds the file to write.</summary>
    Forced,
    /// <summary>A rule was already <c>IsServer: true</c> and nothing needed changing.</summary>
    AlreadyServer,
    /// <summary>Can't force it: no <c>InstallRule</c>, unreadable JSON, or no boolean flag to act on.</summary>
    NotApplicable,
}

/// <summary>The result of an injection pass: an <see cref="ForceOutcome"/> and, when Forced, the updated JSON.</summary>
public readonly record struct ForceResult(ForceOutcome Outcome, string? Json);

/// <summary>
/// Forces a mod's <c>Info.json</c> to deploy server-side by giving its <c>InstallRule</c> entries
/// <c>"IsServer": true</c>. Backs the "Force Server Install" option for mods a Palworld dedicated server would
/// otherwise skip because the author didn't mark them server-compatible (docs' troubleshooting #1: no
/// <c>IsServer: true</c> in an InstallRule means the mod isn't built for servers).
///
/// The policy keys off whether an <c>IsServer</c> key exists ANYWHERE in the rules, so a deliberate
/// client/server split is never overridden:
/// - <b>A key exists somewhere</b> (any rule, true or false): only touch keyed rules (leave a <c>true</c>, flip
///   a boolean <c>false</c> to <c>true</c>) and NEVER add the key to a rule that lacks it. So UE4SS (one server
///   rule, one keyless client rule) is left untouched.
/// - <b>No key anywhere</b>: the mod targets no server at all, so add <c>IsServer: true</c> to every rule. So a
///   lone-Lua-rule mod like Smart Transport gets forced on.
///
/// It edits a parsed <see cref="JsonNode"/> DOM and SETS the key rather than string-appending, so a duplicate
/// <c>IsServer</c> key is impossible. Pure, so it's unit-tested.
/// </summary>
public static class ModInfoEditor
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static ForceResult InjectServerFlag(string json)
    {
        // A duplicate key surfaces as ArgumentException when the JsonObject materializes (not JsonException), and
        // malformed JSON as JsonException. Either just means we can't safely edit it, so treat it as NotApplicable.
        try { return Inject(json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new ForceResult(ForceOutcome.NotApplicable, null);
        }
    }

    private static ForceResult Inject(string json)
    {
        var root = JsonNode.Parse(json);
        if (root is not JsonObject obj
            || !obj.TryGetPropertyValue("InstallRule", out var rule)
            || rule is null)
            return new ForceResult(ForceOutcome.NotApplicable, null);

        // The real shape is an array of rule objects; a lone object is handled defensively.
        var rules = rule switch
        {
            JsonArray arr => arr.OfType<JsonObject>().ToList(),
            JsonObject single => new List<JsonObject> { single },
            _ => new List<JsonObject>(),
        };
        if (rules.Count == 0)
            return new ForceResult(ForceOutcome.NotApplicable, null);

        if (!rules.Any(HasServerKey))
        {
            // No IsServer anywhere: the author didn't target servers. Add it to every rule.
            foreach (var ruleObj in rules)
                ruleObj["IsServer"] = true;
            return new ForceResult(ForceOutcome.Forced, obj.ToJsonString(Indented));
        }

        // A key exists somewhere: only flip a boolean false to true, never add to a keyless rule (that would
        // override the author's deliberate client/server split).
        var flipped = false;
        foreach (var ruleObj in rules)
            if (ruleObj.TryGetPropertyValue("IsServer", out var v) && v?.GetValueKind() == JsonValueKind.False)
            {
                ruleObj["IsServer"] = true;
                flipped = true;
            }

        if (flipped)
            return new ForceResult(ForceOutcome.Forced, obj.ToJsonString(Indented));
        return new ForceResult(
            rules.Any(IsServerTrue) ? ForceOutcome.AlreadyServer : ForceOutcome.NotApplicable, null);
    }

    private static bool HasServerKey(JsonObject rule) => rule.ContainsKey("IsServer");

    private static bool IsServerTrue(JsonObject rule) =>
        rule.TryGetPropertyValue("IsServer", out var v) && v?.GetValueKind() == JsonValueKind.True;
}
