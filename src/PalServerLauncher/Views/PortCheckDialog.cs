using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Tests whether the server's ports are reachable from the internet, using check-host.cc as an external
/// vantage point. Because Palworld's game port never answers an arbitrary probe, the check binds our own
/// temporary listener on each port (see <see cref="PortChecker"/>), so it is stopped-only. The first row
/// confirms the check-host service itself is up (else every port row is Unknown, never a false "closed").
/// This is the launcher's first async code-built dialog: the Check handler is <c>async void</c>, per-row
/// updates run on the UI thread, and a <see cref="CancellationTokenSource"/> tied to <c>Closed</c> aborts
/// an in-flight check if the user closes the window.
/// </summary>
public sealed class PortCheckDialog : Window
{
    private static readonly Brush Green = Theme.Success;
    private static readonly Brush Amber = Theme.Warning;
    private static readonly Brush Red = Theme.Error;
    private static readonly Brush Grey = Theme.TextCaption;
    private static readonly Brush Blue = Theme.Accent;

    private readonly Logger _logger;
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBox _ipBox;
    private readonly TextBlock _serviceLight;
    private readonly TextBlock _serviceStatus;
    private readonly Button _checkButton;
    private readonly List<PortRow> _rows = new();

    private sealed class PortRow
    {
        public required PortKind Kind { get; init; }
        public required PortProtocol Protocol { get; init; }
        public required TextBox PortBox { get; init; }
        public required TextBlock Light { get; init; }
        public required TextBlock Status { get; init; }
    }

    private PortCheckDialog(LauncherConfig config, PalworldServerSettings settings, string? detectedIp, Logger logger)
    {
        _logger = logger;

        Title = Strings.PortCheck_Title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        Closed += (_, _) => _cts.Cancel();

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(new TextBlock
        {
            Text = Strings.PortCheck_Intro,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        _ipBox = Field(detectedIp ?? "");
        stack.Children.Add(Row(Strings.PortCheck_YourPublicIp, _ipBox));

        stack.Children.Add(Header(Strings.PortCheck_Results));

        _serviceLight = Light();
        _serviceStatus = Status(Strings.PortCheck_NotCheckedYet);
        stack.Children.Add(RowGrid(Strings.PortCheck_ServiceOnline, portBox: null, _serviceLight, _serviceStatus));

        foreach (var item in PortCheckPlan.Build(config, settings))
        {
            var portBox = Field(item.Port.ToString(CultureInfo.InvariantCulture));
            portBox.Width = 66;
            portBox.HorizontalAlignment = HorizontalAlignment.Left;
            DigitsOnly(portBox);
            var light = Light();
            var status = Status(Strings.PortCheck_NotCheckedYet);
            _rows.Add(new PortRow { Kind = item.Kind, Protocol = item.Protocol, PortBox = portBox, Light = light, Status = status });
            stack.Children.Add(RowGrid(string.Format(Strings.PortCheck_PortRowLabel, item.Label, item.Protocol.ToString().ToUpperInvariant()), portBox, light, status));
        }

        stack.Children.Add(Note(Strings.PortCheck_FirewallNote));

        _checkButton = MakeButton(Strings.PortCheck_Check, OnCheck);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_checkButton);
        buttons.Children.Add(MakeButton(Strings.PortCheck_Close, Close));
        stack.Children.Add(buttons);

        Content = stack;
    }

    public static void Show(Window? owner, LauncherConfig config, PalworldServerSettings settings, string? detectedIp, Logger logger)
    {
        var dialog = new PortCheckDialog(config, settings, detectedIp, logger) { Owner = owner };
        dialog.ShowDialog();
    }

    private async void OnCheck()
    {
        var target = _ipBox.Text.Trim();
        if (target.Length == 0)
        {
            ChoiceDialog.Show(this, Strings.PortCheck_NoPublicIpTitle,
                Strings.PortCheck_NoPublicIpMessage, Strings.Common_OK);
            return;
        }

        _checkButton.IsEnabled = false;
        SetLight(_serviceLight, _serviceStatus, Blue, Strings.PortCheck_Checking, muted: true);
        foreach (var row in _rows)
            SetLight(row.Light, row.Status, Blue, Strings.PortCheck_Checking, muted: true);

        try
        {
            using var checkHost = new CheckHostClient();

            var serviceUp = await checkHost.IsServiceUpAsync(_cts.Token);
            SetLight(_serviceLight, _serviceStatus, serviceUp ? Green : Red,
                serviceUp ? Strings.PortCheck_ServiceOnlineStatus : Strings.PortCheck_ServiceUnavailable, muted: !serviceUp);

            if (!serviceUp)
            {
                foreach (var row in _rows)
                {
                    var verdict = PortCheckVerdict.Evaluate(row.Kind, PortReachability.Inconclusive, serviceUp: false);
                    SetVerdict(row, verdict);
                }
                return;
            }

            var checker = new PortChecker(checkHost, _logger);
            foreach (var row in _rows)
            {
                if (!TryReadPort(row.PortBox, out var port))
                {
                    SetLight(row.Light, row.Status, Grey, Strings.PortCheck_InvalidPort, muted: true);
                    continue;
                }

                SetLight(row.Light, row.Status, Blue, Strings.PortCheck_Checking, muted: true);
                var item = new PortCheckItem(row.Kind, "", row.Protocol, port);
                var reachability = await checker.CheckPortAsync(target, item, _cts.Token);
                SetVerdict(row, PortCheckVerdict.Evaluate(row.Kind, reachability, serviceUp: true));
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed mid-check; nothing to do.
        }
        catch (Exception ex)
        {
            _logger.Error("Port check failed", ex);
            SetLight(_serviceLight, _serviceStatus, Red, Strings.PortCheck_Failed, muted: true);
        }
        finally
        {
            if (!_cts.IsCancellationRequested)
                _checkButton.IsEnabled = true;
        }
    }

    private void SetVerdict(PortRow row, PortVerdict verdict) =>
        SetLight(row.Light, row.Status, LevelBrush(verdict.Level), verdict.Message, muted: verdict.Level == VerdictLevel.Unknown);

    private static void SetLight(TextBlock light, TextBlock status, Brush colour, string message, bool muted)
    {
        light.Foreground = colour;
        status.Text = message;
        status.Foreground = muted ? Muted : Fg;
    }

    private static Brush LevelBrush(VerdictLevel level) => level switch
    {
        VerdictLevel.Ok => Green,
        VerdictLevel.Warn => Amber,
        VerdictLevel.Fail => Red,
        _ => Grey,
    };

    private static bool TryReadPort(TextBox box, out int port) =>
        int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    // --- small dark-theme builders (mirrors DiscordDialog) ---
    private static TextBlock Light() => new()
    {
        Text = "●", FontSize = 14, Foreground = Grey,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Status(string text) => new()
    {
        Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Grid Row(string label, FrameworkElement input)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);
        return grid;
    }

    private static Grid RowGrid(string label, FrameworkElement? portBox, TextBlock light, TextBlock status)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (portBox is not null)
        {
            Grid.SetColumn(portBox, 1);
            grid.Children.Add(portBox);
        }

        Grid.SetColumn(light, 2);
        grid.Children.Add(light);
        Grid.SetColumn(status, 3);
        grid.Children.Add(status);
        return grid;
    }

    private static Border Note(string text) => new()
    {
        Background = Theme.BannerBg,
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 14, 0, 0),
        Child = new TextBlock { Text = text, Foreground = Theme.BannerFg, TextWrapping = TextWrapping.Wrap },
    };
}
