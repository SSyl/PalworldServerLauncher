using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The CLI stop verb parse and its wire form. Both ends of the pipe share this type, so the round-trip cases
/// matter as much as the argv ones: a request that survives ToWire -> FromWire is what the running launcher acts on.
/// </summary>
public class StopRequestTests
{
    private static StopRequest? Parse(params string[] args) => StopRequest.FromCommandLine(args, out _);

    private static string? ErrorFor(params string[] args)
    {
        StopRequest.FromCommandLine(args, out var error);
        return error;
    }

    [Fact]
    public void No_stop_verb_parses_to_nothing()
    {
        Assert.Null(Parse());
        Assert.Null(Parse("--debug", "--console"));
        Assert.Null(ErrorFor("--debug"));
    }

    [Theory]
    [InlineData("--stop-server")]
    [InlineData("-stop-server")]
    [InlineData("--STOP-SERVER")]
    public void Bare_stop_flag_is_an_immediate_graceful_stop(string flag)
    {
        Assert.Equal(new StopRequest(StopKind.Graceful), Parse(flag));
    }

    [Theory]
    [InlineData("--kill-server")]
    [InlineData("-kill-server")]
    public void Kill_flag_parses_to_kill(string flag)
    {
        Assert.Equal(new StopRequest(StopKind.Kill), Parse(flag));
    }

    [Fact]
    public void A_following_number_is_the_countdown()
    {
        Assert.Equal(new StopRequest(StopKind.Countdown, 60), Parse("--stop-server", "60"));
    }

    [Fact]
    public void The_equals_form_also_carries_the_countdown()
    {
        Assert.Equal(new StopRequest(StopKind.Countdown, 60), Parse("--stop-server=60"));
    }

    [Fact]
    public void A_following_flag_is_not_eaten_as_a_countdown()
    {
        // --stop-server --debug must stay an immediate stop, not fail parsing.
        Assert.Equal(new StopRequest(StopKind.Graceful), Parse("--stop-server", "--debug"));
        Assert.Null(ErrorFor("--stop-server", "--debug"));
    }

    [Fact]
    public void Other_flags_around_the_verb_are_ignored()
    {
        Assert.Equal(new StopRequest(StopKind.Countdown, 30), Parse("--debug", "--stop-server", "30", "--console"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_zero_or_negative_countdown_is_just_an_immediate_stop(string seconds)
    {
        Assert.Equal(new StopRequest(StopKind.Graceful), Parse("--stop-server", seconds));
    }

    [Fact]
    public void The_countdown_bound_matches_the_gui_prompt()
    {
        Assert.Equal(new StopRequest(StopKind.Countdown, 3600), Parse("--stop-server", "3600"));
        Assert.Null(Parse("--stop-server", "3601"));
        Assert.Contains("3600", ErrorFor("--stop-server", "3601"));
    }

    [Fact]
    public void A_non_numeric_equals_value_is_an_error_not_a_silent_immediate_stop()
    {
        Assert.Null(Parse("--stop-server=soon"));
        Assert.Contains("soon", ErrorFor("--stop-server=soon"));
    }

    [Theory]
    [InlineData("--stop-server", "99999999999")]
    [InlineData("--stop-server=99999999999", null)]
    public void A_countdown_too_large_for_an_int_still_reports_the_range(string flag, string? seconds)
    {
        // It must not fall through "that isn't a number" into a silent immediate stop.
        var args = seconds is null ? new[] { flag } : [flag, seconds];
        Assert.Null(StopRequest.FromCommandLine(args, out var error));
        Assert.Contains("3600", error);
    }

    [Fact]
    public void Kill_wins_when_it_comes_first()
    {
        Assert.Equal(new StopRequest(StopKind.Kill), Parse("--kill-server", "--stop-server"));
    }

    [Theory]
    [InlineData(StopKind.Graceful, 0, "stop")]
    [InlineData(StopKind.Countdown, 60, "stop 60")]
    [InlineData(StopKind.Kill, 0, "kill")]
    public void Wire_form_round_trips(StopKind kind, int seconds, string wire)
    {
        var request = new StopRequest(kind, seconds);
        Assert.Equal(wire, request.ToWire());
        Assert.Equal(request, StopRequest.FromWire(wire));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("start")]
    [InlineData("stop now")]
    [InlineData("stop 3601")]
    public void Junk_on_the_wire_is_rejected(string? line)
    {
        Assert.Null(StopRequest.FromWire(line));
    }

    [Fact]
    public void Wire_parsing_tolerates_case_and_padding()
    {
        Assert.Equal(new StopRequest(StopKind.Kill), StopRequest.FromWire("  KILL  "));
        Assert.Equal(new StopRequest(StopKind.Countdown, 15), StopRequest.FromWire("Stop  15"));
    }
}
