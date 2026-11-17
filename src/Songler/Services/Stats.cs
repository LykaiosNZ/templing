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

    public static string Duration(int sec, bool shortForm = false)
    {
        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalHours >= 1) return shortForm ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return sec == 0 ? "0m" : $"{sec}s";
    }

    public static string Clock(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public static string Ago(DateTime? d, DateTime today)
    {
        if (d is null) return "never";
        var days = (today.Date - d.Value.Date).Days;
        return days switch { 0 => "today", 1 => "yesterday", < 7 => $"{days} days ago", < 30 => $"{days / 7}w ago", _ => d.Value.ToString("d MMM yyyy") };
    }
}
