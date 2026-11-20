using System.Text.Json;
using Microsoft.JSInterop;
using Templing.Models;

namespace Templing.Services;

public record ScoreTrackInfo(int Index, string Name);
public record ScoreBarInfo(int N, int D, double T, string? Marker);
public record ScoreInfo(string Title, string Artist, List<ScoreTrackInfo> Tracks, List<ScoreBarInfo> Bars, List<int> Order);

/// <summary>Reads score files through alphaTab (js/score.js) and turns their tempo map into song parts.</summary>
public class ScoreImport(IJSRuntime js)
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private IJSObjectReference? _mod;

    public static readonly string[] Extensions = [".gp", ".gpx", ".gp5", ".gp4", ".gp3", ".musicxml", ".mxl", ".xml"];

    public async Task<IJSObjectReference> ModuleAsync() =>
        _mod ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/score.js");

    public async Task<ScoreInfo> ParseAsync(byte[] bytes, string fileName)
    {
        var mod = await ModuleAsync();
        var json = await mod.InvokeAsync<string>("parse", bytes, fileName);
        return JsonSerializer.Deserialize<ScoreInfo>(json, Opts) ?? throw new FormatException("Empty score");
    }

    /// <summary>Saves the most recently parsed file under a new id and returns it.</summary>
    public async Task<string> SaveParsedAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        await (await ModuleAsync()).InvokeVoidAsync("savePending", id);
        return id;
    }

    /// <summary>Tempo changes smaller than this (bpm) don't start a new part.</summary>
    public const double TempoTolerance = 2;

    /// <summary>
    /// Builds parts by walking the bars in play order:
    /// a section marker starts a new part (unless we just jumped back for a repeat),
    /// and so does a tempo change. Time-signature changes stay inside the part (per-bar meters).
    /// </summary>
    public static List<Section> BuildParts(ScoreInfo score)
    {
        var parts = new List<Section>();
        if (score.Bars.Count == 0) return parts;
        bool anyMarkers = score.Bars.Any(b => !string.IsNullOrWhiteSpace(b.Marker));
        Section? cur = null;
        string curBase = "";
        int prev = -1;

        foreach (var m in score.Order)
        {
            if (m < 0 || m >= score.Bars.Count) continue;
            var b = score.Bars[m];
            bool forward = m > prev;
            bool marker = forward && !string.IsNullOrWhiteSpace(b.Marker);
            bool tempoChange = cur != null && Math.Abs(b.T - cur.Bpm) > TempoTolerance;
            if (cur == null || marker || tempoChange)
            {
                curBase = marker ? b.Marker!.Trim()
                    : cur == null ? (anyMarkers ? "Start" : "Part")
                    : curBase;
                cur = new Section
                {
                    Name = curBase,
                    Bpm = Math.Round(b.T),
                    BeatsPerBar = b.N,
                    BeatUnit = b.D,
                    Subdivision = 1,
                    ScoreBars = new()
                };
                parts.Add(cur);
            }
            cur.ScoreBars!.Add([m, b.N, b.D]);
            prev = m;
        }

        // names: single unnamed part = "Full song"; otherwise number duplicates "Chorus", "Chorus (2)"
        if (parts.Count == 1 && !anyMarkers) parts[0].Name = "Full song";
        else if (!anyMarkers) for (int i = 0; i < parts.Count; i++) parts[i].Name = $"Part {i + 1}";
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            seen[p.Name] = seen.GetValueOrDefault(p.Name) + 1;
            if (seen[p.Name] > 1) p.Name = $"{p.Name} ({seen[p.Name]})";
        }
        foreach (var p in parts) p.Bars = p.ScoreBars!.Count;
        return parts;
    }

    /// <summary>Appends <paramref name="next"/> onto <paramref name="into"/> (used by "merge with previous").</summary>
    public static void Merge(Section into, Section next)
    {
        into.ScoreBars = [.. into.ScoreBars ?? [], .. next.ScoreBars ?? []];
        into.Bars = into.ScoreBars.Count;
    }

    /// <summary>Picks a sensible default track: the first guitar-ish track, else the first.</summary>
    public static int DefaultTrack(ScoreInfo s)
    {
        bool Has(ScoreTrackInfo t, string k) => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase);
        var pick = s.Tracks.FirstOrDefault(t => Has(t, "lead") && (Has(t, "guitar") || Has(t, "gtr")))
                   ?? s.Tracks.FirstOrDefault(t => Has(t, "guitar") || Has(t, "gtr"));
        return pick?.Index ?? 0;
    }
}
