using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Rest;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Manage Palworld's built-in Steam Workshop server mods: a master toggle, an optional Steam sign-in for
/// downloading Workshop items, and a list of tracked mods (Workshop ids the launcher downloads plus dropped-in
/// mods the user placed in Mods\Workshop themselves). Mutates the config in place and returns true if the user
/// saved; mods deploy on the next server start/restart. Built in code to match the other dark dialogs.
/// </summary>
public sealed class ModsDialog : Window
{
    private static readonly Brush RowBorder = Theme.Divider;
    private static readonly Brush GreenFg = Theme.Success;
    private static readonly Brush AmberFg = Theme.Warning;
    private static readonly Brush RedFg = Theme.Error;
    private static readonly Brush InsetBg = Theme.Inset;

    private readonly LauncherConfig _config;
    private readonly ModService _modService;
    private readonly Func<string, string, Task<bool>> _connectSteam;
    private readonly Func<string, Task<bool>> _checkLogin;
    private readonly Action<string> _restoreOriginalInfo;
    private bool _saved;

    private readonly CheckBox _modsEnabled;
    private readonly TextBox _username;
    private readonly TextBlock _steamStatus;
    private readonly Button _connectButton;
    private readonly TextBlock _differentAccountLink;
    private readonly StackPanel _connectPanel;
    private readonly TextBox _addInput;
    private readonly Button _addButton;
    private readonly StackPanel _modListPanel;
    private readonly Border _noAccountWarning;
    private readonly StackPanel _loosePakPanel;
    private readonly List<ModRow> _rows = new();

    private sealed class ModRow
    {
        public required ModEntry Entry;
        public required CheckBox Enabled;
        public required TextBox Name;
        public required TextBox Note;
        public required CheckBox Force;
        public required FrameworkElement Panel;
    }

