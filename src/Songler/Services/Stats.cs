using Songler.Models;

namespace Songler.Services;

public record ItemStats(
    int DaysPracticed,
    int TotalSec,
    int Sessions,
    double? Last,
    double? LastMin,
    double? Best,
    DateTime? LastDate,
    DateTime? FirstDate,
    double? First,
    double? Progress);

public record SectionStats(Section Section, double? Last, double? Best, DateTime? LastDate, int Sessions, int TotalSec, double? Progress, int? Effort);

public record TempoPoint(DateTime Date, double Value, double? Min, string? Note, int? Effort = null);

public static class Stats
{
    public static DateTime WeekStart(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Monday start
        return d.Date.AddDays(-diff);
    }

    public static ItemStats ForItem(PracticeItem item, IEnumerable<PracticeLog> allLogs, string? sectionId = null)
    {
        var logs = allLogs.Where(l => l.ItemId == item.Id && (sectionId == null || l.SectionId == sectionId))
            .OrderBy(l => l.Date).ToList();
        var valued = logs.Where(l => l.Value.HasValue && l.Unit == item.Unit).ToList();
        var last = valued.LastOrDefault();
        var first = valued.FirstOrDefault();
        double? progress = item.Goal is > 0 && last?.Value is { } v ? v / item.Goal.Value : null;
        // multi-part items: overall progress is the average across parts (an unpractised part counts as 0)
        if (sectionId == null && item.HasParts())
        {
            var parts = ForSections(item, allLogs);
            if (parts.Any(p => p.Progress.HasValue)) progress = parts.Average(p => Math.Min(1, p.Progress ?? 0));
        }
        return new ItemStats(
            logs.Select(l => l.Date.Date).Distinct().Count(),
            logs.Sum(l => l.DurationSec ?? 0),
            logs.Count,
            last?.Value, last?.ValueMin,
            valued.Count > 0 ? valued.Max(l => l.Value!.Value) : null,
            logs.LastOrDefault()?.Date,
            logs.FirstOrDefault()?.Date,
            first?.Value,
            progress);
    }

    /// <summary>
    /// Per-section status. A section's latest tempo comes from its own logs or from a whole-piece
    /// run-through (no section), whichever is more recent: playing the full song at 90% means each part was at 90%.
    /// </summary>
    public static List<SectionStats> ForSections(PracticeItem item, IEnumerable<PracticeLog> allLogs)
    {
        var logs = allLogs.Where(l => l.ItemId == item.Id).OrderBy(l => l.Date).ToList();
        var known = item.Sections.Select(s => s.Id).ToHashSet();
        return item.Sections.Select(s =>
        {
            var own = logs.Where(l => l.SectionId == s.Id).ToList();
            var relevant = logs.Where(l => l.SectionId == s.Id || l.SectionId == null || !known.Contains(l.SectionId)).ToList();
            var valued = relevant.Where(l => l.Value.HasValue && l.Unit == item.Unit).ToList();
            var last = valued.LastOrDefault();
            double? progress = item.Goal is > 0 && last?.Value is { } v ? v / item.Goal.Value : null;
            return new SectionStats(s, last?.Value, valued.Count > 0 ? valued.Max(l => l.Value!.Value) : null,
                own.LastOrDefault()?.Date, own.Count, own.Sum(l => l.DurationSec ?? 0), progress,
                relevant.LastOrDefault(l => l.Effort.HasValue)?.Effort);
        }).ToList();
    }

