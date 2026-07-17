using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Sets where backup archives are written. Shows the current folder (clickable to open it in Explorer), a
/// Change button (folder picker), and a reset-to-default (↺) that appears only for a custom path. Save
/// write-tests the folder and reports the result, noting where any existing backups were left; Cancel discards.
/// An empty custom path means the default <c>&lt;ServerRoot&gt;\backups</c>. Built in code to match the theme.
/// </summary>
public sealed class BackupLocationDialog : Window
{
    private readonly string _defaultResolved;
    private readonly string _currentResolved; // before any change, for the "existing backups stay here" note
    private readonly Action<string> _applyFolder;
    private readonly Logger _logger;

    private string _pending; // "" = default, else a custom absolute path
    private readonly TextBox _pathBox;
    private readonly Button _resetButton;

    private BackupLocationDialog(string initialRaw, string defaultResolved, Action<string> applyFolder, Logger logger)
    {
        _defaultResolved = defaultResolved;
        _applyFolder = applyFolder;
        _logger = logger;
        _pending = initialRaw ?? "";
        _currentResolved = Resolve(_pending);

        Title = Strings.BackupLoc_Title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 540;

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = Strings.BackupLoc_Intro, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // A read-only field, so it reads as a normal path box (selectable/copyable) but can't be edited by hand.
        // Double-click opens the folder in Explorer.
        _pathBox = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _pathBox.PreviewMouseDoubleClick += (_, e) => { e.Handled = true; OpenFolder(Resolve(_pending)); };
        _pathBox.Loaded += (_, _) => ShowPathTail(); // once laid out, scroll to the end so the leaf folder shows
        Grid.SetColumn(_pathBox, 0);

        var change = MakeButton(Strings.BackupLoc_Change, OnChange);
        Grid.SetColumn(change, 1);

        _resetButton = new Button
        {
            Content = "↺", Width = 26, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0, 1, 0, 1),
            Foreground = Fg, Background = Theme.Control, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = Strings.Settings_ResetFieldTooltip,
        };
        _resetButton.Click += (_, _) => { _pending = ""; RefreshDisplay(); };
        Grid.SetColumn(_resetButton, 2);

        row.Children.Add(_pathBox);
        row.Children.Add(change);
        row.Children.Add(_resetButton);
        stack.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));
        stack.Children.Add(buttons);

        Content = stack;
        RefreshDisplay();
    }

    public static void Show(Window? owner, string initialRaw, string defaultResolved, Action<string> applyFolder, Logger logger)
    {
        var dialog = new BackupLocationDialog(initialRaw, defaultResolved, applyFolder, logger) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>A blank pending value means the default folder, otherwise the custom path is used as-is.</summary>
    private string Resolve(string raw) => string.IsNullOrWhiteSpace(raw) ? _defaultResolved : raw;

    private void RefreshDisplay()
    {
        var resolved = Resolve(_pending);
        _pathBox.Text = resolved;
        _pathBox.ToolTip = resolved;
        ShowPathTail();
        _resetButton.Visibility = string.IsNullOrWhiteSpace(_pending) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Scroll the read-only field to its end so the leaf folder is visible rather than the drive root.</summary>
    private void ShowPathTail()
    {
        _pathBox.CaretIndex = _pathBox.Text.Length;
        _pathBox.ScrollToHorizontalOffset(double.MaxValue);
    }

    private void OnChange()
    {
        var picker = new OpenFolderDialog { Title = Strings.BackupLoc_PickTitle };
        var current = Resolve(_pending);
        if (Directory.Exists(current))
            picker.InitialDirectory = current;
        if (picker.ShowDialog(this) != true)
            return;
        // Picking the default folder itself is just "default", not a custom path pinned to it.
        _pending = PathsEqual(picker.FolderName, _defaultResolved) ? "" : picker.FolderName;
        RefreshDisplay();
    }

    private void OnSave()
    {
        var target = Resolve(_pending);
        if (!BackupService.TryEnsureWritable(target, out var error))
        {
            ChoiceDialog.Show(this, Strings.BackupLoc_FailedTitle, Strings.BackupLoc_FailedMessage + "\n" + target + "\n\n" + error, Strings.Common_OK);
            return; // stay open so they can pick another folder
        }

        _applyFolder(_pending);
        _logger.Info($"Backup location set to {target}.");

        var message = Strings.BackupLoc_ChangedMessage + "\n" + target;
        if (!PathsEqual(target, _currentResolved) && HasBackups(_currentResolved))
            message += "\n\n" + string.Format(Strings.BackupLoc_ExistingNote, _currentResolved);
        ChoiceDialog.Show(this, Strings.BackupLoc_ChangedTitle, message, Strings.Common_OK);
        Close(); // success closes this dialog too, returning to the main window
    }

    private static bool HasBackups(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "palworld-*.zip").Any(); }
        catch { return false; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private void OpenFolder(string path)
    {
        var target = Directory.Exists(path) ? path : Directory.GetParent(path)?.FullName ?? path;
        OpenUrl(target);
    }
}
