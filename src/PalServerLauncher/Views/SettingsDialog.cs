using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;

namespace PalServerLauncher.Views;

/// <summary>Which slice of the settings this dialog instance shows.</summary>
public enum SettingsSection
{
    /// <summary>The tabbed PalWorldSettings.ini editor: World Settings, Admin, Undocumented, plus a Launch
    /// Arguments tab (launcher.json args, always editable) folded in.</summary>
    ServerSettings,
    /// <summary>Low-level process tuning (priority, CPU affinity), behind a danger-zone warning.</summary>
    Advanced,
}

/// <summary>
/// Data-driven settings editor, opened in one of three sections (<see cref="SettingsSection"/>) so each
/// button gets a focused dialog. Launch args write our config (apply on next start); the ini sections
/// are grouped by category, rendered by type, grayed while the server is running (the ini must not
/// change under a live server), and only CHANGED keys are written back (unedited keys are preserved by
/// the round-trip writer). Typed text fields gate their input and turn red on invalid values; Save is
/// blocked and lists any failures. Built in code to match the other dark dialogs.
/// </summary>
public sealed class SettingsDialog : Window
{
    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    private static readonly Brush ErrorBorder = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
    private static readonly Brush LinkFg = new SolidColorBrush(Color.FromRgb(0x5A, 0xA0, 0xE0));

    private const string ConfigDocsUrl = "https://docs.palworldgame.com/settings-and-operation/configuration/";

    private readonly SettingsSection _section;
    private readonly LauncherConfig _config;
    private readonly GameSettingsService _gameSettings;
    private readonly bool _serverRunning;
    private bool _saved;

    // Launch-arg inputs (only built in the LaunchArgs section).
    private TextBox? _port, _maxPlayers, _workerThreads, _publicIp, _publicPort, _extraArgs;
    private CheckBox? _perfThreads, _community;
    private ComboBox? _logFormat;
    private ComboBox? _priority;
    private CheckBox[]? _affinityBoxes;
    private TextBox? _commandPreview;
    private string _extraArgsOriginal = "";

    // Game-setting inputs: (setting, read-current-value, write-value, original-value). Set lets a difficulty
    // preset push values into the live controls.
    private readonly List<(GameSetting Setting, System.Func<string> Read, System.Action<string> Set, string Original)> _gameInputs = new();

    // Extra (non-catalog) inputs: (key, read-current-value, original-value).
    private readonly List<(string Key, System.Func<string> Read, string Original)> _extraInputs = new();

    // Keys whose "does nothing on a dedicated server" warning has already been shown this dialog session.
    private readonly HashSet<string> _noEffectWarned = new();

    // Text fields (launch args + typed game settings) with character gating, live red, and Save validation.
    private readonly List<ValidatedField> _validated = new();
    private sealed record ValidatedField(string Label, TextBox Box, SettingType Type, double? Min, double? Max, bool Required);

    // Per-field reset-to-default actions (each ↺ button; also driven by the "Reset to defaults" button).
    private readonly List<System.Action> _resetActions = new();

