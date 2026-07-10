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

namespace PalServerLauncher.Views;

/// <summary>Which slice of the settings this dialog instance shows.</summary>
public enum SettingsSection
{
    /// <summary>Command-line launch args (our config; apply on next start, always editable).</summary>
    LaunchArgs,
    /// <summary>PalWorldSettings.ini "Server management" keys.</summary>
    Admin,
    /// <summary>PalWorldSettings.ini gameplay keys (Performance + Gameplay + Game balance).</summary>
    Game,
    /// <summary>PalWorldSettings.ini keys the catalog doesn't cover (auto-discovered, incl. future game params).</summary>
    Extra,
    /// <summary>Low-level process tuning (priority, CPU affinity) - behind a danger-zone warning.</summary>
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

    // Game-setting inputs: (setting, read-current-value, original-value).
    private readonly List<(GameSetting Setting, System.Func<string> Read, string Original)> _gameInputs = new();

    // Extra (non-catalog) inputs: (key, read-current-value, original-value).
    private readonly List<(string Key, System.Func<string> Read, string Original)> _extraInputs = new();

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

        Title = section switch
        {
            SettingsSection.LaunchArgs => "Launch Arguments",
            SettingsSection.Admin => "Admin Settings",
            SettingsSection.Extra => "New Settings",
            SettingsSection.Advanced => "Advanced Settings",
            _ => "Game Settings",
        };
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 660;
        Height = section switch
        {
            SettingsSection.LaunchArgs => 560,
            SettingsSection.Advanced => 440,
            _ => 680,
        };
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        if (section == SettingsSection.LaunchArgs)
        {
            BuildLaunchArgs(stack);
        }
        else if (section == SettingsSection.Extra)
        {
            BuildExtraSettings(stack);
        }
        else if (section == SettingsSection.Advanced)
        {
            BuildAdvanced(stack);
        }
        else
        {
            var gameAvailable = _gameSettings.EnsureInitialized();
            var gameEnabled = gameAvailable && !_serverRunning;
            var current = gameAvailable ? _gameSettings.Load() : new Dictionary<string, string?>();
            var defaults = gameAvailable ? _gameSettings.LoadDefaults() : new Dictionary<string, string?>();

            stack.Children.Add(section == SettingsSection.Admin
                ? DocBlurb("Server management - server name, passwords, player limit, and the REST / RCON APIs. Full reference:",
                    "https://docs.palworldgame.com/settings-and-operation/configuration#server-management", "Server management docs")
                : DocBlurb("Gameplay and balance - difficulty, EXP / capture / drop rates, damage multipliers, and world features. Full reference:",
                    "https://docs.palworldgame.com/settings-and-operation/configuration#features", "Features docs"));

            if (_serverRunning)
                stack.Children.Add(Banner("The server is running - these settings are read-only. Stop it to edit them."));
            else if (!gameAvailable)
                stack.Children.Add(Banner("Game settings unavailable - install the server first (no DefaultPalWorldSettings.ini found)."));

            foreach (var category in CategoriesFor(section))
            {
                stack.Children.Add(Header(CategoryLabel(category)));
                foreach (var setting in GameSettingsCatalog.All.Where(s => s.Category == category))
                {
                    var value = current.TryGetValue(setting.Key, out var v) ? v ?? "" : "";
                    var hasDefault = defaults.TryGetValue(setting.Key, out var dv);
                    // Tooltip leads with the real ini key (the "true" name) so it's discoverable, then the description.
                    var tip = string.IsNullOrEmpty(setting.Description) ? setting.Key : $"{setting.Key}\n{setting.Description}";
                    var (input, reset) = BuildGameInput(setting, value, dv ?? "", gameEnabled);
                    // No reset (↺) on secret fields - don't let "Reset to defaults" silently blank a password.
                    stack.Children.Add(Row(setting.Label, input, tip, gameEnabled && hasDefault && !setting.Secret ? reset : null));
                }
            }
        }

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 10, 18, 14) };
        if (_resetActions.Count > 0)
            buttons.Children.Add(MakeButton("Reset to defaults", ResetAll));
        buttons.Children.Add(MakeButton("Save", OnSave));
        buttons.Children.Add(MakeButton("Cancel", Close));

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
        Content = root;
    }

    public static bool ShowLaunchArgs(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.LaunchArgs, config, gs, serverRunning);

    public static bool ShowAdmin(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.Admin, config, gs, serverRunning);

    public static bool ShowGame(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.Game, config, gs, serverRunning);

    public static bool ShowExtra(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.Extra, config, gs, serverRunning);

    public static bool ShowAdvanced(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning) =>
        Show(owner, SettingsSection.Advanced, config, gs, serverRunning);

    private static bool Show(Window? owner, SettingsSection section, LauncherConfig config, GameSettingsService gs, bool serverRunning)
    {
        var dialog = new SettingsDialog(section, config, gs, serverRunning) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private static IEnumerable<SettingCategory> CategoriesFor(SettingsSection section) => section == SettingsSection.Admin
        ? new[] { SettingCategory.ServerAdmin }
        : new[] { SettingCategory.Performance, SettingCategory.Gameplay, SettingCategory.GameBalance };

    private void BuildLaunchArgs(StackPanel stack)
    {
        stack.Children.Add(new TextBlock
        {
            Text = "The exact command line used to launch the dedicated server (rebuilt live below). Where an "
                 + "argument here and a PalWorldSettings.ini setting control the same thing, the argument wins - a "
                 + "field left blank, 0, or at its default is left off, so the ini value is used instead. Most game "
                 + "settings have no argument and live only in the ini (edit those under Game Settings / Admin "
                 + "Settings). Changes take effect on the next start.",
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
        portNote.Inlines.Add(new Run("Listen port") { FontWeight = FontWeights.SemiBold, Foreground = Fg });
        portNote.Inlines.Add(" is the port the server binds to and players connect to. ");
        portNote.Inlines.Add(new Run("Public port") { FontWeight = FontWeights.SemiBold, Foreground = Fg });
        portNote.Inlines.Add(" (community servers only) sets what the server advertises to the public list - it does NOT change the listen port.");
        stack.Children.Add(portNote);

        _port = ValidatedTextField("Listen port", _config.ServerPort.ToString(), true, SettingType.Int, min: 1, max: 65535, required: true);
        _maxPlayers = ValidatedTextField("Max players", _config.MaxPlayers.ToString(), true, SettingType.Int, min: 0);
        _perfThreads = CheckField(_config.PerformanceThreads, true);
        _workerThreads = ValidatedTextField("Worker threads", _config.WorkerThreads.ToString(), true, SettingType.Int, min: 0);
        _community = CheckField(_config.CommunityServer, true);
        _publicIp = ValidatedTextField("Public IP", _config.PublicIp, true, SettingType.IpAddress);
        _publicPort = ValidatedTextField("Public port", _config.PublicPortArg.ToString(), true, SettingType.Int, min: 0, max: 65535);
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
        stack.Children.Add(Row("Listen port (-port)", _port,
            "The UDP port the server binds to - what players connect to (Palworld default 8211). Always applied. This is not the Public port below.",
            TextReset(_port, d.ServerPort.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row("Max players (-players, 0 = use game setting)", _maxPlayers,
            "Overrides ServerPlayerMaxNum in the ini when set. 0 = leave the argument off and use the game setting.",
            TextReset(_maxPlayers, d.MaxPlayers.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row("Performance threads", _perfThreads,
            "Adds -useperfthreads -NoAsyncLoadingThread -UseMultithreadForDS. Recommended on for a dedicated server.",
            CheckReset(_perfThreads, d.PerformanceThreads)));
        stack.Children.Add(Row("Worker threads (0 = auto)", _workerThreads,
            "-NumberOfWorkerThreadsServer. 0 = auto (left off). Only used when Performance threads is on.",
            TextReset(_workerThreads, d.WorkerThreads.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row("Community/public server (-publiclobby)", _community,
            "-publiclobby: list the server on the public/community browser. Off = private (players join by IP).",
            CheckReset(_community, d.CommunityServer)));
        stack.Children.Add(Row("Public IP (community, blank = auto)", _publicIp,
            "-publicip for community servers (blank = auto-detect). Advertising only; does not change what the server binds to.",
            TextReset(_publicIp, d.PublicIp)));
        stack.Children.Add(Row("Public port (community, 0 = use game setting)", _publicPort,
            "-publicport for community servers: the external port advertised to the public list. Does NOT change the listen port. 0 = left off (use the ini value).",
            TextReset(_publicPort, d.PublicPortArg.ToString(CultureInfo.InvariantCulture))));
        stack.Children.Add(Row("Log format", _logFormat,
            "-logformat (Text or Json). Blank = left off; the ini/default applies.",
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
            Text = "Raw arguments appended to the command line verbatim (space- or line-separated). Some Unreal servers accept map / game-class overrides here. Only change this if you know what you're doing.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
        });
        advancedBody.Children.Add(_extraArgs);

        stack.Children.Add(new Expander
        {
            Header = "Advanced", IsExpanded = false, Foreground = Fg, Margin = new Thickness(0, 12, 0, 0), Content = advancedBody,
        });

        UpdatePreview();
    }

    /// <summary>The Advanced Settings section: low-level process tuning applied after launch (danger-zone gated).</summary>
    private void BuildAdvanced(StackPanel stack)
    {
        stack.Children.Add(new TextBlock
        {
            Text = "Low-level settings applied to the server process after it launches. The wrong values here can "
                 + "hurt performance - change only if you know what you're doing. Takes effect on the next start.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        stack.Children.Add(Header("Process priority & CPU affinity"));

        _priority = ComboField(new[] { "Below normal", "Normal", "Above normal", "High" }, PriorityToLabel(_config.ServerPriority), true);
        stack.Children.Add(Row("Process priority", _priority,
            "Windows priority for the server process. Above normal / High can help under load but can starve other apps and hurt performance; RealTime isn't offered (unsafe).",
            ComboReset(_priority, "Normal")));

        var cores = Environment.ProcessorCount;
        _affinityBoxes = new CheckBox[cores];
        var allCores = _config.ServerAffinityMask == 0;
        var affinityPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < cores; i++)
        {
            var box = new CheckBox
            {
                Content = $"Core {i}", Foreground = Fg, MinWidth = 68, Margin = new Thickness(0, 0, 10, 4),
                IsChecked = allCores || (_config.ServerAffinityMask & (1L << i)) != 0,
            };
            _affinityBoxes[i] = box;
            affinityPanel.Children.Add(box);
        }
        stack.Children.Add(new TextBlock
        {
            Text = "CPU affinity - pin the server to specific cores (all checked = no restriction / use every core):",
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

        var choice = ChoiceDialog.Show(this, "Advanced arguments",
            "You've put custom arguments in the Advanced box. They're passed to the server exactly as typed - only keep them if you know what you're doing.",
            "Accept", "Cancel");
        if (choice == 0)
            return true;

        _extraArgs!.Text = _extraArgsOriginal; // Cancel reverts the box; the user stays in the dialog.
        return false;
    }

    private void BuildExtraSettings(StackPanel stack)
    {
        var available = _gameSettings.EnsureInitialized();
        var enabled = available && !_serverRunning;

        if (_serverRunning)
            stack.Children.Add(Banner("The server is running - these are read-only. Stop it to edit them."));
        else if (!available)
            stack.Children.Add(Banner("Unavailable - install the server first (no config found)."));

        stack.Children.Add(DocBlurb(
            "Settings found in PalWorldSettings.ini that this launcher has no dedicated editor for (including "
            + "any the game adds in a future update). Values are edited raw - only change these if you know the "
            + "format. Look up what each one does in the",
            ConfigDocsUrl, "Palworld configuration reference"));

        var extras = available ? _gameSettings.LoadExtras() : System.Array.Empty<GameSettingsService.ExtraSetting>();
        if (extras.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Nothing here - every setting in your config is covered by the other panels.",
                Foreground = Fg, Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

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
            stack.Children.Add(Row(extra.Key, box, null, reset));
        }
    }

    private (FrameworkElement Input, ResetSpec Reset) BuildGameInput(GameSetting setting, string value, string defaultValue, bool enabled)
    {
        switch (setting.Type)
        {
            case SettingType.Bool:
            {
                var box = CheckField(IsTrue(value), enabled);
                _gameInputs.Add((setting, () => box.IsChecked == true ? "True" : "False", value));
                return (box, CheckReset(box, IsTrue(defaultValue)));
            }
            case SettingType.Enum:
            {
                var combo = ComboField(setting.Options?.ToArray() ?? new[] { value }, value, enabled);
                _gameInputs.Add((setting, () => (combo.SelectedItem as string) ?? "", value));
                return (combo, ComboReset(combo, defaultValue));
            }
            default:
            {
                if (setting.Secret)
                {
                    var secret = new SecretField(value, enabled);
                    _gameInputs.Add((setting, () => secret.Value, value));
                    return (secret.Element, new ResetSpec(
                        () => secret.SetValue(defaultValue), () => secret.Value == defaultValue, cb => secret.OnChanged(cb)));
                }
                var box = ValidatedTextField(setting.Label, value, enabled, setting.Type, setting.Min, setting.Max);
                _gameInputs.Add((setting, () => box.Text, value));
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
        if (_section == SettingsSection.LaunchArgs)
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
            return true;
        }

        if (_section == SettingsSection.Advanced)
        {
            _config.ServerPriority = LabelToPriority(_priority!.SelectedItem as string);
            _config.ServerAffinityMask = ComputeAffinityMask();
            _config.Save();
            return true;
        }

        if (_section == SettingsSection.Extra)
        {
            if (_serverRunning)
                return true;
            var edits = _extraInputs.Where(x => x.Read() != x.Original).ToDictionary(x => x.Key, x => x.Read());
            if (edits.Count == 0 || _gameSettings.SaveExtras(edits, serverRunning: false, out var badKey))
                return true;

            ChoiceDialog.Show(this, "Not saved",
                $"'{badKey}' has a value that would break the config format (a stray comma, quote, or unbalanced "
                + "parenthesis). Nothing was saved - fix it and try again.", "OK");
            return false;
        }

        // Admin / Game: game settings -> ini (only changed keys, and only when stopped; the service enforces both).
        if (!_serverRunning)
        {
            // Compare by typed value, not raw text, so a hand-edited non-canonical value (bHardcore=false,
            // 1.0 vs 1.000000, enum casing) on a key the user didn't touch isn't rewritten canonical.
            var edits = _gameInputs
                .Where(g => !SettingValidator.ValuesEqual(g.Setting.Type, g.Read(), g.Original))
                .ToDictionary(g => g.Setting.Key, g => g.Read());
            if (edits.Count > 0 && !_gameSettings.Save(edits, serverRunning: false, out var badKey))
            {
                ChoiceDialog.Show(this, "Not saved",
                    $"'{badKey}' has a value that would break the config format (a stray comma, quote, or "
                    + "unbalanced parenthesis). Nothing was saved - fix it and try again.", "OK");
                return false;
            }
        }
        return true;
    }

    /// <summary>Validate every editable text field; block Save and list the failures if any are invalid.</summary>
    private void OnSave()
    {
        var errors = _validated
            .Where(f => f.Box.IsEnabled) // grayed-out game fields (server running) aren't user-editable
            .Select(f => (f.Label, Result: SettingValidator.Validate(f.Type, f.Box.Text, f.Min, f.Max, f.Required)))
            .Where(x => !x.Result.Ok)
            .Select(x => $"{x.Label} is invalid. Must be {x.Result.Reason}.")
            .ToList();

        if (errors.Count > 0)
        {
            ChoiceDialog.Show(this, "Invalid settings", string.Join("\n", errors), "OK");
            return; // keep the dialog open so the user can fix the highlighted fields
        }

        if (_section == SettingsSection.LaunchArgs && !ConfirmAdvancedArgs())
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
            // No default browser / launch blocked - nothing useful to do here.
        }
    }

    private Grid Row(string label, FrameworkElement input, string? tip = null, ResetSpec? reset = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
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
            var resetButton = ResetButton(reset);
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
    private Button ResetButton(ResetSpec spec)
    {
        _resetActions.Add(spec.Reset);
        var button = new Button
        {
            Content = "↺", Width = 26, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0, 1, 0, 1),
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Reset this field to its default",
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
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, MinWidth = 90,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Reset every field in the open panel to its default (confirmed; still needs Save to apply).</summary>
    private void ResetAll()
    {
        if (ChoiceDialog.Show(this, "Reset to defaults",
                "Reset every field in this panel to its default value? You'll still need to Save to apply.",
                "Reset all", "Cancel") != 0)
            return;
        foreach (var reset in _resetActions)
            reset();
    }

    private static string CategoryLabel(SettingCategory c) => c switch
    {
        SettingCategory.ServerAdmin => "Server management",
        SettingCategory.Performance => "Performance",
        SettingCategory.Gameplay => "Gameplay",
        _ => "Game balance",
    };
}
