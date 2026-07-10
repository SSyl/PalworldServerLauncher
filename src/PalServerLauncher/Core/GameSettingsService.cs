using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// Reads and writes the game/server settings in PalWorldSettings.ini via <see cref="OptionSettingsBlob"/>
/// (round-trip: unknown keys and formatting are preserved; only edited keys change). Writes are gated
/// to a stopped server. If the ini has no settings yet, it can seed one from DefaultPalWorldSettings.ini.
/// </summary>
public sealed class GameSettingsService
{
    private readonly string _serverRoot;
    private readonly Logger _logger;

    public GameSettingsService(string serverRoot, Logger logger)
    {
        _serverRoot = serverRoot;
        _logger = logger;
    }

    private string SettingsPath => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
    private string DefaultTemplatePath => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName, "DefaultPalWorldSettings.ini");

    /// <summary>True once PalWorldSettings.ini exists and has an OptionSettings line to edit.</summary>
    public bool IsInitialized() =>
        File.Exists(SettingsPath) && OptionSettingsBlob.Load(ReadOrEmpty(SettingsPath)).HasOptionSettings;

    /// <summary>Seed PalWorldSettings.ini from the default template if it's missing/empty. Returns true if it's now usable.</summary>
    public bool EnsureInitialized()
    {
        if (IsInitialized())
            return true;

        if (!File.Exists(DefaultTemplatePath))
        {
            _logger.Info("Can't open game settings - DefaultPalWorldSettings.ini not found (install the server first).");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.Copy(DefaultTemplatePath, SettingsPath, overwrite: true);
        _logger.Info("Seeded PalWorldSettings.ini from the default template.");
        return IsInitialized();
    }

    /// <summary>
    /// Values from DefaultPalWorldSettings.ini (the game's defaults), key -> unquoted value. Empty when the
    /// template is missing (server not installed). Used by the settings editor's "reset to defaults".
    /// </summary>
    public IReadOnlyDictionary<string, string?> LoadDefaults()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(DefaultTemplatePath))
            return result;

        var blob = OptionSettingsBlob.Load(ReadOrEmpty(DefaultTemplatePath));
        foreach (var key in blob.Keys)
            result[key] = blob.GetValue(key);
        return result;
    }

    /// <summary>Current unquoted values for the catalog keys (null when a key isn't present in the file).</summary>
    public IReadOnlyDictionary<string, string?> Load()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SettingsPath))
            return result;

        var blob = OptionSettingsBlob.Load(ReadOrEmpty(SettingsPath));
        foreach (var setting in GameSettingsCatalog.All)
            result[setting.Key] = setting.Type == SettingType.Raw
                ? blob.GetRaw(setting.Key)?.Trim() // tuples/lists are edited verbatim, not unquoted
                : blob.GetValue(setting.Key);
        return result;
    }

    /// <summary>
    /// Apply edits (key -> new value, typed per the catalog for correct quoting) and write the file.
    /// Refuses while the server is running - the ini must not change under a live server.
    /// </summary>
    public bool Save(IReadOnlyDictionary<string, string> edits, bool serverRunning) =>
        Save(edits, serverRunning, out _);

    /// <summary>As <see cref="Save(IReadOnlyDictionary{string,string},bool)"/>, but reports the setting
    /// whose value would corrupt the blob via <paramref name="invalidKey"/> (null on success), so a caller
    /// can name it in an error message.</summary>
    public bool Save(IReadOnlyDictionary<string, string> edits, bool serverRunning, out string? invalidKey)
    {
        invalidKey = null;
        if (serverRunning)
        {
            _logger.Info("Game settings can only be changed while the server is stopped.");
            return false;
        }
        if (!File.Exists(SettingsPath))
            return false;

        var blob = OptionSettingsBlob.Load(ReadOrEmpty(SettingsPath));
        if (!blob.HasOptionSettings)
            return false;

        var byKey = GameSettingsCatalog.All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var rawEdits = new List<(string Key, string Value)>();
        var appliedEdits = new List<(string Key, string Value)>();
        foreach (var (key, value) in edits)
        {
            if (!byKey.TryGetValue(key, out var setting)) // only ever touch keys we know how to type
                continue;
            ApplyTyped(blob, setting, value);
            if (setting.Type == SettingType.Raw)
                rawEdits.Add((setting.Key, value.Trim()));
            appliedEdits.Add((setting.Key, value));
        }

        var rendered = blob.Render();
        // Raw (tuple/list) values are written verbatim, so a malformed one - a stray comma, quote, or
        // unbalanced parenthesis - could corrupt the whole blob. Re-parse and confirm each survived intact
        // before writing; refuse rather than save a broken file (same safety net as SaveExtras).
        if (rawEdits.Count > 0)
        {
            var reparsed = OptionSettingsBlob.Load(rendered);
            foreach (var (key, value) in rawEdits)
                if ((reparsed.GetRaw(key) ?? "").Trim() != value)
                {
                    invalidKey = key;
                    _logger.Error($"Game setting '{key}' would corrupt PalWorldSettings.ini - nothing saved.");
                    return false;
                }
        }

        File.WriteAllText(SettingsPath, rendered);
        foreach (var (key, value) in appliedEdits)
            _logger.Info($"Changed: {key} = {value}");
        _logger.Info($"Saved {appliedEdits.Count} game setting(s) to PalWorldSettings.ini.");
        return true;
    }

    /// <summary>One PalWorldSettings.ini key the catalog doesn't cover, with its current unquoted value.</summary>
    public readonly record struct ExtraSetting(string Key, string Value);

    /// <summary>
    /// Keys present in PalWorldSettings.ini's OptionSettings blob that <see cref="GameSettingsCatalog"/>
    /// doesn't cover - including any params a future game update adds. Surfaced so new settings are editable
    /// without a code change (the Extra Settings panel).
    /// </summary>
    public IReadOnlyList<ExtraSetting> LoadExtras()
    {
        if (!File.Exists(SettingsPath))
            return Array.Empty<ExtraSetting>();

        var blob = OptionSettingsBlob.Load(ReadOrEmpty(SettingsPath));
        var known = GameSettingsCatalog.All.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return blob.Keys
            .Where(k => !known.Contains(k))
            .Select(k => new ExtraSetting(k, blob.GetValue(k) ?? ""))
            .ToList();
    }

    /// <summary>
    /// Write edited extra (non-catalog) keys back, preserving each one's quoted/bare shape. Stopped-only.
    /// Refuses (saving nothing) if a value would break the blob structure - checked by a round-trip.
    /// </summary>
    public bool SaveExtras(IReadOnlyDictionary<string, string> edits, bool serverRunning) =>
        SaveExtras(edits, serverRunning, out _);

    /// <summary>As <see cref="SaveExtras(IReadOnlyDictionary{string,string},bool)"/>, but reports the key
    /// whose value would corrupt the blob via <paramref name="invalidKey"/> (null on success).</summary>
    public bool SaveExtras(IReadOnlyDictionary<string, string> edits, bool serverRunning, out string? invalidKey)
    {
        invalidKey = null;
        if (serverRunning)
        {
            _logger.Info("Game settings can only be changed while the server is stopped.");
            return false;
        }
        if (!File.Exists(SettingsPath))
            return false;

        var blob = OptionSettingsBlob.Load(ReadOrEmpty(SettingsPath));
        if (!blob.HasOptionSettings)
            return false;

        var known = GameSettingsCatalog.All.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();
        foreach (var (key, value) in edits)
        {
            if (known.Contains(key) || blob.GetRaw(key) is not { } raw)
                continue; // only touch existing, non-catalog keys
            if (raw.TrimStart().StartsWith('"'))
                blob.SetString(key, value); // was a quoted string -> keep it quoted
            else
                blob.SetRaw(key, value);    // bare scalar / tuple -> write verbatim
            applied.Add(key);
        }

        // Safety net: re-parse the result; if an edited value didn't survive intact it broke the blob
        // structure (stray comma / quote / unbalanced paren) - refuse rather than write a corrupt file.
        var rendered = blob.Render();
        var reparsed = OptionSettingsBlob.Load(rendered);
        foreach (var key in applied)
            if (reparsed.GetValue(key) != edits[key])
            {
                invalidKey = key;
                _logger.Error($"Extra setting '{key}' would corrupt PalWorldSettings.ini - nothing saved.");
                return false;
            }

        File.WriteAllText(SettingsPath, rendered);
        foreach (var key in applied)
            _logger.Info($"Changed: {key} = {edits[key]}");
        _logger.Info($"Saved {applied.Count} extra setting(s) to PalWorldSettings.ini.");
        return true;
    }

    private static void ApplyTyped(OptionSettingsBlob blob, GameSetting setting, string value)
    {
        switch (setting.Type)
        {
            case SettingType.Bool:
                blob.SetBool(setting.Key, value.Trim() is var v && (v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1"));
                break;
            case SettingType.Int:
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    blob.SetInt(setting.Key, i);
                break;
            case SettingType.Float:
                if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    blob.SetFloat(setting.Key, d);
                break;
            case SettingType.Enum:
                blob.SetEnum(setting.Key, value.Trim());
                break;
            case SettingType.Raw:
                blob.SetRaw(setting.Key, value.Trim()); // tuple/list value: write exactly as typed
                break;
            default:
                blob.SetString(setting.Key, value);
                break;
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
    }
}
