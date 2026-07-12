using System.Collections.Generic;
using System.Linq;

namespace PalServerLauncher.Config;

/// <summary>
/// Round-trips Palworld's <c>Mods/PalModSettings.ini</c>: a single <c>[PalModSettings]</c> section with
/// <c>bGlobalEnableMod</c>, <c>WorkshopRootDir</c>, <c>ConfigVersion</c>, and zero or more repeated
/// <c>ActiveModList=&lt;PackageName&gt;</c> lines (one per enabled mod). The launcher rewrites ONLY
/// <c>bGlobalEnableMod</c> and the <c>ActiveModList</c> lines, preserving every other key and line verbatim,
/// so it never clobbers <c>WorkshopRootDir</c> / <c>ConfigVersion</c> or a future key. Pure, so it's tested.
/// </summary>
public sealed class PalModSettingsFile
{
    private const string SectionHeader = "[PalModSettings]";

    private readonly List<string> _lines;
    private readonly string _newline;
    private bool _globalEnable;
    private List<string> _activeMods;

    private PalModSettingsFile(List<string> lines, string newline, bool globalEnable, List<string> activeMods)
    {
        _lines = lines;
        _newline = newline;
        _globalEnable = globalEnable;
        _activeMods = activeMods;
    }

    public bool GlobalEnable => _globalEnable;
    public IReadOnlyList<string> ActiveMods => _activeMods;

    public static PalModSettingsFile Load(string iniText)
    {
        var newline = iniText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = iniText.Replace("\r\n", "\n").Split('\n').ToList();

        var globalEnable = false;
        var activeMods = new List<string>();
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (IsSectionHeader(trimmed))
            {
                inSection = trimmed.Equals(SectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection)
                continue;
            var (key, value) = SplitKeyValue(trimmed);
            if (key.Equals("bGlobalEnableMod", StringComparison.OrdinalIgnoreCase))
                globalEnable = value.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            else if (key.Equals("ActiveModList", StringComparison.OrdinalIgnoreCase) && value.Trim().Length > 0)
                activeMods.Add(value.Trim());
        }
        return new PalModSettingsFile(lines, newline, globalEnable, activeMods);
    }

    public void SetGlobalEnable(bool value) => _globalEnable = value;

    public void SetActiveMods(IEnumerable<string> packageNames) =>
        _activeMods = packageNames.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();

    /// <summary>Re-emit the ini, rewriting only bGlobalEnableMod (in place) and the ActiveModList lines
    /// (regenerated right after it), and preserving every other line verbatim.</summary>
    public string Render()
    {
        var output = new List<string>();
        var inSection = false;
        var wroteBlock = false;
        var sawSection = false;
        foreach (var line in _lines)
        {
            var trimmed = line.Trim();
            if (IsSectionHeader(trimmed))
            {
                inSection = trimmed.Equals(SectionHeader, StringComparison.OrdinalIgnoreCase);
                if (inSection)
                    sawSection = true;
                output.Add(line);
                continue;
            }
            if (inSection)
            {
                var (key, _) = SplitKeyValue(trimmed);
                if (key.Equals("bGlobalEnableMod", StringComparison.OrdinalIgnoreCase))
                {
                    EmitModBlock(output);
                    wroteBlock = true;
                    continue;
                }
                if (key.Equals("ActiveModList", StringComparison.OrdinalIgnoreCase))
                    continue; // regenerated with the bGlobalEnableMod line
            }
            output.Add(line);
        }
        if (!wroteBlock)
        {
            if (!sawSection)
                output.Add(SectionHeader);
            EmitModBlock(output);
        }
        return string.Join(_newline, output);
    }

    private void EmitModBlock(List<string> output)
    {
        output.Add($"bGlobalEnableMod={(_globalEnable ? "True" : "False")}");
        foreach (var pkg in _activeMods)
            output.Add($"ActiveModList={pkg}");
    }

    private static bool IsSectionHeader(string trimmed) =>
        trimmed.StartsWith('[') && trimmed.EndsWith(']');

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? (line, "") : (line[..eq].Trim(), line[(eq + 1)..]);
    }
}
