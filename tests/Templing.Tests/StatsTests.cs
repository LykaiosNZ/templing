namespace Templing.Tests;

public class StatsTests
{
    static readonly DateTime Today = new(2026, 11, 18); // a Wednesday

    static PracticeLog Log(Piece p, DateTime date, double? value = null, int? sec = null, string? section = null,
        double? min = null, int? effort = null, TempoUnit unit = TempoUnit.Bpm, string? notes = null) =>
        new() { PieceId = p.Id, PieceName = p.Name, Date = date, Value = value, ValueMin = min, DurationSec = sec, SectionId = section, Effort = effort, Unit = unit, Notes = notes };

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(-5, "0 min")]
    [InlineData(45, "45 sec")]
    [InlineData(60, "1 min")]
    [InlineData(599, "9 min")]
    [InlineData(3600, "1 hr")]
    [InlineData(3900, "1 hr 5 min")]
    [InlineData(90000, "25 hr")]
    public void Duration_reads_as_DESIGN_md_says(int sec, string expected) => Assert.Equal(expected, Stats.Duration(sec));

    [Fact]
    public void DurationMin_rounds_to_the_second() => Assert.Equal("2 min", Stats.DurationMin(2.004));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65.9, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    public void Clock_shows_hours_only_when_needed(double sec, string expected) => Assert.Equal(expected, Stats.Clock(sec));

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(6, "6 days ago")]
    [InlineData(7, "1w ago")]
    [InlineData(29, "4w ago")]
    public void Ago_counts_days_then_weeks(int daysAgo, string expected) =>
        Assert.Equal(expected, Stats.Ago(Today.AddDays(-daysAgo).AddHours(15), Today));

    [Fact]
    public void Ago_of_nothing_is_never() => Assert.Equal("never", Stats.Ago(null, Today));

    [Fact]
    public void Weeks_start_on_Monday()
    {
        Assert.Equal(new DateTime(2026, 11, 16), Stats.WeekStart(Today));
        Assert.Equal(new DateTime(2026, 11, 16), Stats.WeekStart(new DateTime(2026, 11, 16, 23, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 16), Stats.WeekStart(new DateTime(2026, 11, 22))); // Sunday
    }

    [Fact]
    public void Streaks_count_today_or_up_to_yesterday()
    {
        var p = new Piece();
        var days = new[] { 0, 1, 2, 5, 6, 7, 8 }.Select(d => Log(p, Today.AddDays(-d))).ToList();
        Assert.Equal((3, 4), Stats.Streaks(days, Today));
        // not practised yet today: the streak still stands, ending yesterday
        Assert.Equal((2, 4), Stats.Streaks(days.Where(l => l.Date != Today).ToList(), Today));
        // nothing yesterday or today: broken
        Assert.Equal((0, 4), Stats.Streaks(days, Today.AddDays(2)));
        Assert.Equal((0, 0), Stats.Streaks([], Today));
    }

    [Fact]
    public void Several_logs_on_one_day_count_once()
    {
        var p = new Piece();
        var logs = new[] { Log(p, Today.AddHours(9)), Log(p, Today.AddHours(20)), Log(p, Today.AddDays(-1)) };
        Assert.Equal((2, 2), Stats.Streaks(logs, Today));
        Assert.Equal(2, Stats.DaysPracticed(logs).Count);
    }

    [Fact]
    public void WeeklySeconds_fills_empty_weeks_oldest_first()
    {
        var p = new Piece();
        var logs = new[] { Log(p, Today, sec: 600), Log(p, Today.AddDays(-2), sec: 300), Log(p, Today.AddDays(-14), sec: 60) };
        var weeks = Stats.WeeklySeconds(logs, Today, 3);
        Assert.Equal([(new DateTime(2026, 11, 2), 60), (new DateTime(2026, 11, 9), 0), (new DateTime(2026, 11, 16), 900)], weeks);
    }

    [Fact]
    public void ForPiece_uses_only_this_piece_and_its_unit()
    {
        var p = new Piece { Goal = 120 };
        var other = new Piece();
        var logs = new[]
        {
            Log(p, Today.AddDays(-3), value: 80, sec: 600),
            Log(p, Today.AddDays(-2), value: 100, min: 90, sec: 300),
            Log(p, Today.AddDays(-1), value: 75, unit: TempoUnit.Percent), // another unit: no tempo
            Log(p, Today.AddDays(-1), sec: 120),                        // no tempo at all
            Log(other, Today, value: 200, sec: 999),
        };
        var s = Stats.ForPiece(p, logs);
        Assert.Equal(3, s.DaysPracticed);
        Assert.Equal(1020, s.TotalSec);
        Assert.Equal(4, s.Sessions);
        Assert.Equal(100, s.Last);
        Assert.Equal(90, s.LastMin);
        Assert.Equal(100, s.Best);
        Assert.Equal(80, s.First);
        Assert.Equal(100 / 120.0, s.Progress);
        Assert.Equal(Today.AddDays(-1), s.LastDate);
    }

    [Fact]
    public void ForPiece_without_a_goal_has_no_progress()
    {
        var p = new Piece();
        Assert.Null(Stats.ForPiece(p, [Log(p, Today, value: 100)]).Progress);
    }

    [Fact]
    public void A_whole_piece_run_through_counts_for_every_section()
    {
        var p = new Piece { Goal = 100, Sections = [new Section { Id = "a" }, new Section { Id = "b" }] };
        var logs = new[]
        {
            Log(p, Today.AddDays(-2), value: 60, section: "a"),
            Log(p, Today.AddDays(-1), value: 80),               // run-through
            Log(p, Today, value: 90, section: "b", effort: 4),
        };
        var parts = Stats.ForSections(p, logs);
        Assert.Equal(80, parts[0].Last); // the run-through is newer than a's own log
        Assert.Equal(1, parts[0].Sessions); // but only a's own logs count as its sessions
        Assert.Equal(90, parts[1].Last);
        Assert.Equal(4, parts[1].Effort);
        Assert.Null(parts[0].Effort);
    }

    [Fact]
    public void Multi_part_progress_averages_the_parts_capped_at_the_goal()
    {
        var p = new Piece { Goal = 100, Sections = [new Section { Id = "a" }, new Section { Id = "b" }, new Section { Id = "c" }] };
        var logs = new[] { Log(p, Today, value: 120, section: "a"), Log(p, Today, value: 50, section: "b") };
        // a: 1 (capped from 1.2), b: 0.5, c: never practised, 0
        Assert.Equal(0.5, Stats.ForPiece(p, logs).Progress!.Value, 6);
    }

    [Fact]
    public void TempoHistory_keeps_each_days_best_with_its_notes()
    {
        var p = new Piece();
        var logs = new[]
        {
            Log(p, Today.AddHours(9), value: 90, min: 70, notes: "warm-up", effort: 2),
            Log(p, Today.AddHours(18), value: 110, min: 95, notes: "clean"),
            Log(p, Today.AddDays(-1), value: 100),
        };
        var h = Stats.TempoHistory(p, logs);
        Assert.Equal(2, h.Count);
        Assert.Equal(Today.AddDays(-1), h[0].Date);
        Assert.Equal(110, h[1].Value);
        Assert.Equal(95, h[1].Min);
        Assert.Equal("warm-up · clean", h[1].Note);
        Assert.Equal(2, h[1].Effort); // the top log wasn't rated: the day's rating is used
    }

    [Fact]
    public void LastRated_is_the_newest_log_with_a_rating()
    {
        var p = new Piece();
        var rated = Log(p, Today.AddDays(-1), effort: 3);
        Assert.Same(rated, Stats.LastRated(p, [Log(p, Today.AddDays(-2), effort: 5), rated, Log(p, Today)]));
    }
}
