using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class RestartAnnouncerTests
{
    [Theory]
    [InlineData(RestartReason.Scheduled, 10, "Server restart in 10 minutes")]
    [InlineData(RestartReason.Manual, 5, "Server restart in 5 minutes")]
    [InlineData(RestartReason.Update, 1, "Update! Restarting in 1 min")]
    public void Message_substitutes_minutes_and_picks_template_by_reason(RestartReason reason, int minutes, string expected)
    {
        var result = RestartAnnouncer.Message(
            reason, TimeSpan.FromMinutes(minutes),
            restartTemplate: "Server restart in {minutes} minutes",
            updateTemplate: "Update! Restarting in {minutes} min");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Message_without_the_token_is_a_fixed_string()
    {
        var result = RestartAnnouncer.Message(
            RestartReason.Manual, TimeSpan.FromMinutes(5),
            restartTemplate: "Server going down now - save your progress.",
            updateTemplate: "unused");
        Assert.Equal("Server going down now - save your progress.", result);
    }

    [Fact]
    public void Message_floors_sub_minute_remaining_to_one()
    {
        var result = RestartAnnouncer.Message(
            RestartReason.Manual, TimeSpan.FromSeconds(20),
            restartTemplate: "{minutes}", updateTemplate: "{minutes}");
        Assert.Equal("1", result); // never "0"
    }

    [Fact]
    public void Schedule_places_largest_lead_first_at_zero_delay()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        var restartAt = now.AddMinutes(10);

        var marks = RestartAnnouncer.Schedule(new[] { 10, 5, 1 }, restartAt, now);

        Assert.Equal(3, marks.Count);
        Assert.Equal((TimeSpan.Zero, 10), (marks[0].Delay, marks[0].LeadMinutes));
        Assert.Equal((TimeSpan.FromMinutes(5), 5), (marks[1].Delay, marks[1].LeadMinutes));
        Assert.Equal((TimeSpan.FromMinutes(9), 1), (marks[2].Delay, marks[2].LeadMinutes));
    }

    [Fact]
    public void Schedule_dedupes_sorts_and_caps_at_three()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        var restartAt = now.AddMinutes(30);

        // Duplicates and more than three entries, out of order.
        var marks = RestartAnnouncer.Schedule(new[] { 5, 10, 5, 3, 1, 20 }, restartAt, now);

        Assert.Equal(new[] { 20, 10, 5 }, marks.Select(m => m.LeadMinutes).ToArray());
    }

    [Fact]
    public void Schedule_drops_non_positive_leads()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        var marks = RestartAnnouncer.Schedule(new[] { 0, -5, 10 }, now.AddMinutes(10), now);

        Assert.Equal(new[] { 10 }, marks.Select(m => m.LeadMinutes).ToArray());
    }

    [Fact]
    public void Schedule_empty_leads_gives_no_marks()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        Assert.Empty(RestartAnnouncer.Schedule(Array.Empty<int>(), now.AddMinutes(10), now));
    }

    [Fact]
    public void Schedule_lead_larger_than_window_yields_negative_delay()
    {
        // Restart only 3 minutes out but a 10-minute lead configured -> that mark is already past.
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        var marks = RestartAnnouncer.Schedule(new[] { 10 }, now.AddMinutes(3), now);

        Assert.Single(marks);
        Assert.Equal(TimeSpan.FromMinutes(-7), marks[0].Delay);
    }
}