    private SettingsDialog(SettingsSection section, LauncherConfig config, GameSettingsService gameSettings, bool serverRunning)
    {
        _section = section;
        _config = config;
        _gameSettings = gameSettings;
        _serverRunning = serverRunning;

        Title = section == SettingsSection.Advanced ? Strings.Settings_AdvancedTitle : Strings.Settings_ServerTitle;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = section == SettingsSection.ServerSettings ? 720 : 660;
        Height = section == SettingsSection.Advanced ? 440 : 720;
        ShowInTaskbar = false;

        if (section == SettingsSection.ServerSettings)
        {
            BuildServerSettings();
            return;
        }

        // The only non-ServerSettings section left is Advanced (Launch Arguments is now a tab in ServerSettings).
        var stack = new StackPanel { Margin = new Thickness(18) };
        BuildAdvanced(stack);

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 10, 18, 14) };
        if (_resetActions.Count > 0)
            buttons.Children.Add(MakeButton(Strings.Settings_ResetToDefaults, ResetAll));
        buttons.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>
    /// Build the tabbed PalWorldSettings.ini editor: World Settings + Admin hold the documented (and
    /// understood-undocumented) keys grouped by category, and the Undocumented tab collects the keys we
    /// can't confidently explain plus any the catalog doesn't recognize. One Save applies every tab.
    /// </summary>
    private void BuildServerSettings()
    {
        var gameAvailable = _gameSettings.EnsureInitialized();
        var gameEnabled = gameAvailable && !_serverRunning;
        var current = gameAvailable ? _gameSettings.Load() : new Dictionary<string, string?>();
        var defaults = gameAvailable ? _gameSettings.LoadDefaults() : new Dictionary<string, string?>();

        var world = BuildIniTab(
            DocBlurb(Strings.Settings_WorldBlurb,
                "https://docs.palworldgame.com/settings-and-operation/configuration#features", Strings.Settings_WorldBlurbLink),
            new[] { SettingCategory.Performance, SettingCategory.Gameplay, SettingCategory.GameBalance },
            s => s.Doc != DocStatus.Unknown, gameEnabled, current, defaults,
            topExtra: BuildPresetRow(gameEnabled));
        var admin = BuildIniTab(
            DocBlurb(Strings.Settings_AdminBlurb,
                "https://docs.palworldgame.com/settings-and-operation/configuration#server-management", Strings.Settings_AdminBlurbLink),
            new[] { SettingCategory.ServerAdmin },
            s => s.Doc != DocStatus.Unknown, gameEnabled, current, defaults);
        var undoc = BuildUndocumentedTab(gameAvailable, gameEnabled, current, defaults);

        // Launch Arguments live in launcher.json (ours), so this tab stays editable even while the server runs,
        // unlike the ini-backed tabs above. BuildLaunchArgs wires its own fields, live preview, and resets.
        var launchStack = new StackPanel { Margin = new Thickness(18) };
        BuildLaunchArgs(launchStack);
        var launch = new ScrollViewer { Content = launchStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var tabs = new TabControl
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderThickness = new Thickness(0),
        };
        if (Application.Current?.TryFindResource("DarkTabItem") is Style tabStyle)
            tabs.ItemContainerStyle = tabStyle;
        tabs.Items.Add(new TabItem { Header = Strings.Settings_TabWorld, Content = world });
        tabs.Items.Add(new TabItem { Header = Strings.Settings_TabAdmin, Content = admin });
        tabs.Items.Add(new TabItem { Header = Strings.Settings_TabUndocumented, Content = undoc });
        tabs.Items.Add(new TabItem { Header = Strings.Settings_TabLaunchArgs, Content = launch });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 10, 18, 14) };
        if (_resetActions.Count > 0)
            buttons.Children.Add(MakeButton(Strings.Settings_ResetToDefaults, ResetAll));
        buttons.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));

        var root = new DockPanel();
        var bannerText = _serverRunning
            ? Strings.Settings_BannerServerRunning
            : !gameAvailable
                ? Strings.Settings_BannerGameUnavailable
                : null;
        if (bannerText is not null)
        {
            var banner = Banner(bannerText);
            DockPanel.SetDock(banner, Dock.Top);
            root.Children.Add(banner);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    /// <summary>One catalog tab: the doc blurb, then rows for the matching keys grouped by category (headers
    /// are dropped for a category with no matching rows).</summary>
    private ScrollViewer BuildIniTab(UIElement blurb, IEnumerable<SettingCategory> categories, Func<GameSetting, bool> filter,
        bool gameEnabled, IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string?> defaults,
        UIElement? topExtra = null)
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(blurb);
        if (topExtra is not null)
            stack.Children.Add(topExtra);
        foreach (var category in categories)
        {
            var settings = GameSettingsCatalog.All.Where(s => s.Category == category && filter(s)).ToList();
            if (settings.Count == 0)
                continue;
            stack.Children.Add(Header(CategoryLabel(category)));
            foreach (var setting in settings)
                AddCatalogRow(stack, setting, gameEnabled, current, defaults);
        }
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>The Undocumented tab: cataloged keys we can't confidently explain (typed, with a best-guess
    /// tooltip), then a divider and any keys in the config the catalog doesn't recognize (raw, future-proofing
    /// against a game update adding params before we catalog them).</summary>
    private ScrollViewer BuildUndocumentedTab(bool gameAvailable, bool gameEnabled,
        IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string?> defaults)
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(DocBlurb(
            Strings.Settings_UndocumentedBlurb,
            ConfigDocsUrl, Strings.Settings_UndocumentedBlurbLink));

        stack.Children.Add(Header(Strings.Settings_UndocKnownHeader));
        foreach (var setting in GameSettingsCatalog.All.Where(s => s.Doc == DocStatus.Unknown))
            AddCatalogRow(stack, setting, gameEnabled, current, defaults);

        stack.Children.Add(Header(Strings.Settings_UndocNewHeader));
        if (AppendExtras(stack, gameAvailable, gameEnabled) == 0)
            stack.Children.Add(new TextBlock
            {
                Text = Strings.Settings_UndocNoneRecognized,
                Foreground = Fg, Margin = new Thickness(0, 2, 0, 0),
            });

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Add one catalog key's row (input built by type, reset when a default exists and it's editable,
    /// undocumented marker driven by <see cref="GameSetting.Doc"/>).</summary>
    private void AddCatalogRow(StackPanel stack, GameSetting setting, bool gameEnabled,
        IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string?> defaults)
    {
        var value = current.TryGetValue(setting.Key, out var v) ? v ?? "" : "";
        var hasDefault = defaults.TryGetValue(setting.Key, out var dv);
        // Tooltip leads with the real ini key (the "true" name) so it's discoverable, then the description.
        var tip = string.IsNullOrEmpty(setting.Description) ? setting.Key : $"{setting.Key}\n{setting.Description}";
        // An app-preferred default (e.g. RESTAPIEnabled = True) overrides the game default for reset: the ↺
        // resets toward it (so it only shows when the value differs, i.e. REST is off) and the key is left out
        // of bulk "Reset to defaults" so a blanket reset can't disable a feature the launcher relies on.
        var resetTarget = setting.AppDefault ?? dv ?? "";
        var (input, reset) = BuildGameInput(setting, value, resetTarget, gameEnabled);
        if (setting.NoServerEffect && input is ComboBox noEffectCombo)
            WarnNoServerEffectOnUse(setting, noEffectCombo);
        // No reset (↺) on secret fields, don't let "Reset to defaults" silently blank a password.
        var offerReset = gameEnabled && !setting.Secret && (setting.AppDefault is not null || hasDefault);
        stack.Children.Add(Row(setting.Label, input, tip, offerReset ? reset : null, setting.Doc,
            includeInBulkReset: setting.AppDefault is null));
    }

    /// <summary>Warn (once per session) when the user opens a dropdown for a setting that has no effect on a
    /// dedicated server, e.g. the game's Difficulty key, which is a client / single-player setting.</summary>
    private void WarnNoServerEffectOnUse(GameSetting setting, ComboBox combo)
    {
        combo.DropDownOpened += (_, _) =>
        {
            if (!_noEffectWarned.Add(setting.Key))
                return;
            combo.IsDropDownOpen = false;
            // Defer so the modal doesn't open synchronously inside the dropdown-opened event.
            Dispatcher.BeginInvoke(new System.Action(() => ChoiceDialog.Show(this, Strings.Settings_NoEffectTitle,
                string.Format(Strings.Settings_NoEffectMessage, setting.Label), Strings.Settings_GotIt)));
        };
    }

    /// <summary>The difficulty-preset buttons for the World Settings tab (disabled while the server runs).</summary>
    private UIElement BuildPresetRow(bool enabled)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Settings_DifficultyPreset, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        foreach (var name in DifficultyPresets.Names)
        {
            var presetName = name;
            var button = MakeButton(presetName, () => ApplyPreset(presetName));
            button.IsEnabled = enabled;
            button.Margin = new Thickness(0, 0, 6, 4);
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>
    /// Apply a difficulty preset: show what changes versus the current values, and on confirm push them into
    /// the live fields and save just those keys (each logged to the General tab). Saves directly rather than
    /// only staging, matching the user-facing "apply and save" flow; other unsaved edits are left as-is.
    /// </summary>
    private void ApplyPreset(string presetName)
    {
        if (_serverRunning)
            return; // buttons are disabled while running, but guard anyway

        var defaults = _gameSettings.LoadDefaults();
        var current = _gameInputs.ToDictionary(g => g.Setting.Key, g => (string?)g.Read(), StringComparer.OrdinalIgnoreCase);
        var changes = DifficultyPresets.ResolveChanges(presetName, defaults, current);

        if (changes.Count == 0)
        {
            ChoiceDialog.Show(this, string.Format(Strings.Settings_PresetTitle, presetName),
                string.Format(Strings.Settings_PresetNoChanges, presetName), Strings.Common_OK);
            return;
        }

        var list = string.Join("\n", changes.Select(c => $"{c.Key} = {c.Value}"));
        var message = string.Format(Strings.Settings_PresetConfirmMessage, presetName, list);
        if (ChoiceDialog.Show(this, string.Format(Strings.Settings_PresetTitle, presetName), message, Strings.Settings_Yes, Strings.Settings_No) != 0)
            return;

        // Push into the live controls and update each Original so the change isn't re-flagged as unsaved.
        var edits = changes.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _gameInputs.Count; i++)
        {
            if (edits.TryGetValue(_gameInputs[i].Setting.Key, out var val))
            {
                var g = _gameInputs[i];
                g.Set(val);
                _gameInputs[i] = (g.Setting, g.Read, g.Set, val);
            }
        }

        if (!_gameSettings.Save(edits, serverRunning: false, out var badKey))
        {
            ShowCorruptError(badKey);
            return;
        }
        _saved = true;
        ChoiceDialog.Show(this, string.Format(Strings.Settings_PresetAppliedTitle, presetName),
            string.Format(Strings.Settings_PresetAppliedMessage, presetName, changes.Count), Strings.Common_OK);
    }

    public static bool ShowServerSettings(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.ServerSettings, config, gs, serverRunning);

    public static bool ShowAdvanced(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.Advanced, config, gs, serverRunning);

    private static bool Show(Window? owner, SettingsSection section, LauncherConfig config, GameSettingsService gs, bool serverRunning)
    {
        var dialog = new SettingsDialog(section, config, gs, serverRunning) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private void BuildLaunchArgs(StackPanel stack)
    {
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Settings_LaunchArgsIntro,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        // Live, read-only command preview: not editable, but selectable so it can be copied.
        _commandPreview = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 46,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xD0, 0x9C)),
            FontFamily = new FontFamily("Consolas"), BorderBrush = NormalBorder,
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 14),
        };
        stack.Children.Add(_commandPreview);

        // The single biggest launch-arg footgun: Listen port vs Public port. Call it out visibly.
        var portNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = Muted };
        portNote.Inlines.Add(new Run(Strings.Settings_ListenPortTerm) { FontWeight = FontWeights.SemiBold, Foreground = Fg });
        portNote.Inlines.Add(Strings.Settings_ListenPortNote);
        portNote.Inlines.Add(new Run(Strings.Settings_PublicPortTerm) { FontWeight = FontWeights.SemiBold, Foreground = Fg });
        portNote.Inlines.Add(Strings.Settings_PublicPortNote);
        stack.Children.Add(portNote);

        _port = ValidatedTextField(Strings.Settings_ValidateListenPort, _config.ServerPort.ToString(), true, SettingType.Int, min: 1, max: 65535, required: true);
        _maxPlayers = ValidatedTextField(Strings.Settings_ValidateMaxPlayers, _config.MaxPlayers.ToString(), true, SettingType.Int, min: 0);
        _perfThreads = CheckField(_config.PerformanceThreads, true);
        _workerThreads = ValidatedTextField(Strings.Settings_ValidateWorkerThreads, _config.WorkerThreads.ToString(), true, SettingType.Int, min: 0);
        _community = CheckField(_config.CommunityServer, true);
        _publicIp = ValidatedTextField(Strings.Settings_ValidatePublicIp, _config.PublicIp, true, SettingType.IpAddress);
        _publicPort = ValidatedTextField(Strings.Settings_ValidatePublicPort, _config.PublicPortArg.ToString(), true, SettingType.Int, min: 0, max: 65535);
        _logFormat = ComboField(new[] { "", "Text", "Json" }, _config.LogFormat, true);

        // Rebuild the preview whenever any launch field changes.
        _port.TextChanged += OnLaunchFieldChanged;
        _maxPlayers.TextChanged += OnLaunchFieldChanged;
        _perfThreads.Checked += OnLaunchFieldChanged;
        _perfThreads.Unchecked += OnLaunchFieldChanged;
        _workerThreads.TextChanged += OnLaunchFieldChanged;
        _community.Checked += OnLaunchFieldChanged;
        _community.Unchecked += OnLaunchFieldChanged;
        _publicIp.TextChanged += OnLaunchFieldChanged;
        _publicPort.TextChanged += OnLaunchFieldChanged;
        _logFormat.SelectionChanged += OnLaunchFieldChanged;

        var d = new LauncherConfig(); // built-in defaults for the reset (↺) actions
        stack.Children.Add(Row(Strings.Settings_RowListenPort, _port,
            Strings.Settings_TipListenPort,
            TextReset(_port, d.ServerPort.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row(Strings.Settings_RowMaxPlayers, _maxPlayers,
            Strings.Settings_TipMaxPlayers,
            TextReset(_maxPlayers, d.MaxPlayers.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row(Strings.Settings_RowPerfThreads, _perfThreads,
            Strings.Settings_TipPerfThreads,
            CheckReset(_perfThreads, d.PerformanceThreads)));
        stack.Children.Add(Row(Strings.Settings_RowWorkerThreads, _workerThreads,
            Strings.Settings_TipWorkerThreads,
            TextReset(_workerThreads, d.WorkerThreads.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row(Strings.Settings_RowCommunity, _community,
            Strings.Settings_TipCommunity,
            CheckReset(_community, d.CommunityServer)));
        stack.Children.Add(Row(Strings.Settings_RowPublicIp, _publicIp,
            Strings.Settings_TipPublicIp,
            TextReset(_publicIp, d.PublicIp)));
        stack.Children.Add(Row(Strings.Settings_RowPublicPort, _publicPort,
            Strings.Settings_TipPublicPort,
            TextReset(_publicPort, d.PublicPortArg.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row(Strings.Settings_RowLogFormat, _logFormat,
            Strings.Settings_TipLogFormat,
            ComboReset(_logFormat, d.LogFormat)));

        // --- Advanced (collapsed): free-form extra arguments ---
        _extraArgsOriginal = _config.ExtraServerArgs;
        _extraArgs = new TextBox
        {
            Text = _config.ExtraServerArgs, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = FieldBg, Foreground = Fg, BorderBrush = NormalBorder,
            Padding = new Thickness(6), CaretBrush = Brushes.White,
        };
        _extraArgs.TextChanged += OnLaunchFieldChanged;
        _resetActions.Add(() => _extraArgs.Text = d.ExtraServerArgs); // "Reset to defaults" clears the Advanced box too

        var advancedBody = new StackPanel();
        advancedBody.Children.Add(new TextBlock
        {
            Text = Strings.Settings_ExtraArgsNote,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
        });
        advancedBody.Children.Add(_extraArgs);

        stack.Children.Add(new Expander
        {
            Header = Strings.Settings_AdvancedExpander, IsExpanded = false, Foreground = Fg, Margin = new Thickness(0, 12, 0, 0), Content = advancedBody,
        });

        UpdatePreview();
    }

    /// <summary>The Advanced Settings section: low-level process tuning applied after launch (danger-zone gated).</summary>
    private void BuildAdvanced(StackPanel stack)
    {
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Settings_AdvancedIntro,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        stack.Children.Add(Header(Strings.Settings_PriorityAffinityHeader));

        _priority = ComboField(new[] { "Below normal", "Normal", "Above normal", "High" }, PriorityToLabel(_config.ServerPriority), true);
        stack.Children.Add(Row(Strings.Settings_RowPriority, _priority,
            Strings.Settings_TipPriority,
            ComboReset(_priority, "Normal")));

        var cores = Environment.ProcessorCount;
        _affinityBoxes = new CheckBox[cores];
        var allCores = _config.ServerAffinityMask == 0;
        var affinityPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < cores; i++)
        {
            var box = new CheckBox
            {
                Content = string.Format(Strings.Settings_CoreLabel, i), Foreground = Fg, MinWidth = 68, Margin = new Thickness(0, 0, 10, 4),
                IsChecked = allCores || (_config.ServerAffinityMask & (1L << i)) != 0,
            };
            _affinityBoxes[i] = box;
            affinityPanel.Children.Add(box);
        }
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Settings_CpuAffinityNote,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4),
        });
        stack.Children.Add(affinityPanel);
        _resetActions.Add(() => { foreach (var b in _affinityBoxes) b.IsChecked = true; }); // reset = all cores
    }

    private static string PriorityToLabel(string priority) => priority switch
    {
        "BelowNormal" => "Below normal",
        "AboveNormal" => "Above normal",
        "High" => "High",
        _ => "Normal",
    };

    private static string LabelToPriority(string? label) => label switch
    {
        "Below normal" => "BelowNormal",
        "Above normal" => "AboveNormal",
        "High" => "High",
        _ => "Normal",
    };

    /// <summary>The affinity mask from the core checkboxes; 0 when all (or none) are checked = no restriction.</summary>
    private long ComputeAffinityMask()
    {
        if (_affinityBoxes is null)
            return _config.ServerAffinityMask;
        long mask = 0;
        for (var i = 0; i < _affinityBoxes.Length; i++)
            if (_affinityBoxes[i].IsChecked == true)
                mask |= 1L << i;
        var allMask = _affinityBoxes.Length >= 64 ? -1L : (1L << _affinityBoxes.Length) - 1;
        return mask == 0 || mask == allMask ? 0 : mask;
    }

    private void OnLaunchFieldChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_commandPreview is null) return;
        var temp = new LauncherConfig
        {
            ServerPort = ParseInt(_port!.Text, _config.ServerPort),
            MaxPlayers = ParseInt(_maxPlayers!.Text, _config.MaxPlayers),
            PerformanceThreads = _perfThreads!.IsChecked == true,
            WorkerThreads = ParseInt(_workerThreads!.Text, _config.WorkerThreads),
            CommunityServer = _community!.IsChecked == true,
            PublicIp = _publicIp!.Text.Trim(),
            PublicPortArg = ParseInt(_publicPort!.Text, _config.PublicPortArg),
            LogFormat = (_logFormat!.SelectedItem as string) ?? "",
            ExtraServerArgs = _extraArgs?.Text ?? "",
        };
        var args = ServerController.BuildLaunchArgs(temp, queryPort: 0);
        _commandPreview.Text = "PalServer-Win64-Shipping-Cmd.exe " + string.Join(" ", args).Replace("-QueryPort=0", "-QueryPort=<auto>");
    }

    /// <summary>Warn before saving if the Advanced box gained new content. Accept keeps it; Cancel reverts it.</summary>
    private bool ConfirmAdvancedArgs()
    {
        var current = (_extraArgs?.Text ?? "").Trim();
        if (current.Length == 0 || current == _extraArgsOriginal.Trim())
            return true;

        var choice = ChoiceDialog.Show(this, Strings.Settings_AdvancedArgsTitle,
            Strings.Settings_AdvancedArgsMessage,
            Strings.Settings_Accept, Strings.Common_Cancel);
        if (choice == 0)
            return true;

        _extraArgs!.Text = _extraArgsOriginal; // Cancel reverts the box; the user stays in the dialog.
        return false;
    }

    /// <summary>Append rows for keys present in the config that the catalog doesn't recognize (edited raw, and
    /// marked undocumented). Returns how many were added, so the caller can show a placeholder when there are none.</summary>
    private int AppendExtras(StackPanel stack, bool available, bool enabled)
    {
        var extras = available ? _gameSettings.LoadExtras() : System.Array.Empty<GameSettingsService.ExtraSetting>();
        if (extras.Count == 0)
            return 0;

        var defaults = available ? _gameSettings.LoadDefaults() : new Dictionary<string, string?>();
        foreach (var extra in extras)
        {
            var box = TextField(extra.Value, enabled);
            // Block only quotes/backslash (unrepresentable); commas/parens stay so tuple values are editable.
            box.PreviewTextInput += (_, e) =>
            {
                foreach (var c in e.Text)
                    if (c is '"' or '\\') { e.Handled = true; return; }
            };
            _extraInputs.Add((extra.Key, () => box.Text, extra.Value));
            // Only offer a reset when the default template actually has this key.
            ResetSpec? reset = enabled && defaults.TryGetValue(extra.Key, out var dv)
                ? TextReset(box, dv ?? "")
                : null;
            stack.Children.Add(Row(extra.Key, box, null, reset, DocStatus.Unknown));
        }
        return extras.Count;
    }

    private (FrameworkElement Input, ResetSpec Reset) BuildGameInput(GameSetting setting, string value, string defaultValue, bool enabled)
    {
        switch (setting.Type)
        {
            case SettingType.Bool:
            {
                var box = CheckField(IsTrue(value), enabled);
                _gameInputs.Add((setting, () => box.IsChecked == true ? "True" : "False", s => box.IsChecked = IsTrue(s), value));
                return (box, CheckReset(box, IsTrue(defaultValue)));
            }
            case SettingType.Enum:
            {
                var combo = ComboField(setting.Options?.ToArray() ?? new[] { value }, value, enabled);
                _gameInputs.Add((setting, () => (combo.SelectedItem as string) ?? "", s => SelectCombo(combo, s), value));
                return (combo, ComboReset(combo, defaultValue));
            }
            default:
            {
                if (setting.Secret)
                {
                    var secret = new SecretField(value, enabled);
                    _gameInputs.Add((setting, () => secret.Value, s => secret.SetValue(s), value));
                    return (secret.Element, new ResetSpec(
                        () => secret.SetValue(defaultValue), () => secret.Value == defaultValue, cb => secret.OnChanged(cb)));
                }
                var box = ValidatedTextField(setting.Label, value, enabled, setting.Type, setting.Min, setting.Max);
                _gameInputs.Add((setting, () => box.Text, s => box.Text = s, value));
                return (box, TextReset(box, defaultValue));
            }
        }
    }

    private static bool IsTrue(string s) => s.Trim().Equals("True", System.StringComparison.OrdinalIgnoreCase);

    private static void SelectCombo(ComboBox combo, string value)
    {
        if (combo.Items.Contains(value))
            combo.SelectedItem = value;
    }

    private bool Apply()
    {
        if (_section == SettingsSection.Advanced)
        {
            _config.ServerPriority = LabelToPriority(_priority!.SelectedItem as string);
            _config.ServerAffinityMask = ComputeAffinityMask();
            _config.Save();
            return true;
        }

        // ServerSettings also hosts the Launch Arguments tab. Launch args (launcher.json) are ours and always
        // safe to write, so we save them regardless of running state. The game ini can only be written while
        // stopped (a running server would overwrite it), so those edits are gated below.
        if (_serverRunning)
        {
            ApplyLaunchArgs();
            return true;
        }

        // Compare catalog keys by typed value, not raw text, so a hand-edited non-canonical value (bHardcore=false,
        // 1.0 vs 1.000000, enum casing) on a key the user didn't touch isn't rewritten canonical.
        var gameEdits = _gameInputs
            .Where(g => !SettingValidator.ValuesEqual(g.Setting.Type, g.Read(), g.Original))
            .ToDictionary(g => g.Setting.Key, g => g.Read());
        var extraEdits = _extraInputs
            .Where(x => x.Read() != x.Original)
            .ToDictionary(x => x.Key, x => x.Read());

        if (gameEdits.Count == 0 && extraEdits.Count == 0)
        {
            ApplyLaunchArgs();
            return true;
        }

        // Show exactly what will change and let the user confirm before we touch PalWorldSettings.ini.
        // Cap the list so a big change set (e.g. after Reset to defaults) can't make an oversized dialog;
        // the full set is written to the General log by the save either way.
        var changes = gameEdits.Concat(extraEdits).Select(e => $"{e.Key} = {e.Value}").ToList();
        const int maxShown = 20;
        var body = string.Join("\n", changes.Take(maxShown));
        if (changes.Count > maxShown)
            body += string.Format(Strings.Settings_ConfirmSaveMore, changes.Count - maxShown);
        if (ChoiceDialog.Show(this, Strings.Settings_ConfirmSaveTitle,
                string.Format(Strings.Settings_ConfirmSaveMessage, body), Strings.Common_Save, Strings.Common_Cancel) != 0)
            return false; // keep the dialog open so the user can review or cancel

        if (gameEdits.Count > 0 && !_gameSettings.Save(gameEdits, serverRunning: false, out var badGameKey))
        {
            ShowCorruptError(badGameKey);
            return false;
        }
        if (extraEdits.Count > 0 && !_gameSettings.SaveExtras(extraEdits, serverRunning: false, out var badExtraKey))
        {
            ShowCorruptError(badExtraKey);
            return false;
        }
        // Launch args save only after the ini write succeeds, so cancelling the confirm above saves nothing.
        ApplyLaunchArgs();
        return true;
    }

    /// <summary>Persist the launch-argument fields to launcher.json. Safe to write any time (it's ours; the
    /// running server never touches it), and applied on the next start.</summary>
    private void ApplyLaunchArgs()
    {
        _config.ServerPort = ParseInt(_port!.Text, _config.ServerPort);
        _config.MaxPlayers = ParseInt(_maxPlayers!.Text, _config.MaxPlayers);
        _config.PerformanceThreads = _perfThreads!.IsChecked == true;
        _config.WorkerThreads = ParseInt(_workerThreads!.Text, _config.WorkerThreads);
        _config.CommunityServer = _community!.IsChecked == true;
        _config.PublicIp = _publicIp!.Text.Trim();
        _config.PublicPortArg = ParseInt(_publicPort!.Text, _config.PublicPortArg);
        _config.LogFormat = (_logFormat!.SelectedItem as string) ?? "";
        _config.ExtraServerArgs = _extraArgs!.Text.Trim();
        _config.Save();
    }

    private void ShowCorruptError(string? key) =>
        ChoiceDialog.Show(this, Strings.Settings_NotSavedTitle,
            string.Format(Strings.Settings_NotSavedMessage, key), Strings.Common_OK);

    /// <summary>Validate every editable text field; block Save and list the failures if any are invalid.</summary>
    private void OnSave()
    {
        var errors = _validated
            .Where(f => f.Box.IsEnabled) // grayed-out game fields (server running) aren't user-editable
            .Select(f => (f.Label, Result: SettingValidator.Validate(f.Type, f.Box.Text, f.Min, f.Max, f.Required)))
            .Where(x => !x.Result.Ok)
            .Select(x => string.Format(Strings.Settings_ValidationError, x.Label, x.Result.Reason))
            .ToList();

        if (errors.Count > 0)
        {
            ChoiceDialog.Show(this, Strings.Settings_InvalidSettingsTitle, string.Join("\n", errors), Strings.Common_OK);
            return; // keep the dialog open so the user can fix the highlighted fields
        }

        if (_extraArgs is not null && !ConfirmAdvancedArgs())
            return;

        if (!Apply())
            return; // Apply showed its own message (e.g. an extra value would corrupt the config)

        _saved = true;
        Close();
    }

    /// <summary>
    /// A <see cref="TextField"/> that gates typed and pasted characters to what the type allows
    /// (<see cref="SettingValidator.IsCharAllowed"/>) and turns its border red while the assembled value
    /// is invalid or out of range. Registered in <see cref="_validated"/> for the blocking Save check.
    /// </summary>
    private TextBox ValidatedTextField(string label, string value, bool enabled, SettingType type,
        double? min = null, double? max = null, bool required = false)
    {
        var box = TextField(value, enabled);
        var field = new ValidatedField(label, box, type, min, max, required);
        _validated.Add(field);

        box.PreviewTextInput += (_, e) =>
        {
            foreach (var c in e.Text)
                if (!SettingValidator.IsCharAllowed(type, c)) { e.Handled = true; return; }
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string pasted && !SettingValidator.IsTextAllowed(type, pasted))
                e.CancelCommand();
        });
        box.TextChanged += (_, _) => Recolor(field);
        Recolor(field);
        return box;
    }

    private static void Recolor(ValidatedField field)
    {
        var (ok, _) = SettingValidator.Validate(field.Type, field.Box.Text, field.Min, field.Max, field.Required);
        field.Box.BorderBrush = ok ? NormalBorder : ErrorBorder;
    }

    // --- small control builders (dark theme) ---
    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6),
    };

    private static Border Banner(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x1E)),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 8),
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x80)), TextWrapping = TextWrapping.Wrap },
    };

    /// <summary>A muted description ending in a clickable link to the Palworld docs (opens the default browser).</summary>
    private static TextBlock DocBlurb(string text, string url, string linkText)
    {
        var block = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        block.Inlines.Add(text + " ");
        var link = new Hyperlink(new Run(linkText)) { NavigateUri = new Uri(url), Foreground = LinkFg };
        link.RequestNavigate += (_, e) => { OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
        block.Inlines.Add(link);
        return block;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser / launch blocked, nothing useful to do here.
        }
    }

    private Grid Row(string label, FrameworkElement input, string? tip = null, ResetSpec? reset = null, DocStatus doc = DocStatus.Documented, bool includeInBulkReset = true)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run(label));
        // Flag anything the official docs don't cover, so a plain-language label never reads as authoritative.
        if (doc != DocStatus.Documented)
            text.Inlines.Add(new Run(Strings.Settings_UndocumentedMarker) { Foreground = Muted, FontStyle = FontStyles.Italic });
        if (!string.IsNullOrEmpty(tip))
        {
            text.ToolTip = tip;
            input.ToolTip ??= tip; // also on the field itself, so hovering the control shows it too
        }
        Grid.SetColumn(text, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);

        if (reset is not null)
        {
            var resetButton = ResetButton(reset, includeInBulkReset);
            Grid.SetColumn(resetButton, 2);
            grid.Children.Add(resetButton);
        }
        return grid;
    }

    /// <summary>A field's reset behavior: set it to its default, report whether it's currently AT the default,
    /// and subscribe to its changes (so the ↺ button can hide itself when the value equals the default).</summary>
    private sealed record ResetSpec(System.Action Reset, System.Func<bool> IsDefault, System.Action<System.Action> Subscribe);

    private static ResetSpec TextReset(TextBox box, string defaultValue) => new(
        () => box.Text = defaultValue,
        () => box.Text == defaultValue,
        cb => box.TextChanged += (_, _) => cb());

    private static ResetSpec CheckReset(CheckBox box, bool defaultChecked) => new(
        () => box.IsChecked = defaultChecked,
        () => (box.IsChecked == true) == defaultChecked,
        cb => { box.Checked += (_, _) => cb(); box.Unchecked += (_, _) => cb(); });

    private static ResetSpec ComboReset(ComboBox combo, string defaultValue) => new(
        () => SelectCombo(combo, defaultValue),
        () => (combo.SelectedItem as string) == defaultValue,
        cb => combo.SelectionChanged += (_, _) => cb());

    /// <summary>A small "reset to default" (↺) button for one field, shown only while the value differs from
    /// its default; also collected for the "Reset to defaults" button.</summary>
    private Button ResetButton(ResetSpec spec, bool includeInBulk = true)
    {
        if (includeInBulk)
            _resetActions.Add(spec.Reset);
        var button = new Button
        {
            Content = "↺", Width = 26, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0, 1, 0, 1),
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Settings_ResetFieldTooltip,
        };
        button.Click += (_, _) => spec.Reset();
        void Refresh() => button.Visibility = spec.IsDefault() ? Visibility.Collapsed : Visibility.Visible;
        spec.Subscribe(Refresh);
        Refresh(); // hidden if the loaded value already equals the default
        return button;
    }

    private static TextBox TextField(string value, bool enabled) => new()
    {
        Text = value, IsEnabled = enabled, Background = FieldBg, Foreground = Fg,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)), Padding = new Thickness(4, 3, 4, 3),
        CaretBrush = Brushes.White,
    };

    private static CheckBox CheckField(bool value, bool enabled) => new()
    {
        IsChecked = value, IsEnabled = enabled, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center,
    };

    private static ComboBox ComboField(string[] options, string value, bool enabled)
    {
        var combo = new ComboBox { IsEnabled = enabled, Background = FieldBg, Foreground = Brushes.Black };
        // Preserve an out-of-list / odd-case original so Save never silently snaps it to the first option.
        var items = !string.IsNullOrEmpty(value) && !options.Contains(value)
            ? options.Prepend(value).ToArray()
            : options;
        foreach (var o in items)
            combo.Items.Add(o);
        combo.SelectedItem = items.Contains(value) ? value : items.FirstOrDefault();
        return combo;
    }

    private static Button MakeButton(string label, System.Action onClick)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7),
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, MinWidth = 90,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Reset every field in the open panel to its default (confirmed; still needs Save to apply).</summary>
    private void ResetAll()
    {
        if (ChoiceDialog.Show(this, Strings.Settings_ResetToDefaults,
                Strings.Settings_ResetAllMessage,
                Strings.Settings_ResetAllButton, Strings.Common_Cancel) != 0)
            return;
        foreach (var reset in _resetActions)
            reset();
    }

    private static string CategoryLabel(SettingCategory c) => c switch
    {
        SettingCategory.ServerAdmin => Strings.Settings_CategoryServerManagement,
        SettingCategory.Performance => Strings.Settings_CategoryPerformance,
        SettingCategory.Gameplay => Strings.Settings_CategoryGameplay,
        _ => Strings.Settings_CategoryGameBalance,
    };
}
