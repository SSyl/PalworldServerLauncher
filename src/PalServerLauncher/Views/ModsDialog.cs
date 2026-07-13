using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Rest;

namespace PalServerLauncher.Views;

/// <summary>
/// Manage Palworld's built-in Steam Workshop server mods: a master toggle, an optional Steam sign-in for
/// downloading Workshop items, and a list of tracked mods (Workshop ids the launcher downloads plus dropped-in
/// mods the user placed in Mods\Workshop themselves). Mutates the config in place and returns true if the user
/// saved; mods deploy on the next server start/restart. Built in code to match the other dark dialogs.
/// </summary>
public sealed class ModsDialog : Window
{
    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    private static readonly Brush RowBorder = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C));

    private readonly LauncherConfig _config;
    private readonly ModService _modService;
    private readonly Func<string, Task<bool>> _connectSteam;
    private bool _saved;

    private readonly CheckBox _modsEnabled;
    private readonly TextBox _username;
    private readonly TextBlock _steamStatus;
    private readonly Button _connectButton;
    private readonly TextBox _addInput;
    private readonly Button _addButton;
    private readonly StackPanel _modListPanel;
    private readonly Border _noAccountWarning;
    private readonly List<ModRow> _rows = new();

    private sealed class ModRow
    {
        public required ModEntry Entry;
        public required CheckBox Enabled;
        public required TextBox Name;
        public required TextBox Note;
        public required FrameworkElement Panel;
    }

    private ModsDialog(LauncherConfig config, ModService modService, Func<string, Task<bool>> connectSteam)
    {
        _config = config;
        _modService = modService;
        _connectSteam = connectSteam;

        Title = "Mods";
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 720;
        Height = 660;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(new TextBlock
        {
            Text = "Manage this server's mods. Add Steam Workshop ids for the launcher to download, or drop mod folders "
                 + "into the mods folder yourself and Scan for them. Either way, enabled mods load on the next server "
                 + "start or restart.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        _modsEnabled = Check("Enable mods on this server", config.ModsEnabled);
        _modsEnabled.Checked += (_, _) => RefreshWarning();
        _modsEnabled.Unchecked += (_, _) => RefreshWarning();
        stack.Children.Add(_modsEnabled);

        // --- Steam account ---
        stack.Children.Add(Header("Steam account (optional, only needed to download Workshop mods)"));
        _steamStatus = new TextBlock { Foreground = Fg, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_steamStatus);
        stack.Children.Add(Note(
            "Steam's own SteamCMD handles the sign-in in its own window. The launcher never sees or stores your "
            + "password, it only remembers your username. Dropped-in mods need no account."));

        _username = Field(config.SteamUsername);
        _username.TextChanged += (_, _) => { UpdateSteamStatus(); RefreshWarning(); };
        _connectButton = MakeButton("Connect Steam account", () => _ = OnConnectSteam());
        var connectRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_username, 0);
        _connectButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_connectButton, 1);
        connectRow.Children.Add(_username);
        connectRow.Children.Add(_connectButton);
        stack.Children.Add(connectRow);

        // --- Mods ---
        stack.Children.Add(Header("Mods"));
        _addInput = Field("");
        _addButton = MakeButton("Add", () => _ = OnAddMod());
        var addRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_addInput, 0);
        _addButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_addButton, 1);
        var scanButton = MakeButton("Scan folder", OnScanFolder);
        var openButton = MakeButton("Open mods folder", () => _modService.OpenModsFolder());
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal };
        scanButton.Margin = new Thickness(8, 0, 0, 0);
        openButton.Margin = new Thickness(8, 0, 0, 0);
        folderButtons.Children.Add(scanButton);
        folderButtons.Children.Add(openButton);
        Grid.SetColumn(folderButtons, 2);
        addRow.Children.Add(_addInput);
        addRow.Children.Add(_addButton);
        addRow.Children.Add(folderButtons);
        stack.Children.Add(new TextBlock { Text = "Add by Steam Workshop id or URL:", Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(addRow);

        _noAccountWarning = Warning(
            "You have Workshop mods enabled but no Steam account connected. Connect one above to download them, "
            + "or they'll be skipped on start (dropped-in mods still load).");
        stack.Children.Add(_noAccountWarning);

        stack.Children.Add(ListHeader());
        _modListPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        stack.Children.Add(_modListPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(MakeButton("Save", OnSave));
        buttons.Children.Add(MakeButton("Cancel", Close));
        stack.Children.Add(buttons);

        foreach (var entry in config.Mods)
            _rows.Add(BuildRow(entry.Clone()));
        RebuildModList();
        UpdateSteamStatus();
        RefreshWarning();

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static bool Show(Window? owner, LauncherConfig config, ModService modService, Func<string, Task<bool>> connectSteam)
    {
        var dialog = new ModsDialog(config, modService, connectSteam) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private async Task OnConnectSteam()
    {
        var username = _username.Text.Trim();
        if (username.Length == 0)
        {
            ChoiceDialog.Show(this, "Enter a username", "Enter your Steam account name first, then connect.", "OK");
            return;
        }
        _connectButton.IsEnabled = false;
        _connectButton.Content = "Connecting...";
        _steamStatus.Text = "A SteamCMD window opened. Enter your password and Steam Guard code there, it stays open so you can read the result, then close it.";
        try
        {
            var ok = await _connectSteam(username);
            _steamStatus.Text = ok
                ? $"Connected as {username}. SteamCMD cached the session."
                : "Couldn't confirm the sign-in. Click Connect again and watch the SteamCMD window for the error (wrong password or Steam Guard code).";
        }
        catch (Exception ex)
        {
            _steamStatus.Text = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            _connectButton.IsEnabled = true;
            _connectButton.Content = "Connect Steam account";
            RefreshWarning();
        }
    }

    private async Task OnAddMod()
    {
        var id = WorkshopId.TryParse(_addInput.Text);
        if (id is null)
        {
            ChoiceDialog.Show(this, "Invalid Workshop id",
                "Enter a numeric Steam Workshop id, or a workshop URL like https://steamcommunity.com/sharedfiles/filedetails/?id=123456.", "OK");
            return;
        }
        if (_rows.Any(r => r.Entry.WorkshopId == id))
        {
            ChoiceDialog.Show(this, "Already added", "That Workshop id is already in the list.", "OK");
            return;
        }

        _addButton.IsEnabled = false;
        _addButton.Content = "Adding...";
        try
        {
            var name = "";
            long timeUpdated = 0;
            using (var client = new SteamWorkshopClient())
            {
                var details = await client.GetDetailsAsync(id, default);
                if (details is not null)
                {
                    if (details.ConsumerAppId != SteamWorkshopClient.PalworldAppId)
                    {
                        var choice = ChoiceDialog.Show(this, "Not a Palworld mod?",
                            $"This item's Steam app id is {details.ConsumerAppId}, not Palworld's ({SteamWorkshopClient.PalworldAppId}), "
                            + "so it may not work on your server. Add it anyway?", "Add anyway", "Cancel");
                        if (choice != 0)
                            return;
                    }
                    name = details.Title;
                    timeUpdated = details.TimeUpdated;
                }
            }
            _rows.Add(BuildRow(new ModEntry { WorkshopId = id, ModName = name, Enabled = true, TimeUpdated = timeUpdated }));
            _addInput.Text = "";
            RebuildModList();
            RefreshWarning();
        }
        finally
        {
            _addButton.IsEnabled = true;
            _addButton.Content = "Add";
        }
    }

    private void OnScanFolder()
    {
        var added = 0;
        var skipped = 0;
        foreach (var mod in _modService.ScanInstalledMods())
        {
            var alreadyTracked = _rows.Any(r =>
                (r.Entry.WorkshopId.Length > 0 && r.Entry.WorkshopId == mod.FolderId)
                || (mod.PackageName.Length > 0 && r.Entry.PackageName == mod.PackageName));
            if (alreadyTracked)
                continue;
            if (!mod.HasInfo || mod.PackageName.Length == 0)
            {
                skipped++;
                continue;
            }
            // Found on disk, so track it as a local mod (empty WorkshopId, the launcher won't try to download it).
            _rows.Add(BuildRow(new ModEntry { WorkshopId = "", ModName = mod.FolderId, PackageName = mod.PackageName, Enabled = true }));
            added++;
        }
        RebuildModList();
        RefreshWarning();

        var message = added == 0 ? "No new mods found in the mods folder." : $"Added {added} mod(s) from the mods folder.";
        if (skipped > 0)
            message += $" Skipped {skipped} folder(s) with no readable Info.json.";
        ChoiceDialog.Show(this, "Scan mods folder", message, "OK");
    }

    private void OnSave()
    {
        foreach (var row in _rows)
        {
            row.Entry.Enabled = row.Enabled.IsChecked == true;
            row.Entry.ModName = row.Name.Text.Trim();
            row.Entry.Note = row.Note.Text.Trim();
        }

        var modsOn = _modsEnabled.IsChecked == true;
        var username = _username.Text.Trim();

        // Make going without a Workshop connection a deliberate choice, not a silent skip at launch.
        var needsAccount = modsOn && username.Length == 0
            && _rows.Any(r => r.Entry.Enabled && r.Entry.WorkshopId.Length > 0);
        if (needsAccount)
        {
            var choice = ChoiceDialog.Show(this, "No Steam account connected",
                "You have Workshop mods enabled but no Steam account connected, so they can't be downloaded and will "
                + "be skipped when the server starts (dropped-in mods still load). Save anyway?", "Save anyway", "Cancel");
            if (choice != 0)
                return;
        }

        _config.ModsEnabled = modsOn;
        _config.SteamUsername = username;
        _config.Mods = _rows.Select(r => r.Entry).ToList();
        _config.Save();
        _saved = true;
        Close();
    }

    private ModRow BuildRow(ModEntry entry)
    {
        var enabled = new CheckBox { IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center, Foreground = Fg, Margin = new Thickness(0, 0, 6, 0) };
        enabled.Checked += (_, _) => RefreshWarning();
        enabled.Unchecked += (_, _) => RefreshWarning();

        var name = RowField(entry.ModName);
        var idLabel = new TextBlock
        {
            Text = entry.WorkshopId.Length > 0 ? entry.WorkshopId : "local",
            Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            ToolTip = entry.PackageName.Length > 0 ? $"Package: {entry.PackageName}" : "Package name not resolved yet (download or scan the mod).",
        };
        var note = RowField(entry.Note);
        var remove = new Button
        {
            Content = "✕", Width = 30, Height = 26, Padding = new Thickness(0), Foreground = Fg,
            Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Remove from the list (doesn't delete the mod files).",
        };
        remove.Click += (_, _) => { _rows.RemoveAll(r => ReferenceEquals(r.Entry, entry)); RebuildModList(); RefreshWarning(); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // checkbox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });                      // id
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) }); // note
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // remove
        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(idLabel, 2);
        Grid.SetColumn(note, 3);
        Grid.SetColumn(remove, 4);
        grid.Children.Add(enabled);
        grid.Children.Add(name);
        grid.Children.Add(idLabel);
        grid.Children.Add(note);
        grid.Children.Add(remove);

        var border = new Border
        {
            Child = grid, BorderBrush = RowBorder, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4, 0, 4),
        };
        return new ModRow { Entry = entry, Enabled = enabled, Name = name, Note = note, Panel = border };
    }

    private void RebuildModList()
    {
        _modListPanel.Children.Clear();
        if (_rows.Count == 0)
        {
            _modListPanel.Children.Add(new TextBlock
            {
                Text = "No mods yet. Add a Workshop id above, or drop mod folders into the mods folder and Scan.",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }
        foreach (var row in _rows)
            _modListPanel.Children.Add(row.Panel);
    }

    private void RefreshWarning()
    {
        var needsAccount = _modsEnabled.IsChecked == true
            && string.IsNullOrWhiteSpace(_username.Text)
            && _rows.Any(r => r.Enabled.IsChecked == true && r.Entry.WorkshopId.Length > 0);
        _noAccountWarning.Visibility = needsAccount ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSteamStatus() => _steamStatus.Text = string.IsNullOrWhiteSpace(_username.Text)
        ? "Not connected. Add Workshop mods below, then connect an account to download them."
        : $"Steam account: {_username.Text.Trim()}. SteamCMD keeps the sign-in cached between runs.";

    // --- small dark-theme builders (mirrors DiscordDialog) ---
    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6),
    };

    private static TextBox Field(string value) => new()
    {
        Text = value, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
        Padding = new Thickness(5, 4, 5, 4), CaretBrush = Brushes.White,
    };

    private static TextBox RowField(string value) => new()
    {
        Text = value, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
        Padding = new Thickness(4, 3, 4, 3), CaretBrush = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = text, IsChecked = value, Foreground = Fg, Margin = new Thickness(0, 6, 0, 0),
    };

    private static Border Note(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
        Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 6, 0, 0),
        Child = new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap },
    };

    private static Border Warning(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x1E)),
        Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x80)), TextWrapping = TextWrapping.Wrap },
    };

    private static Grid ListHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddHeaderCell(grid, 0, "On");
        AddHeaderCell(grid, 1, "Name");
        AddHeaderCell(grid, 2, "ID");
        AddHeaderCell(grid, 3, "Note");
        return grid;
    }

    private static void AddHeaderCell(Grid grid, int column, string text)
    {
        var cell = new TextBlock { Text = text, Foreground = Muted, FontWeight = FontWeights.SemiBold, Margin = new Thickness(column == 0 ? 0 : 6, 0, 6, 0) };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static Button MakeButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7),
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, MinWidth = 90,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
