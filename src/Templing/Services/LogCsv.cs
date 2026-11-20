using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Templing.Models;

namespace Templing.Services;

/// <summary>Tempo text parsing for the log dialog, and CSV export of the practice log.</summary>
public static partial class LogCsv
{
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

    public static string ExportLogCsv(DataStore store)
    {
        static string Q(string? s) => s is null ? "" : s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder("Date,Exercise,Section,BPM/Speed,Time (min),Feel,Notes\n");
        foreach (var l in store.Logs.OrderBy(l => l.Date))
        {
            var piece = store.GetPiece(l.PieceId);
            var section = piece?.Sections.FirstOrDefault(s => s.Id == l.SectionId)?.Name;
            var suf = l.Unit == TempoUnit.Percent ? "%" : "";
            var speed = l.Value is null ? "" : l.ValueMin is { } mn && mn != l.Value ? $"{mn:0} - {l.Value:0}{suf}" : $"{l.Value:0}{suf}";
            var mins = l.DurationSec is { } d ? (d / 60.0).ToString("0.#", CultureInfo.InvariantCulture) : "";
            sb.Append($"{l.Date:dd/MM/yyyy},{Q(l.PieceName)},{Q(section)},{Q(speed)},{mins},{Q(Effort.Get(l.Effort)?.Label)},{Q(l.Notes)}\n");
        }
        return sb.ToString();
    }
}