    /// <summary>Daily tempo points (max of the day) for an item, in the item's unit.</summary>
    public static List<TempoPoint> TempoHistory(PracticeItem item, IEnumerable<PracticeLog> allLogs, string? sectionId = null) =>
        allLogs.Where(l => l.ItemId == item.Id && l.Value.HasValue && l.Unit == item.Unit && (sectionId == null || l.SectionId == sectionId))
            .GroupBy(l => l.Date.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var top = g.MaxBy(l => l.Value)!;
                return new TempoPoint(g.Key, top.Value!.Value, top.ValueMin, string.Join(" · ", g.Where(x => !string.IsNullOrWhiteSpace(x.Notes)).Select(x => x.Notes)),
                    top.Effort ?? g.LastOrDefault(x => x.Effort.HasValue)?.Effort);
            }).ToList();

    /// <summary>Most recent rated session for an item, if any.</summary>
    public static PracticeLog? LastRated(PracticeItem item, IEnumerable<PracticeLog> allLogs) =>
        allLogs.Where(l => l.ItemId == item.Id && l.Effort.HasValue).OrderBy(l => l.Date).LastOrDefault();

    public static Dictionary<DateTime, int> SecondsByDay(IEnumerable<PracticeLog> logs) =>
        logs.GroupBy(l => l.Date.Date).ToDictionary(g => g.Key, g => g.Sum(l => l.DurationSec ?? 0));

    public static HashSet<DateTime> DaysPracticed(IEnumerable<PracticeLog> logs) => logs.Select(l => l.Date.Date).ToHashSet();

    public static (int current, int longest) Streaks(IEnumerable<PracticeLog> logs, DateTime today)
    {
        var days = DaysPracticed(logs);
        if (days.Count == 0) return (0, 0);
        // current streak: today counts if practiced; otherwise allow streak to end yesterday
        int current = 0;
        var d = days.Contains(today.Date) ? today.Date : today.Date.AddDays(-1);
        while (days.Contains(d)) { current++; d = d.AddDays(-1); }
        int longest = 0, run = 0; DateTime? prev = null;
        foreach (var day in days.OrderBy(x => x))
        {
            run = prev.HasValue && (day - prev.Value).TotalDays == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
            prev = day;
        }
        return (current, longest);
    }

    public static List<(DateTime week, int sec)> WeeklySeconds(IEnumerable<PracticeLog> logs, DateTime today, int weeks)
    {
        var byWeek = logs.GroupBy(l => WeekStart(l.Date)).ToDictionary(g => g.Key, g => g.Sum(l => l.DurationSec ?? 0));
        var start = WeekStart(today).AddDays(-7 * (weeks - 1));
        return Enumerable.Range(0, weeks).Select(i => start.AddDays(7 * i))
            .Select(w => (w, byWeek.GetValueOrDefault(w))).ToList();
    }

    /// <summary>Axis labels for <see cref="WeeklySeconds"/>; the last (current) week reads "This week".</summary>
    public static List<string> WeekLabels(List<(DateTime week, int sec)> weeks) =>
        weeks.Select((w, i) => i == weeks.Count - 1 && w.week == WeekStart(DateTime.Today) ? "This week" : w.week.ToString("d MMM")).ToList();

    /// <summary>Tooltip labels for <see cref="WeeklySeconds"/>.</summary>
    public static List<string> WeekTips(List<(DateTime week, int sec)> weeks) =>
        weeks.Select((w, i) => i == weeks.Count - 1 && w.week == WeekStart(DateTime.Today) ? "This week (so far)" : $"Week of {w.week:d MMM yyyy}").ToList();

    /// <summary>Durations in text: "45 s", "10 min", "1 h", "1 h 5 min" (see DESIGN.md).</summary>
    public static string Duration(int sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        if (ts.TotalHours >= 1) return ts.Minutes == 0 ? $"{(int)ts.TotalHours} h" : $"{(int)ts.TotalHours} h {ts.Minutes} min";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes} min";
        return sec <= 0 ? "0 min" : $"{sec} s";
    }

    public static string DurationMin(double min) => Duration((int)Math.Round(min * 60));

    public static string Clock(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    /// <summary>Day label: "Sat 14 Nov", with the year only outside the current year (see DESIGN.md).</summary>
    public static string Day(DateTime d) => d.Year == DateTime.Today.Year ? d.ToString("ddd d MMM") : d.ToString("ddd d MMM yyyy");

    public static string Ago(DateTime? d, DateTime today)
    {
        if (d is null) return "never";
        var days = (today.Date - d.Value.Date).Days;
        return days switch { 0 => "today", 1 => "yesterday", < 7 => $"{days} days ago", < 30 => $"{days / 7}w ago", _ => d.Value.ToString("d MMM yyyy") };
    }
}
