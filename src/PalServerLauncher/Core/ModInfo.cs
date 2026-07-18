using System.Linq;
using System.Text.Json;

namespace PalServerLauncher.Core;

/// <summary>
/// A parsed Palworld mod <c>Info.json</c>: the <c>PackageName</c> (what PalModSettings.ini's ActiveModList
/// needs), the <c>Version</c>, and whether the mod declares server support (any <c>InstallRule</c> entry with
/// <c>IsServer: true</c>). <c>InstallRule</c> is normally an array of rule objects (a lone object is handled
/// defensively). The parser is tolerant (returns null on malformed JSON or a missing PackageName), so it's
/// unit-tested.
/// </summary>
public sealed record ModInfo(string PackageName, string Version, bool IsServer)
{
    public static ModInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var packageName = GetString(root, "PackageName");
            if (string.IsNullOrWhiteSpace(packageName))
                return null;

            var version = GetString(root, "Version") ?? "";
            return new ModInfo(packageName!, version, HasServerRule(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when any <c>InstallRule</c> entry has <c>IsServer: true</c>. Handles the real array shape
    /// and, defensively, a lone rule object.</summary>
    private static bool HasServerRule(JsonElement root)
    {
        if (!root.TryGetProperty("InstallRule", out var rule))
            return false;
        return rule.ValueKind switch
        {
            JsonValueKind.Array => rule.EnumerateArray().Any(RuleIsServer),
            JsonValueKind.Object => RuleIsServer(rule),
            _ => false,
        };
    }

    private static bool RuleIsServer(JsonElement rule) =>
        rule.ValueKind == JsonValueKind.Object
        && rule.TryGetProperty("IsServer", out var srv)
        && srv.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;
}
