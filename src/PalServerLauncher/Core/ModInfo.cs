using System.Text.Json;

namespace PalServerLauncher.Core;

/// <summary>
/// A parsed Palworld mod <c>Info.json</c>: the <c>PackageName</c> (what PalModSettings.ini's ActiveModList
/// needs), the <c>Version</c>, and whether the mod declares server support (<c>InstallRule.IsServer</c>).
/// The parser is tolerant (returns null on malformed JSON or a missing PackageName), so it's unit-tested.
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
            var isServer = root.TryGetProperty("InstallRule", out var rule)
                && rule.ValueKind == JsonValueKind.Object
                && rule.TryGetProperty("IsServer", out var srv)
                && srv.ValueKind == JsonValueKind.True;

            return new ModInfo(packageName!, version, isServer);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;
}
