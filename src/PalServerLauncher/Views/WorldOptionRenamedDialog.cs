using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PalServerLauncher.Localization;

namespace PalServerLauncher.Views;

/// <summary>
/// Confirms one or more WorldOption.sav files were renamed to .bak, each with a clickable link that reveals
/// the renamed file in File Explorer. Dark, code-built to match the app's other one-off dialogs.
/// </summary>
public sealed class WorldOptionRenamedDialog : Window
{
    private WorldOptionRenamedDialog(IReadOnlyList<string> bakPaths)
    {
        Title = Strings.WorldOpt_RenamedTitle;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 420;

        var root = new StackPanel { Margin = new Thickness(20) };

        foreach (var bak in bakPaths)
        {
            root.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.WorldOpt_RenamedFormat, Path.GetFileName(bak)),
                Foreground = Theme.Text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480,
            });

            // The folder as a clickable link that opens Explorer with the renamed file selected.
            var pathLine = new TextBlock { Margin = new Thickness(0, 2, 0, 14), MaxWidth = 480, TextWrapping = TextWrapping.Wrap };
            var link = new Hyperlink(new Run(Path.GetDirectoryName(bak) ?? "")) { Foreground = DarkControls.LinkFg };
            link.Click += (_, _) => RevealInExplorer(bak);
            pathLine.Inlines.Add(link);
            root.Children.Add(pathLine);
        }

        var ok = new Button { Content = Strings.Common_OK, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        root.Children.Add(ok);

        Content = root;
    }

    /// <summary>Open File Explorer with the renamed file selected.</summary>
    private static void RevealInExplorer(string filePath)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer missing or blocked, nothing useful to do here.
        }
    }

    public static void Show(Window? owner, IReadOnlyList<string> bakPaths)
    {
        var dialog = new WorldOptionRenamedDialog(bakPaths) { Owner = owner };
        dialog.ShowDialog();
    }
}
