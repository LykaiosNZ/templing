using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Songler.Models;

namespace Songler.Services;

/// <summary>Imports the "Pieces" and "Practice Log" sheets exported from Google Sheets as CSV.</summary>
public static partial class CsvImport
{
    public record Result(List<PracticeItem> NewItems, List<PracticeItem> UpdatedItems, List<PracticeLog> Logs, int Skipped);

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(sb.ToString()); sb.Clear();
                rows.Add(row.ToArray()); row.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)")]
    private static partial Regex NumberRx();

    /// <summary>Parses "70 - 90", "65- 145", "95", "70%" into (min, max, unit).</summary>
    public static (double? min, double? max, TempoUnit unit) ParseSpeed(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null, TempoUnit.Bpm);
        var unit = s.Contains('%') ? TempoUnit.Percent : TempoUnit.Bpm;
        var nums = NumberRx().Matches(s).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) return (null, null, unit);
        if (nums.Count == 1) return (null, nums[0], unit);
        return (nums.Min(), nums.Max(), unit);
    }

    private static ItemType ParseType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "riff / lick" or "riff" or "lick" or "rifflick" => ItemType.RiffLick,
        "song" => ItemType.Song,
        "exercise" => ItemType.Exercise,
        _ => ItemType.Other
    };

    private static int Col(string[] header, params string[] names)
    {
        for (int i = 0; i < header.Length; i++)
            if (names.Any(n => header[i].Trim().Equals(n, StringComparison.OrdinalIgnoreCase))) return i;
        return -1;
    }

    private static string At(string[] row, int i) => i >= 0 && i < row.Length ? row[i].Trim() : "";

    private static int? ParseEffort(string s)
    {
        if (int.TryParse(s, out var n) && n is >= 1 and <= 5) return n;
        return Effort.Levels.FirstOrDefault(l => l.Label.Equals(s, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    public static bool LooksLikePieces(string[] header) => Col(header, "Speed Goal") >= 0 || (Col(header, "Name") >= 0 && Col(header, "Type") >= 0);
    public static bool LooksLikeLog(string[] header) => Col(header, "Date") >= 0 && Col(header, "Exercise", "Item", "Name") >= 0;

    public static Result Import(string text, DataStore store)
    {
        var rows = Parse(text);
        if (rows.Count == 0) return new([], [], [], 0);
        var header = rows[0];
        var known = store.Items.ToList();
        var newItems = new List<PracticeItem>();
        var updated = new List<PracticeItem>();
        var logs = new List<PracticeLog>();
        int skipped = 0;

        PracticeItem? Find(string name) =>
            known.FirstOrDefault(i => string.Equals(i.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (LooksLikePieces(header))
        {
            int cName = Col(header, "Name"), cType = Col(header, "Type"), cGoal = Col(header, "Speed Goal", "Goal");
            foreach (var r in rows.Skip(1))
            {
                var name = At(r, cName);
                if (name.Length == 0) continue;
                var (_, goal, unit) = ParseSpeed(At(r, cGoal));
                var existing = Find(name);
                var item = existing ?? new PracticeItem { Name = name };
                item.Type = ParseType(At(r, cType));
                if (goal.HasValue || existing == null)
                {
                    item.Unit = goal.HasValue ? unit : item.Unit;
                    item.Goal = goal;
                }
                if (existing == null)
                {
                    item.Sections = [new Section { Name = "Main", Bpm = item.Unit == TempoUnit.Bpm && goal is > 0 ? goal.Value : 120 }];
                    newItems.Add(item); known.Add(item);
                }
                else updated.Add(item);
            }
        }
        else if (LooksLikeLog(header))
        {
            int cDate = Col(header, "Date"), cName = Col(header, "Exercise", "Item", "Name"),
                cSpeed = Col(header, "BPM/Speed", "Speed", "BPM"), cTime = Col(header, "Time (min)", "Time", "Minutes"),
                cNotes = Col(header, "Notes"), cFeel = Col(header, "Feel", "Effort");
            foreach (var r in rows.Skip(1))
            {
                var name = At(r, cName);
                if (name.Length == 0) continue;
                if (!DateTime.TryParseExact(At(r, cDate), ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                { skipped++; continue; }
                var (min, max, unit) = ParseSpeed(At(r, cSpeed));
                int? dur = double.TryParse(At(r, cTime), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? (int)Math.Round(m * 60) : null;
                var item = Find(name);
                if (item == null)
                {
                    item = new PracticeItem
                    {
                        Name = name, Type = ItemType.Exercise, Unit = unit,
                        Sections = [new Section { Name = "Main", Bpm = unit == TempoUnit.Bpm && max is > 0 ? max.Value : 120 }]
                    };
                    newItems.Add(item); known.Add(item);
                }
                var notes = At(r, cNotes);
                var log = new PracticeLog
                {
                    Date = date, ItemId = item.Id, ItemName = item.Name, Unit = unit,
                    Value = max, ValueMin = min, DurationSec = dur,
                    Notes = notes.Length > 0 ? notes : null, Source = "import",
                    Effort = ParseEffort(At(r, cFeel))
                };
                bool dupe = store.Logs.Concat(logs).Any(l => l.Date == log.Date && l.ItemId == log.ItemId && l.Value == log.Value
                                                           && l.ValueMin == log.ValueMin && l.DurationSec == log.DurationSec && l.Notes == log.Notes);
                if (dupe) { skipped++; continue; }
                logs.Add(log);
            }
        }
        else throw new FormatException("Unrecognised CSV. Expected a Pieces sheet (Name, Type, Speed Goal) or a Practice Log sheet (Date, Exercise, BPM/Speed, Time (min), Notes).");

        return new(newItems, updated, logs, skipped);
    }

    public static string ExportLogCsv(DataStore store)
    {
        static string Q(string? s) => s is null ? "" : s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder("Date,Exercise,Section,BPM/Speed,Time (min),Feel,Notes\n");
        foreach (var l in store.Logs.OrderBy(l => l.Date))
        {
            var item = store.GetItem(l.ItemId);
            var section = item?.Sections.FirstOrDefault(s => s.Id == l.SectionId)?.Name;
            var suf = l.Unit == TempoUnit.Percent ? "%" : "";
            var speed = l.Value is null ? "" : l.ValueMin is { } mn && mn != l.Value ? $"{mn:0} - {l.Value:0}{suf}" : $"{l.Value:0}{suf}";
            var mins = l.DurationSec is { } d ? (d / 60.0).ToString("0.#", CultureInfo.InvariantCulture) : "";
            sb.Append($"{l.Date:dd/MM/yyyy},{Q(l.ItemName)},{Q(section)},{Q(speed)},{mins},{Q(Effort.Get(l.Effort)?.Label)},{Q(l.Notes)}\n");
        }
        return sb.ToString();
    }
}
