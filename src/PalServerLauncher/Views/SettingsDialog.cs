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
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Data-driven settings editor: one dialog with a three-tab top strip (Game Settings, Launcher Arguments,
/// CPU Affinity/Priority). Game Settings holds an inner sub-tab strip of the ini categories (Admin, Gameplay,
/// Game Balance, Performance, Undocumented) plus the search box. Launch args and CPU tuning write our config
/// (applied on the next start), the ini tabs are grouped by category, rendered by type, grayed while the
/// server is running (the ini must not change under a live server), and only CHANGED keys are written back
/// (unedited keys are preserved by the round-trip writer). Typed text fields gate their input and turn red on
/// invalid values, Save is blocked and lists any failures. Built in code to match the other dark dialogs.
/// </summary>
public sealed class SettingsDialog : Window
{
    private static readonly Brush NormalBorder = FieldBorder;
    private static readonly Brush ErrorBorder = Theme.Error;

    private const string ConfigDocsUrl = "https://docs.palworldgame.com/settings-and-operation/configuration/";

    private readonly LauncherConfig _config;
    private readonly GameSettingsService _gameSettings;
    private readonly bool _serverRunning;
    private bool _saved;

    // Launch-arg inputs (only built in the LaunchArgs section).
    private TextBox? _port, _queryPort, _maxPlayers, _workerThreads, _publicIp, _publicPort, _extraArgs;
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

    // Server Settings tabs. _tabs is the top strip (Game Settings / Launcher Arguments / CPU Affinity/Priority),
    // _iniTabs is the inner strip inside Game Settings (the ini categories). Only the inner ini sub-tabs are
    // registered in _searchTabs, so the search filters those and never Launch Arguments or CPU tuning.
    private TabControl? _tabs;
    private TabControl? _iniTabs;
    private TextBox? _searchBox;
    private UIElement? _presetRow; // the difficulty-preset buttons atop Game Settings, hidden while a search is active
    private readonly List<SearchTab> _searchTabs = new();

    // One filterable setting row: the row element plus the text the search matches against. Key is the literal
    // (English) ini variable name, Label/Description are the localized text the user sees, matching the spec.
    private sealed record SearchRow(FrameworkElement Element, string Key, string Label, string Description);

    private sealed class SearchGroup
    {
        public TextBlock? Header { get; init; }
        public List<SearchRow> Rows { get; } = new();
    }

    private sealed class SearchTab
    {
        public SearchTab(TabItem tab, string baseHeader, TextBlock emptyPlaceholder, List<SearchGroup> groups)
        {
            Tab = tab;
            BaseHeader = baseHeader;
            EmptyPlaceholder = emptyPlaceholder;
            Groups = groups;
        }
        public TabItem Tab { get; }
        public string BaseHeader { get; }
        public TextBlock EmptyPlaceholder { get; }
        public IReadOnlyList<SearchGroup> Groups { get; }
        public int LastMatchCount { get; set; }
    }

    private SettingsDialog(LauncherConfig config, GameSettingsService gameSettings, bool serverRunning)
    {
        _config = config;
        _gameSettings = gameSettings;
        _serverRunning = serverRunning;

        Title = Strings.Settings_ServerTitle;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 720;
        Height = 720;
        ShowInTaskbar = false;

        BuildServerSettings();
    }

    /// <summary>
    /// Build the three-tab dialog: a Game Settings tab (the ini categories as an inner sub-tab strip, with the
    /// difficulty presets and search box above it), a Launcher Arguments tab, and a CPU Affinity/Priority tab.
    /// One Save applies every tab.
    /// </summary>
    private void BuildServerSettings()
    {
        var gameAvailable = _gameSettings.EnsureInitialized();
        var gameEnabled = gameAvailable && !_serverRunning;
        var current = gameAvailable ? _gameSettings.Load() : new Dictionary<string, string?>();
        var defaults = gameAvailable ? _gameSettings.LoadDefaults() : new Dictionary<string, string?>();

        // The five ini sub-tabs, one category each, each with its own doc blurb (in addition to the shared
        // overview blurb hosted atop the Game Settings tab). The gameplay family link to the same features docs.
        _presetRow = BuildPresetRow(gameEnabled);
        var featuresUrl = "https://docs.palworldgame.com/settings-and-operation/configuration#features";
        var admin = BuildIniTab(
            DocBlurb(Strings.Settings_AdminBlurb,
                "https://docs.palworldgame.com/settings-and-operation/configuration#server-management", Strings.Settings_AdminBlurbLink),
            new[] { SettingCategory.ServerAdmin }, s => s.Doc != DocStatus.Unknown, gameEnabled, current, defaults);
        var gameplay = BuildIniTab(
            DocBlurb(Strings.Settings_GameplayBlurb, featuresUrl, Strings.Settings_WorldBlurbLink),
            new[] { SettingCategory.Gameplay }, s => s.Doc != DocStatus.Unknown, gameEnabled, current, defaults);
        var gameBalance = BuildIniTab(
            DocBlurb(Strings.Settings_GameBalanceBlurb, featuresUrl, Strings.Settings_WorldBlurbLink),
            new[] { SettingCategory.GameBalance }, s => s.Doc != DocStatus.Unknown, gameEnabled, current, defaults);
        var performance = BuildIniTab(
            DocBlurb(Strings.Settings_PerformanceBlurb, featuresUrl, Strings.Settings_WorldBlurbLink),
            new[] { SettingCategory.Performance }, s => s.Doc != DocStatus.Unknown, gameEnabled, current, defaults);
        var undoc = BuildUndocumentedTab(gameAvailable, gameEnabled, current, defaults);

        // Inner strip: the ini categories, styled lighter than the top strip. Admin first (server name and
        // password live there), then the gameplay family, then Undocumented.
        _iniTabs = new TabControl
        {
            Background = Theme.Window,
            BorderThickness = new Thickness(0),
        };
        if (Application.Current?.TryFindResource("DarkSubTabItem") is Style subTabStyle)
            _iniTabs.ItemContainerStyle = subTabStyle;
        var adminTab = new TabItem { Header = Strings.Settings_TabAdmin, Content = admin.Content };
        var gameplayTab = new TabItem { Header = CategoryLabel(SettingCategory.Gameplay), Content = gameplay.Content };
        var gameBalanceTab = new TabItem { Header = CategoryLabel(SettingCategory.GameBalance), Content = gameBalance.Content };
        var performanceTab = new TabItem { Header = CategoryLabel(SettingCategory.Performance), Content = performance.Content };
        var undocTab = new TabItem { Header = Strings.Settings_TabUndocumented, Content = undoc.Content };
        _iniTabs.Items.Add(adminTab);
        _iniTabs.Items.Add(gameplayTab);
        _iniTabs.Items.Add(gameBalanceTab);
        _iniTabs.Items.Add(performanceTab);
        _iniTabs.Items.Add(undocTab);

        // Only the ini sub-tabs are searchable (each BaseHeader must equal its tab header string).
        _searchTabs.Add(new SearchTab(adminTab, Strings.Settings_TabAdmin, admin.Placeholder, admin.Groups));
        _searchTabs.Add(new SearchTab(gameplayTab, CategoryLabel(SettingCategory.Gameplay), gameplay.Placeholder, gameplay.Groups));
        _searchTabs.Add(new SearchTab(gameBalanceTab, CategoryLabel(SettingCategory.GameBalance), gameBalance.Placeholder, gameBalance.Groups));
        _searchTabs.Add(new SearchTab(performanceTab, CategoryLabel(SettingCategory.Performance), performance.Placeholder, performance.Groups));
        _searchTabs.Add(new SearchTab(undocTab, Strings.Settings_TabUndocumented, undoc.Placeholder, undoc.Groups));

        // Game Settings host is a DockPanel so the inner TabControl fills. A StackPanel or an outer ScrollViewer
        // would hand the inner TabControl infinite height and break it. The blurb + preset row + search dock to
        // the top, _iniTabs fills the rest (each sub-tab already scrolls on its own).
        var gameTop = new StackPanel { Margin = new Thickness(18, 14, 18, 0) };
        gameTop.Children.Add(DocBlurb(Strings.Settings_WorldBlurb,
            "https://docs.palworldgame.com/settings-and-operation/configuration#features", Strings.Settings_WorldBlurbLink));
        gameTop.Children.Add(_presetRow);
        var gameHost = new DockPanel();
        DockPanel.SetDock(gameTop, Dock.Top);
        gameHost.Children.Add(gameTop);
        var searchRow = BuildSearchRow();
        DockPanel.SetDock(searchRow, Dock.Top);
        gameHost.Children.Add(searchRow);
        gameHost.Children.Add(_iniTabs); // fills the remaining space

        // Launch Arguments and CPU Affinity/Priority are launcher.json-backed (editable even while running). CPU
        // tuning carries the amber warning banner that used to be a pre-open modal.
        var launchStack = new StackPanel { Margin = new Thickness(18) };
        BuildLaunchArgs(launchStack);
        var launch = new ScrollViewer { Content = launchStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var advancedStack = new StackPanel { Margin = new Thickness(18) };
        advancedStack.Children.Add(Banner(Strings.Settings_AdvancedIntro));
        BuildAdvanced(advancedStack);
        var advanced = new ScrollViewer { Content = advancedStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // Top strip: the three concerns.
        _tabs = new TabControl
        {
            Background = Theme.Window,
            BorderThickness = new Thickness(0),
        };
        if (Application.Current?.TryFindResource("DarkTabItem") is Style tabStyle)
            _tabs.ItemContainerStyle = tabStyle;
        _tabs.Items.Add(new TabItem { Header = Strings.Settings_TabGameSettings, Content = gameHost });
        _tabs.Items.Add(new TabItem { Header = Strings.Settings_TabLaunchArgs, Content = launch });
        _tabs.Items.Add(new TabItem { Header = Strings.Settings_TabCpuAffinity, Content = advanced });

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
        root.Children.Add(_tabs); // last child fills the remaining space
        Content = root;
    }

    /// <summary>The search box docked above the tabs: filters the ini tabs by key, label, and description as
    /// you type (see <see cref="ApplySearch"/>). A placeholder shows when empty, a ✕ clears it.</summary>
    private FrameworkElement BuildSearchRow()
    {
        _searchBox = new TextBox
        {
            Background = FieldBg, Foreground = Fg, BorderThickness = new Thickness(0), CaretBrush = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Explicit left padding (not the app-wide 5px) so the caret sits just left of the placeholder below,
            // instead of on top of its first letter.
            Padding = new Thickness(4, 4, 0, 4),
        };

        var placeholder = new TextBlock
        {
            Text = Strings.Settings_SearchPlaceholder, Foreground = Muted, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };

        // A prominent red button with a white ✕ (the app-wide Button style rounds it and adds hover feedback).
        var clear = new Button
        {
            Content = "✕", Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 11,
            Foreground = Brushes.White, Background = Theme.Danger,
            Margin = new Thickness(6, 0, 2, 0), Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Settings_SearchClearTooltip,
        };
        clear.Click += (_, _) => { _searchBox.Clear(); _searchBox.Focus(); };

        _searchBox.TextChanged += (_, _) =>
        {
            var hasText = _searchBox.Text.Length > 0;
            placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            clear.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            ApplySearch(_searchBox.Text);
        };

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_searchBox, 0);
        Grid.SetColumn(placeholder, 0);
        Grid.SetColumn(clear, 1);
        inner.Children.Add(_searchBox);
        inner.Children.Add(placeholder);
        inner.Children.Add(clear);

        Loaded += (_, _) => _searchBox.Focus();
        return new Border
        {
            Background = FieldBg, BorderBrush = NormalBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 5, 4, 5), Margin = new Thickness(18, 12, 18, 4), Child = inner,
        };
    }

    /// <summary>
    /// Filter the ini tabs against the query: hide non-matching rows, hide a category header whose rows are all
    /// hidden, show each tab's match count in its header and a "no matches" placeholder when a tab has none.
    /// An empty query restores everything. Launch Arguments is untouched (it is not a <see cref="SearchTab"/>).
    /// </summary>
    private void ApplySearch(string query)
    {
        var trimmed = (query ?? "").Trim();
        var searching = trimmed.Length > 0;

        foreach (var tab in _searchTabs)
        {
            var matched = 0;
            foreach (var group in tab.Groups)
            {
                var groupMatches = 0;
                foreach (var row in group.Rows)
                {
                    var isMatch = !searching || SettingsSearch.Matches(trimmed, row.Key, row.Label, row.Description);
                    row.Element.Visibility = isMatch ? Visibility.Visible : Visibility.Collapsed;
                    if (isMatch)
                        groupMatches++;
                }
                if (group.Header is not null)
                    group.Header.Visibility = !searching || groupMatches > 0 ? Visibility.Visible : Visibility.Collapsed;
                matched += groupMatches;
            }
            tab.LastMatchCount = matched;
            tab.EmptyPlaceholder.Visibility = searching && matched == 0 ? Visibility.Visible : Visibility.Collapsed;
            tab.Tab.Header = searching ? $"{tab.BaseHeader} ({matched})" : tab.BaseHeader;
        }

        // The difficulty-preset buttons are context, not search results, so hide them while a search is active.
        if (_presetRow is not null)
            _presetRow.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;

        if (searching)
            AutoSwitchToMatches();
    }

    /// <summary>If the user is on an ini tab that now shows nothing while another ini tab has matches, switch to
    /// the first one that does. Leaves them alone otherwise (their tab has hits, or they are on Launch Arguments,
    /// which is not a searchable tab).</summary>
    private void AutoSwitchToMatches()
    {
        var selected = _searchTabs.FirstOrDefault(t => t.Tab == _iniTabs!.SelectedItem as TabItem);
        if (selected is null || selected.LastMatchCount > 0)
            return;
        var firstWithMatches = _searchTabs.FirstOrDefault(t => t.LastMatchCount > 0);
        if (firstWithMatches is not null)
            _iniTabs!.SelectedItem = firstWithMatches.Tab;
    }

    /// <summary>One ini category's sub-tab content: an optional doc blurb, then its rows. Built one category per
    /// call now (the sub-tab header names the category), so no in-tab category header is emitted.</summary>
    private (ScrollViewer Content, List<SearchGroup> Groups, TextBlock Placeholder) BuildIniTab(
        UIElement? blurb, IEnumerable<SettingCategory> categories, Func<GameSetting, bool> filter,
        bool gameEnabled, IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string?> defaults)
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        if (blurb is not null)
            stack.Children.Add(blurb);
        var groups = new List<SearchGroup>();
        foreach (var category in categories)
        {
            var settings = GameSettingsCatalog.All.Where(s => s.Category == category && filter(s)).ToList();
            if (settings.Count == 0)
                continue;
            var group = new SearchGroup();
            foreach (var setting in settings)
                group.Rows.Add(AddCatalogRow(stack, setting, gameEnabled, current, defaults));
            groups.Add(group);
        }
        var placeholder = SearchPlaceholder();
        stack.Children.Add(placeholder);
        return (new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, groups, placeholder);
    }

    /// <summary>The "no settings match your search" line shown in a tab whose rows are all filtered out. Hidden
    /// until a search hides everything in its tab.</summary>
    private static TextBlock SearchPlaceholder() => new()
    {
        Text = Strings.Settings_SearchNoMatches, Foreground = Muted,
        Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
    };

    /// <summary>The Undocumented tab: cataloged keys we can't confidently explain (typed, with a best-guess
    /// tooltip), then a divider and any keys in the config the catalog doesn't recognize (raw, future-proofing
    /// against a game update adding params before we catalog them).</summary>
    private (ScrollViewer Content, List<SearchGroup> Groups, TextBlock Placeholder) BuildUndocumentedTab(
        bool gameAvailable, bool gameEnabled,
        IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string?> defaults)
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(DocBlurb(
            Strings.Settings_UndocumentedBlurb,
            ConfigDocsUrl, Strings.Settings_UndocumentedBlurbLink));

        var groups = new List<SearchGroup>();

        var knownHeader = Header(Strings.Settings_UndocKnownHeader);
        stack.Children.Add(knownHeader);
        var knownGroup = new SearchGroup { Header = knownHeader };
        foreach (var setting in GameSettingsCatalog.All.Where(s => s.Doc == DocStatus.Unknown))
            knownGroup.Rows.Add(AddCatalogRow(stack, setting, gameEnabled, current, defaults));
        groups.Add(knownGroup);

        var newHeader = Header(Strings.Settings_UndocNewHeader);
        stack.Children.Add(newHeader);
        var newGroup = new SearchGroup { Header = newHeader };
        var extras = AppendExtras(stack, gameAvailable, gameEnabled);
        if (extras.Count == 0)
        {
            // No unrecognized keys: this informational line stands in for the (absent) rows. Give it empty
            // searchable text so it hides during an active search and reappears when the box is cleared.
            var none = new TextBlock
            {
                Text = Strings.Settings_UndocNoneRecognized,
                Foreground = Fg, Margin = new Thickness(0, 2, 0, 0),
            };
            stack.Children.Add(none);
            newGroup.Rows.Add(new SearchRow(none, "", "", ""));
        }
        else
        {
            newGroup.Rows.AddRange(extras);
        }
        groups.Add(newGroup);

        var placeholder = SearchPlaceholder();
        stack.Children.Add(placeholder);
        return (new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, groups, placeholder);
    }

    /// <summary>Add one catalog key's row (input built by type, reset when a default exists and it's editable,
    /// undocumented marker driven by <see cref="GameSetting.Doc"/>).</summary>
    private SearchRow AddCatalogRow(StackPanel stack, GameSetting setting, bool gameEnabled,
        IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string?> defaults)
    {
        var value = current.TryGetValue(setting.Key, out var v) ? v ?? "" : "";
        var hasDefault = defaults.TryGetValue(setting.Key, out var dv);
        // Tooltip leads with the real ini key (the "true" name) so it's discoverable, then the description.
        var label = CatalogText.Label(setting);
        var desc = CatalogText.Description(setting);
        var tip = string.IsNullOrEmpty(desc) ? setting.Key : $"{setting.Key}\n{desc}";
        // An app-preferred default (e.g. RESTAPIEnabled = True) overrides the game default for reset: the ↺
        // resets toward it (so it only shows when the value differs, i.e. REST is off) and the key is left out
        // of bulk "Reset to defaults" so a blanket reset can't disable a feature the launcher relies on.
        var resetTarget = setting.AppDefault ?? dv ?? "";
        var (input, reset) = BuildGameInput(setting, value, resetTarget, gameEnabled);
        if (setting.NoServerEffect && input is ComboBox noEffectCombo)
            WarnNoServerEffectOnUse(setting, noEffectCombo);
        // No reset (↺) on secret fields, don't let "Reset to defaults" silently blank a password.
        var offerReset = gameEnabled && !setting.Secret && (setting.AppDefault is not null || hasDefault);
        var row = Row(label, input, tip, offerReset ? reset : null, setting.Doc,
            includeInBulkReset: setting.AppDefault is null);
        stack.Children.Add(row);
        // Searchable text: the literal ini key (always English, so it works in any UI language) plus the
        // localized label and description the user actually sees.
        return new SearchRow(row, setting.Key, label, desc);
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
                string.Format(Strings.Settings_NoEffectMessage, CatalogText.Label(setting)), Strings.Settings_GotIt)));
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
            var button = MakeButton(PresetLabel(presetName), () => ApplyPreset(presetName));
            button.IsEnabled = enabled;
            button.Margin = new Thickness(0, 0, 6, 4);
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>Localized display label for a difficulty preset. The canonical name (Casual/Normal/Hard/Hardcore)
    /// stays the lookup key for <see cref="DifficultyPresets"/>, only the shown text is translated.</summary>
    private static string PresetLabel(string canonicalName) => canonicalName switch
    {
        "Casual" => Strings.Settings_PresetCasual,
        "Normal" => Strings.Settings_PresetNormal,
        "Hard" => Strings.Settings_PresetHard,
        "Hardcore" => Strings.Settings_PresetHardcore,
        _ => canonicalName,
    };

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
        var label = PresetLabel(presetName);

        if (changes.Count == 0)
        {
            ChoiceDialog.Show(this, string.Format(Strings.Settings_PresetTitle, label),
                string.Format(Strings.Settings_PresetNoChanges, label), Strings.Common_OK);
            return;
        }

        var list = string.Join("\n", changes.Select(c => $"{c.Key} = {c.Value}"));
        var message = string.Format(Strings.Settings_PresetConfirmMessage, label, list);
        if (ChoiceDialog.Show(this, string.Format(Strings.Settings_PresetTitle, label), message, Strings.Settings_Yes, Strings.Settings_No) != 0)
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
        ChoiceDialog.Show(this, string.Format(Strings.Settings_PresetAppliedTitle, label),
            string.Format(Strings.Settings_PresetAppliedMessage, label, changes.Count), Strings.Common_OK);
    }

    public static bool ShowServerSettings(Window? owner, LauncherConfig config, GameSettingsService gs, bool serverRunning)
    {
        var dialog = new SettingsDialog(config, gs, serverRunning) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private void BuildLaunchArgs(StackPanel stack)
    {
        stack.Children.Add(DocBlurb(Strings.Settings_LaunchArgsIntro,
            "https://docs.palworldgame.com/settings-and-operation/arguments", Strings.Settings_LaunchArgsBlurbLink));

        // Live, read-only command preview: not editable, but selectable so it can be copied.
        _commandPreview = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 46,
            Background = Theme.Sunken,
            Foreground = Theme.CodeFg,
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
        // Blank when auto (0), so the field reads as "auto" rather than showing a 0 the user never set.
        _queryPort = ValidatedTextField(Strings.Settings_ValidateQueryPort, _config.QueryPort > 0 ? _config.QueryPort.ToString(CultureInfo.InvariantCulture) : "", true, SettingType.Int, min: 0, max: 65535);
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
        _queryPort.TextChanged += OnLaunchFieldChanged;
        _logFormat.SelectionChanged += OnLaunchFieldChanged;

        var d = new LauncherConfig(); // built-in defaults for the reset (↺) actions
        // Launch Arguments (launcher.json) each keep their own ↺ reset but stay out of the bulk "Reset to
        // defaults", which is scoped to the game ini tabs only (same for the Advanced tab below).
        stack.Children.Add(Row(Strings.Settings_RowListenPort, _port,
            Strings.Settings_TipListenPort,
            TextReset(_port, d.ServerPort.ToString(CultureInfo.InvariantCulture)), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowQueryPort, _queryPort,
            Strings.Settings_TipQueryPort,
            TextReset(_queryPort, ""), includeInBulkReset: false)); // reset = blank = auto
        stack.Children.Add(Row(Strings.Settings_RowMaxPlayers, _maxPlayers,
            Strings.Settings_TipMaxPlayers,
            TextReset(_maxPlayers, d.MaxPlayers.ToString(CultureInfo.InvariantCulture)), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowPerfThreads, _perfThreads,
            Strings.Settings_TipPerfThreads,
            CheckReset(_perfThreads, d.PerformanceThreads), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowWorkerThreads, _workerThreads,
            Strings.Settings_TipWorkerThreads,
            TextReset(_workerThreads, d.WorkerThreads.ToString(CultureInfo.InvariantCulture)), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowCommunity, _community,
            Strings.Settings_TipCommunity,
            CheckReset(_community, d.CommunityServer), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowPublicIp, _publicIp,
            Strings.Settings_TipPublicIp,
            TextReset(_publicIp, d.PublicIp), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowPublicPort, _publicPort,
            Strings.Settings_TipPublicPort,
            TextReset(_publicPort, d.PublicPortArg.ToString(CultureInfo.InvariantCulture)), includeInBulkReset: false));
        stack.Children.Add(Row(Strings.Settings_RowLogFormat, _logFormat,
            Strings.Settings_TipLogFormat,
            ComboReset(_logFormat, d.LogFormat), includeInBulkReset: false));

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

    /// <summary>The Advanced tab body: low-level process tuning applied after launch. The caller adds the amber
    /// warning banner above this (see BuildServerSettings).</summary>
    private void BuildAdvanced(StackPanel stack)
    {
        stack.Children.Add(Header(Strings.Settings_PriorityAffinityHeader));

        _priority = ComboField(
            new[] { Strings.Settings_PriorityBelowNormal, Strings.Settings_PriorityNormal, Strings.Settings_PriorityAboveNormal, Strings.Settings_PriorityHigh },
            PriorityToLabel(_config.ServerPriority), true);
        stack.Children.Add(Row(Strings.Settings_RowPriority, _priority,
            Strings.Settings_TipPriority,
            ComboReset(_priority, Strings.Settings_PriorityNormal), includeInBulkReset: false));

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
    }

    // The config stores the canonical ProcessPriorityClass name ("BelowNormal" / "Normal" / "AboveNormal" /
    // "High"), which ServerController.MapPriority turns into the real priority. Only the DISPLAY label is
    // localized, so these map between the stored value and the (translated) dropdown label.
    private static string PriorityToLabel(string priority) => priority switch
    {
        "BelowNormal" => Strings.Settings_PriorityBelowNormal,
        "AboveNormal" => Strings.Settings_PriorityAboveNormal,
        "High" => Strings.Settings_PriorityHigh,
        _ => Strings.Settings_PriorityNormal,
    };

    private static string LabelToPriority(string? label)
    {
        if (label == Strings.Settings_PriorityBelowNormal) return "BelowNormal";
        if (label == Strings.Settings_PriorityAboveNormal) return "AboveNormal";
        if (label == Strings.Settings_PriorityHigh) return "High";
        return "Normal";
    }

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
        var queryPort = ParseInt(_queryPort!.Text, 0); // blank / 0 = auto, rendered as <auto> below
        var args = ServerController.BuildLaunchArgs(temp, queryPort);
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
    private List<SearchRow> AppendExtras(StackPanel stack, bool available, bool enabled)
    {
        var rows = new List<SearchRow>();
        var extras = available ? _gameSettings.LoadExtras() : System.Array.Empty<GameSettingsService.ExtraSetting>();
        if (extras.Count == 0)
            return rows;

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
            var row = Row(extra.Key, box, null, reset, DocStatus.Unknown);
            stack.Children.Add(row);
            // The shown label IS the raw key (these keys aren't in the catalog), and there's no description.
            rows.Add(new SearchRow(row, extra.Key, extra.Key, ""));
        }
        return rows;
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
                var combo = ComboField(setting.Options?.ToArray() ?? new[] { value }, value, enabled, setting.Key);
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
                var box = ValidatedTextField(CatalogText.Label(setting), value, enabled, setting.Type, setting.Min, setting.Max);
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
        // The launcher.json tabs (Launch Arguments + Advanced process tuning) are ours and always safe to write,
        // so ApplyLauncherConfig saves them regardless of running state. The game ini can only be written while
        // stopped (a running server would overwrite it), so those edits are gated below.
        if (_serverRunning)
        {
            ApplyLauncherConfig();
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
            ApplyLauncherConfig();
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
        // Launcher.json saves only after the ini write succeeds, so cancelling the confirm above saves nothing.
        ApplyLauncherConfig();
        return true;
    }

    /// <summary>Persist the launcher.json settings: the launch-argument fields plus the Advanced tab's process
    /// priority and CPU affinity. Safe to write any time (it's ours, the running server never touches it). Launch
    /// args apply on the next start, the affinity re-pin picks up a live change from the health probe.</summary>
    private void ApplyLauncherConfig()
    {
        _config.ServerPort = ParseInt(_port!.Text, _config.ServerPort);
        _config.QueryPort = ParseInt(_queryPort!.Text, 0); // blank / 0 = auto
        _config.MaxPlayers = ParseInt(_maxPlayers!.Text, _config.MaxPlayers);
        _config.PerformanceThreads = _perfThreads!.IsChecked == true;
        _config.WorkerThreads = ParseInt(_workerThreads!.Text, _config.WorkerThreads);
        _config.CommunityServer = _community!.IsChecked == true;
        _config.PublicIp = _publicIp!.Text.Trim();
        _config.PublicPortArg = ParseInt(_publicPort!.Text, _config.PublicPortArg);
        _config.LogFormat = (_logFormat!.SelectedItem as string) ?? "";
        _config.ExtraServerArgs = _extraArgs!.Text.Trim();
        _config.ServerPriority = LabelToPriority(_priority!.SelectedItem as string);
        _config.ServerAffinityMask = ComputeAffinityMask();
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

    private static Border Banner(string text) => new()
    {
        Background = Theme.BannerBg,
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 8),
        Child = new TextBlock { Text = text, Foreground = Theme.BannerFg, TextWrapping = TextWrapping.Wrap },
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
            Foreground = Fg, Background = Theme.Control,
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Settings_ResetFieldTooltip,
        };
        button.Click += (_, _) => spec.Reset();
        void Refresh() => button.Visibility = spec.IsDefault() ? Visibility.Collapsed : Visibility.Visible;
        spec.Subscribe(Refresh);
        Refresh(); // hidden if the loaded value already equals the default
        return button;
    }

    private static TextBox TextField(string value, bool enabled) => Field(value, enabled);

    private static CheckBox CheckField(bool value, bool enabled) => new()
    {
        IsChecked = value, IsEnabled = enabled, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center,
    };

    private static ComboBox ComboField(string[] options, string value, bool enabled, string? optionKey = null)
    {
        var combo = new ComboBox { IsEnabled = enabled, Background = FieldBg, Foreground = Brushes.Black };
        // Preserve an out-of-list / odd-case original so Save never silently snaps it to the first option.
        var items = !string.IsNullOrEmpty(value) && !options.Contains(value)
            ? options.Prepend(value).ToArray()
            : options;
        foreach (var o in items)
            combo.Items.Add(o);
        combo.SelectedItem = items.Contains(value) ? value : items.FirstOrDefault();
        // For enum game settings the item stays the canonical value (so select / reset / save are unchanged),
        // but each is DISPLAYED through a localized option label.
        if (optionKey is not null)
        {
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding { Converter = new OptionLabelConverter(optionKey) });
            combo.ItemTemplate = new DataTemplate { VisualTree = factory };
        }
        return combo;
    }

    private sealed class OptionLabelConverter(string settingKey) : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            CatalogText.Option(settingKey, value as string ?? "");

        public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
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