    private ModsDialog(LauncherConfig config, ModService modService, Func<string, string, Task<bool>> connectSteam, Func<string, Task<bool>> checkLogin, Action<string> restoreOriginalInfo)
    {
        _config = config;
        _modService = modService;
        _connectSteam = connectSteam;
        _checkLogin = checkLogin;
        _restoreOriginalInfo = restoreOriginalInfo;

        Title = Strings.Mods_Title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 720;
        Height = 706;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(new TextBlock
        {
            Text = Strings.Mods_Disclaimer,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        _modsEnabled = Check(Strings.Mods_EnableMods, config.ModsEnabled);
        _modsEnabled.Checked += (_, _) => RefreshWarning();
        _modsEnabled.Unchecked += (_, _) => RefreshWarning();
        stack.Children.Add(_modsEnabled);

        // --- Steam account (stateful: checking / signed-in / not-signed-in) ---
        stack.Children.Add(Header(Strings.Mods_SteamAccountHeader));
        _steamStatus = new TextBlock { Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_steamStatus);

        // Build the connect panel first so the "different account" link can reference it (assigned field).
        _connectPanel = new StackPanel();
        _connectPanel.Children.Add(Note(Strings.Mods_SteamAccountNote));
        _username = Field(config.SteamUsername);
        _username.TextChanged += (_, _) => RefreshWarning();
        _connectButton = MakeButton(Strings.Mods_ConnectButton, () => _ = OnConnectSteam());
        var connectRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_username, 0);
        _connectButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_connectButton, 1);
        connectRow.Children.Add(_username);
        connectRow.Children.Add(_connectButton);
        _connectPanel.Children.Add(connectRow);

        _differentAccountLink = LinkButton(Strings.Mods_DifferentAccountLink, () => _connectPanel.Visibility = Visibility.Visible);
        stack.Children.Add(_differentAccountLink);
        stack.Children.Add(_connectPanel);

        // --- Steam Workshop mods ---
        stack.Children.Add(Header(Strings.Mods_WorkshopHeader));
        stack.Children.Add(Link(Strings.Mods_BrowsePrefix, "https://steamcommunity.com/app/1623730/workshop/", Strings.Mods_BrowseLinkText, Strings.Mods_BrowseSuffix));
        _addInput = Field("");
        _addButton = MakeButton(Strings.Mods_AddButton, () => _ = OnAddMod());
        var addRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_addInput, 0);
        _addButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_addButton, 1);
        var scanButton = MakeButton(Strings.Mods_ScanFolderButton, OnScanFolder);
        var openButton = MakeButton(Strings.Mods_OpenModsFolderButton, () => _modService.OpenModsFolder());
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal };
        scanButton.Margin = new Thickness(8, 0, 0, 0);
        openButton.Margin = new Thickness(8, 0, 0, 0);
        folderButtons.Children.Add(scanButton);
        folderButtons.Children.Add(openButton);
        Grid.SetColumn(folderButtons, 2);
        addRow.Children.Add(_addInput);
        addRow.Children.Add(_addButton);
        addRow.Children.Add(folderButtons);
        stack.Children.Add(new TextBlock { Text = Strings.Mods_AddByIdLabel, Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(addRow);

        _noAccountWarning = Warning(Strings.Mods_NoAccountWarning);
        stack.Children.Add(_noAccountWarning);

        stack.Children.Add(ListHeader());
        _modListPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        stack.Children.Add(_modListPanel);

        // --- Loose .pak mods (a separate mechanism: raw paks in ~mods, toggled by rename) ---
        stack.Children.Add(Separator());
        stack.Children.Add(Header(Strings.Mods_LoosePakHeader));
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Mods_LoosePakDescription,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
        });
        _loosePakPanel = new StackPanel();
        stack.Children.Add(new Border
        {
            Child = _loosePakPanel, Background = InsetBg, BorderBrush = FieldBorder, BorderThickness = new Thickness(1),
            CornerRadius = Metrics.Radius, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 0),
        });
        var looseButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openLoose = MakeButton(Strings.Mods_OpenLoosePaksFolderButton, () => _modService.OpenLoosePaksFolder());
        openLoose.Margin = new Thickness(0);
        looseButtons.Children.Add(openLoose);
        looseButtons.Children.Add(MakeButton(Strings.Mods_RescanButton, RebuildLoosePakList));
        looseButtons.Children.Add(MakeButton(Strings.Mods_OpenUe4ssFolderButton, OnOpenUe4ss));
        stack.Children.Add(looseButtons);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));
        stack.Children.Add(buttons);

        foreach (var entry in config.Mods)
            _rows.Add(BuildRow(entry.Clone()));
        RebuildModList();
        RebuildLoosePakList();
        ShowSteamState(string.IsNullOrWhiteSpace(config.SteamUsername) ? SteamUi.NotSignedIn : SteamUi.Checking);
        RefreshWarning();
        Loaded += async (_, _) => await CheckLoginOnOpenAsync();

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static bool Show(Window? owner, LauncherConfig config, ModService modService, Func<string, string, Task<bool>> connectSteam, Func<string, Task<bool>> checkLogin, Action<string> restoreOriginalInfo)
    {
        var dialog = new ModsDialog(config, modService, connectSteam, checkLogin, restoreOriginalInfo) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private async Task OnConnectSteam()
    {
        var username = _username.Text.Trim();
        if (username.Length == 0)
        {
            ChoiceDialog.Show(this, Strings.Mods_EnterUsernameTitle, Strings.Mods_EnterUsernameBody, Strings.Common_OK);
            return;
        }

        var password = PasswordPromptDialog.Show(this, username);
        if (password is null)
            return; // user cancelled the password prompt, leave the connect panel as-is

        _connectButton.IsEnabled = false;
        _connectButton.Content = Strings.Mods_ConnectingButton;
        _steamStatus.Visibility = Visibility.Visible;
        _steamStatus.Foreground = Muted;
        _steamStatus.Text = Strings.Mods_SteamCmdWindowOpened;
        try
        {
            var ok = await _connectSteam(username, password);
            ShowSteamState(ok ? SteamUi.SignedIn : SteamUi.NotSignedIn,
                ok ? null : Strings.Mods_ConfirmSignInFailed);
        }
        catch (Exception ex)
        {
            ShowSteamState(SteamUi.NotSignedIn, string.Format(Strings.Mods_SignInFailed, ex.Message), error: true);
        }
        finally
        {
            _connectButton.IsEnabled = true;
            _connectButton.Content = Strings.Mods_ConnectButton;
            RefreshWarning();
        }
    }

    /// <summary>On open, if a username is set, verify the cached session in the background and show a real
    /// signed-in / not-signed-in status, so the user isn't left guessing whether a past sign-in still holds.</summary>
    private async Task CheckLoginOnOpenAsync()
    {
        var username = _username.Text.Trim();
        if (username.Length == 0)
            return; // no account -> already showing the connect panel
        ShowSteamState(SteamUi.Checking);
        try
        {
            var signedIn = await _checkLogin(username);
            ShowSteamState(signedIn ? SteamUi.SignedIn : SteamUi.NotSignedIn,
                signedIn ? null : Strings.Mods_SavedSignInInvalid);
        }
        catch (Exception ex)
        {
            ShowSteamState(SteamUi.NotSignedIn, string.Format(Strings.Mods_CheckSignInFailed, ex.Message), error: true);
        }
    }

    private enum SteamUi { Checking, SignedIn, NotSignedIn }

    /// <summary>Drive the Steam account section. Checking: just a status line. Signed in: a green line plus a
    /// "different account" link, connect UI hidden. Not signed in: the connect panel, with an optional message.</summary>
    private void ShowSteamState(SteamUi state, string? message = null, bool error = false)
    {
        switch (state)
        {
            case SteamUi.Checking:
                _steamStatus.Visibility = Visibility.Visible;
                _steamStatus.Foreground = Muted;
                _steamStatus.Text = Strings.Mods_CheckingSignIn;
                _differentAccountLink.Visibility = Visibility.Collapsed;
                _connectPanel.Visibility = Visibility.Collapsed;
                break;
            case SteamUi.SignedIn:
                _steamStatus.Visibility = Visibility.Visible;
                _steamStatus.Foreground = GreenFg;
                _steamStatus.Text = Strings.Mods_SignedIn;
                _differentAccountLink.Visibility = Visibility.Visible;
                _connectPanel.Visibility = Visibility.Collapsed;
                break;
            default: // NotSignedIn
                // Amber for the ambiguous "couldn't confirm / not signed in", red for a definite error.
                _steamStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
                _steamStatus.Foreground = error ? RedFg : AmberFg;
                _steamStatus.Text = message ?? "";
                _differentAccountLink.Visibility = Visibility.Collapsed;
                _connectPanel.Visibility = Visibility.Visible;
                break;
        }
    }

    private async Task OnAddMod()
    {
        var id = WorkshopId.TryParse(_addInput.Text);
        if (id is null)
        {
            ChoiceDialog.Show(this, Strings.Mods_InvalidIdTitle,
                Strings.Mods_InvalidIdBody, Strings.Common_OK);
            return;
        }
        if (_rows.Any(r => r.Entry.WorkshopId == id))
        {
            ChoiceDialog.Show(this, Strings.Mods_AlreadyAddedTitle, Strings.Mods_AlreadyAddedBody, Strings.Common_OK);
            return;
        }

        _addButton.IsEnabled = false;
        _addButton.Content = Strings.Mods_AddingButton;
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
                        var choice = ChoiceDialog.Show(this, Strings.Mods_NotPalworldTitle,
                            string.Format(Strings.Mods_NotPalworldBody, details.ConsumerAppId, SteamWorkshopClient.PalworldAppId),
                            Strings.Mods_AddAnyway, Strings.Common_Cancel);
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
            _addButton.Content = Strings.Mods_AddButton;
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
            _rows.Add(BuildRow(new ModEntry { WorkshopId = "", FolderName = mod.FolderId, ModName = mod.FolderId, PackageName = mod.PackageName, Enabled = true }));
            added++;
        }
        RebuildModList();
        RefreshWarning();

        var message = added == 0 ? Strings.Mods_ScanNoneFound : string.Format(Strings.Mods_ScanAdded, added);
        if (skipped > 0)
            message += string.Format(Strings.Mods_ScanSkipped, skipped);
        ChoiceDialog.Show(this, Strings.Mods_ScanTitle, message, Strings.Common_OK);
    }

    private void OnSave()
    {
        foreach (var row in _rows)
        {
            // On an un-force (was forced, now not), restore the author's original Info.json so the leftover
            // injected IsServer doesn't linger on disk. row.Entry still holds the pre-save Force state here.
            var nowForced = row.Force.IsChecked == true;
            if (row.Entry.ForceServerInstall && !nowForced && row.Entry.WorkshopId.Length > 0)
                _restoreOriginalInfo(row.Entry.WorkshopId);

            row.Entry.Enabled = row.Enabled.IsChecked == true;
            row.Entry.ModName = row.Name.Text.Trim();
            row.Entry.Note = row.Note.Text.Trim();
            row.Entry.ForceServerInstall = nowForced;
        }

        var modsOn = _modsEnabled.IsChecked == true;
        var username = _username.Text.Trim();

        // Make going without a Workshop connection a deliberate choice, not a silent skip at launch.
        var needsAccount = modsOn && username.Length == 0
            && _rows.Any(r => r.Entry.Enabled && r.Entry.WorkshopId.Length > 0);
        if (needsAccount)
        {
            var choice = ChoiceDialog.Show(this, Strings.Mods_NoAccountTitle,
                Strings.Mods_NoAccountSaveBody, Strings.Mods_SaveAnyway, Strings.Common_Cancel);
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
        var pkgTip = entry.PackageName.Length > 0 ? string.Format(Strings.Mods_PackageTip, entry.PackageName) : Strings.Mods_PackageUnresolvedTip;
        FrameworkElement idCell;
        if (entry.WorkshopId.Length > 0)
        {
            var idLink = new Hyperlink(new Run(entry.WorkshopId))
            {
                NavigateUri = new Uri($"https://steamcommunity.com/sharedfiles/filedetails/?id={entry.WorkshopId}"),
                Foreground = LinkFg,
            };
            idLink.RequestNavigate += (_, e) => { OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
            idCell = new TextBlock(idLink)
            {
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0), ToolTip = Strings.Mods_OpenWorkshopPageTip + pkgTip,
            };
        }
        else
        {
            idCell = new TextBlock
            {
                Text = Strings.Mods_LocalLabel, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(6, 0, 6, 0), ToolTip = pkgTip,
            };
        }
        var note = RowField(entry.Note);

        // Force server install is only meaningful for downloaded Workshop mods (a clean cache copy to restore
        // from). For a dropped-in local mod, the user owns the files directly, so the box is disabled.
        var isLocal = entry.WorkshopId.Length == 0;
        var force = new CheckBox
        {
            IsChecked = entry.ForceServerInstall, IsEnabled = !isLocal, Foreground = Fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = isLocal ? Strings.Mods_ForceLocalTip : Strings.Mods_ForceHeaderTip,
        };
        // Click fires only on user interaction (not the programmatic IsChecked above), so init doesn't warn.
        // Forcing implies enabling (a forced mod must be in ActiveModList to deploy). Un-forcing disables the
        // mod, because a client-only mod can't run on a dedicated server, so the server drops its UE4SS entry
        // (modname : 0) on the next start and the leftover files sit inert until the mod is removed.
        force.Click += (_, _) =>
        {
            if (force.IsChecked == true)
            {
                if (ChoiceDialog.Show(this, Strings.Mods_ForceWarnTitle, Strings.Mods_ForceWarnBody,
                        Strings.Mods_ForceWarnAccept, Strings.Common_Cancel) != 0)
                {
                    force.IsChecked = false; // cancelled -> revert the check
                    return;
                }
                enabled.IsChecked = true;
            }
            else
            {
                enabled.IsChecked = false;
            }
        };

        var remove = CloseButton(() => DeleteRow(entry), Strings.Mods_DeleteModTip);
        remove.Margin = new Thickness(6, 0, 0, 0);

        var grid = new Grid();
        // All columns except name/note are fixed so the header Grid and the row Grids compute identical column
        // positions (an Auto column that differs between them, like the remove button, would misalign the rest).
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });                      // checkbox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });  // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });                       // id
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });   // note
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });                       // force
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });                       // remove
        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(idCell, 2);
        Grid.SetColumn(note, 3);
        Grid.SetColumn(force, 4);
        Grid.SetColumn(remove, 5);
        grid.Children.Add(enabled);
        grid.Children.Add(name);
        grid.Children.Add(idCell);
        grid.Children.Add(note);
        grid.Children.Add(force);
        grid.Children.Add(remove);

        var border = new Border
        {
            Child = grid, BorderBrush = RowBorder, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4, 0, 4),
        };
        return new ModRow { Entry = entry, Enabled = enabled, Name = name, Note = note, Force = force, Panel = border };
    }

    private void RebuildModList()
    {
        _modListPanel.Children.Clear();
        if (_rows.Count == 0)
        {
            _modListPanel.Children.Add(new TextBlock
            {
                Text = Strings.Mods_EmptyList,
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }
        foreach (var row in _rows)
            _modListPanel.Children.Add(row.Panel);
    }

    /// <summary>Confirm, then delete the mod's source folder and drop it from the list. The server clears its
    /// deployed copy on the next restart. The file delete is best-effort, a failure still removes the row.</summary>
    private void DeleteRow(ModEntry entry)
    {
        if (ChoiceDialog.Show(this, Strings.Mods_DeleteTitle,
            string.Format(Strings.Mods_DeleteBody, ModLabel(entry)), Strings.Mods_DeleteButton, Strings.Common_Cancel) != 0)
            return;
        try
        {
            _modService.DeleteModFolder(FolderOf(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChoiceDialog.Show(this, Strings.Mods_DeleteFailedTitle,
                string.Format(Strings.Mods_DeleteFailedBody, ex.Message), Strings.Common_OK);
        }
        _rows.RemoveAll(r => ReferenceEquals(r.Entry, entry));
        RebuildModList();
        RefreshWarning();
    }

    /// <summary>The folder under Mods\Workshop for this entry: the WorkshopId for a downloaded mod, else the
    /// scanned FolderName for a dropped-in one.</summary>
    private static string FolderOf(ModEntry entry) => entry.WorkshopId.Length > 0 ? entry.WorkshopId : entry.FolderName;

    private static string ModLabel(ModEntry entry) =>
        entry.ModName.Length > 0 ? entry.ModName
        : entry.WorkshopId.Length > 0 ? entry.WorkshopId
        : entry.FolderName.Length > 0 ? entry.FolderName
        : Strings.Mods_ThisModFallback;

    /// <summary>Rescan the loose-paks folder and rebuild its toggle list.</summary>
    private void RebuildLoosePakList()
    {
        _loosePakPanel.Children.Clear();
        var mods = _modService.ScanLoosePaks();
        if (mods.Count == 0)
        {
            _loosePakPanel.Children.Add(new TextBlock
            {
                Text = Strings.Mods_LoosePakEmpty,
                Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var mod in mods)
        {
            var count = mod.Files.Count;
            var check = new CheckBox
            {
                Content = string.Format(count == 1 ? Strings.Mods_LoosePakLabelSingular : Strings.Mods_LoosePakLabelPlural, mod.BaseName, count),
                IsChecked = mod.Enabled, Foreground = Fg, Margin = new Thickness(0, 4, 0, 0),
            };
            // Click fires only on user interaction, not the programmatic IsChecked above, so there's no toggle loop.
            check.Click += (_, _) => ToggleLoosePak(mod, check.IsChecked == true);
            _loosePakPanel.Children.Add(check);
        }
    }

    /// <summary>Enable/disable a loose pak by renaming its files, then rescan (the file names just changed).</summary>
    private void ToggleLoosePak(LoosePakMods.LoosePakMod mod, bool enable)
    {
        try
        {
            _modService.SetLoosePakEnabled(mod, enable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChoiceDialog.Show(this, Strings.Mods_ChangeModFailedTitle,
                string.Format(enable ? Strings.Mods_EnablePakFailed : Strings.Mods_DisablePakFailed, mod.BaseName, ex.Message), Strings.Common_OK);
        }
        Dispatcher.BeginInvoke(RebuildLoosePakList);
    }

    private void OnOpenUe4ss()
    {
        if (!_modService.Ue4ssInstalled)
        {
            ChoiceDialog.Show(this, Strings.Mods_Ue4ssNotInstalledTitle,
                Strings.Mods_Ue4ssNotInstalledBody, Strings.Common_OK);
            return;
        }
        _modService.OpenUe4ssModsFolder();
    }

    private static Border Separator() => new()
    {
        Height = 1, Background = Theme.Divider, Margin = new Thickness(0, 18, 0, 0),
    };

    private void RefreshWarning()
    {
        var needsAccount = _modsEnabled.IsChecked == true
            && string.IsNullOrWhiteSpace(_username.Text)
            && _rows.Any(r => r.Enabled.IsChecked == true && r.Entry.WorkshopId.Length > 0);
        _noAccountWarning.Visibility = needsAccount ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- small dark-theme builders (mirrors DiscordDialog) ---
    private static TextBox RowField(string value) => new()
    {
        Text = value, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
        Padding = new Thickness(4, 3, 4, 3), CaretBrush = Caret, VerticalAlignment = VerticalAlignment.Center,
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = text, IsChecked = value, Foreground = Fg, Margin = new Thickness(0, 6, 0, 0),
    };

    private static Border Note(string text) => new()
    {
        Background = Theme.Inset,
        Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 6, 0, 0),
        Child = new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap },
    };

    private static Border Warning(string text) => new()
    {
        Background = Theme.BannerBg,
        Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
        Child = new TextBlock { Text = text, Foreground = Theme.BannerFg, TextWrapping = TextWrapping.Wrap },
    };

    private static Grid ListHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        // Column widths MUST match BuildRow's grid exactly, so the headers line up over the row cells.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        AddHeaderCell(grid, 0, Strings.Mods_ColOn);
        AddHeaderCell(grid, 1, Strings.Mods_ColName);
        AddHeaderCell(grid, 2, Strings.Mods_ColId);
        AddHeaderCell(grid, 3, Strings.Mods_ColNote);
        var forceHeader = new TextBlock
        {
            Text = Strings.Mods_ColForce, Foreground = Muted, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = Strings.Mods_ForceHeaderTip,
        };
        Grid.SetColumn(forceHeader, 4);
        grid.Children.Add(forceHeader);
        return grid;
    }

    private static void AddHeaderCell(Grid grid, int column, string text)
    {
        var cell = new TextBlock { Text = text, Foreground = Muted, FontWeight = FontWeights.SemiBold, Margin = new Thickness(column == 0 ? 0 : 6, 0, 6, 0) };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static TextBlock Link(string prefix, string url, string linkText, string suffix)
    {
        var block = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        block.Inlines.Add(prefix);
        var link = new Hyperlink(new Run(linkText)) { NavigateUri = new Uri(url), Foreground = LinkFg };
        link.RequestNavigate += (_, e) => { OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
        block.Inlines.Add(link);
        block.Inlines.Add(suffix);
        return block;
    }

    /// <summary>A clickable text link that runs an action (not a URL), for in-dialog affordances.</summary>
    private static TextBlock LinkButton(string text, Action onClick)
    {
        var block = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        var link = new Hyperlink(new Run(text)) { Foreground = LinkFg };
        link.Click += (_, _) => onClick();
        block.Inlines.Add(link);
        return block;
    }
}
