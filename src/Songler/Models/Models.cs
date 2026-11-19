using System.Text.Json.Serialization;

namespace Songler.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PieceType { Exercise, RiffLick, Song, Other }

/// <summary>How progress on an piece is measured: absolute BPM, or percent of the original/target tempo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TempoUnit { Bpm, Percent }

public static class PieceTypeExt
{
    public static string Label(this PieceType t) => t switch
    {
        PieceType.RiffLick => "Riff / Lick",
        _ => t.ToString()
    };

    public static string Icon(this PieceType t) => t switch
    {
        PieceType.Exercise => MudBlazor.Icons.Material.Rounded.FitnessCenter,
        PieceType.RiffLick => MudBlazor.Icons.Material.Rounded.Bolt,
        PieceType.Song => MudBlazor.Icons.Material.Rounded.LibraryMusic,
        _ => MudBlazor.Icons.Material.Rounded.MusicNote
    };

    /// <summary>Display order: songs first.</summary>
    public static readonly PieceType[] Ordered = [PieceType.Song, PieceType.RiffLick, PieceType.Exercise, PieceType.Other];

    /// <summary>True when an piece is practised as separate parts (songs, or anything with several sections).</summary>
    public static bool HasParts(this Piece i) => i.Sections.Count > 1;

    public static string Suffix(this TempoUnit u) => u == TempoUnit.Percent ? "%" : " bpm";

    /// <summary>Common song-part names offered as quick-add chips.</summary>
    public static readonly string[] SongParts = ["Intro", "Verse", "Pre-chorus", "Chorus", "Bridge", "Solo", "Breakdown", "Outro"];
}

/// <summary>A section of a piece (or a step in a custom metronome) with its own tempo and meter.</summary>
public class Section
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Main";
    /// <summary>The section's reference tempo (the song tempo, or the target tempo for exercises).</summary>
    public double Bpm { get; set; } = 120;
    public int BeatsPerBar { get; set; } = 4;
    public int BeatUnit { get; set; } = 4;
    /// <summary>Clicks per beat: 1 = quarters, 2 = eighths, 3 = triplets, 4 = sixteenths...</summary>
    public int Subdivision { get; set; } = 1;
    /// <summary>Length in bars when playing through a sequence. 0 = loop until moved on.</summary>
    public int Bars { get; set; } = 0;

    /// <summary>
    /// Set when the section came from a score file: the score bars it covers, in play order
    /// (repeats expanded), each as [masterBarIndex, numerator, denominator]. The metronome follows
    /// these meters bar by bar and the tab view uses the indices to stay in sync.
    /// </summary>
    public List<int[]>? ScoreBars { get; set; }

    public Section Clone() => (Section)MemberwiseClone();
    public bool FromScore => ScoreBars is { Count: > 0 };
    /// <summary>Length in bars (score-linked sections use their score bar count).</summary>
    public int Length => FromScore ? ScoreBars!.Count : Bars;
    public bool MixedMeter => FromScore && ScoreBars!.Any(b => b[1] != ScoreBars![0][1] || b[2] != ScoreBars[0][2]);
    public string Meter => $"{BeatsPerBar}/{BeatUnit}{(MixedMeter ? " (mixed)" : "")}";

    /// <summary>Click options, in menu order: clicks per beat and their name.</summary>
    public static readonly (int Value, string Label)[] Subdivisions = [(1, "Beats"), (2, "8ths"), (3, "Triplets"), (4, "16ths"), (6, "Sextuplets")];

    /// <summary>"Beats", "8ths", "Triplets"… for a subdivision.</summary>
    public static string ClickName(int subdivision) =>
        Subdivisions.FirstOrDefault(x => x.Value == subdivision).Label ?? $"{subdivision} per beat";
}

public class Piece
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PieceType Type { get; set; } = PieceType.Exercise;
    public TempoUnit Unit { get; set; } = TempoUnit.Bpm;
    /// <summary>Goal tempo in <see cref="Unit"/> (BPM, or percent of original tempo).</summary>
    public double? Goal { get; set; }
    public List<Section> Sections { get; set; } = new() { new Section() };
    public string? Notes { get; set; }
    public string? Link { get; set; }
    /// <summary>Attached score file (Guitar Pro / MusicXML) stored in IndexedDB, used for the tab view.</summary>
    public string? ScoreFileId { get; set; }
    public string? ScoreFileName { get; set; }
    public int ScoreTrack { get; set; }
    /// <summary>"tab" or "score" (standard notation + tab).</summary>
    public string ScoreStave { get; set; } = "tab";
    public bool Archived { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

public class PracticeLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Today;
    public string? PieceId { get; set; }
    /// <summary>Name snapshot (also used for free-metronome sessions with no piece).</summary>
    public string PieceName { get; set; } = "";
    public string? SectionId { get; set; }
    public TempoUnit Unit { get; set; } = TempoUnit.Bpm;
    /// <summary>Lowest tempo for ranged sessions (e.g. a 65 - 145 warmup). Null for single values.</summary>
    public double? ValueMin { get; set; }
    /// <summary>Top tempo reached (bpm or %).</summary>
    public double? Value { get; set; }
    /// <summary>Active practice time in seconds (rests excluded). Null if not recorded.</summary>
    public int? DurationSec { get; set; }
    public string? Notes { get; set; }
    /// <summary>How the session felt, 1 (too easy, push the tempo) … 5 (too hard, slow down). Null = not rated.</summary>
    public int? Effort { get; set; }
    /// <summary>When the entry was first saved; orders entries within a day. Null for entries saved before this existed.</summary>
    public DateTime? Logged { get; set; }
    /// <summary>"manual", "metronome", "import".</summary>
    public string Source { get; set; } = "manual";

    public string ValueText
    {
        get
        {
            if (Value is null) return "—";
            var suf = Unit == TempoUnit.Percent ? "%" : "";
            return ValueMin is { } min && min != Value ? $"{min:0}–{Value:0}{suf}" : $"{Value:0}{suf}";
        }
    }
}

public static class Effort
{
    public record Level(int Value, string Label, string Advice, string Color);

    /// <summary>Green (too easy) → red (too hard). Mid-scale is the sweet spot.</summary>
    public static readonly Level[] Levels =
    [
        new(1, "Too easy", "Push the tempo up", "#0ca30c"),
        new(2, "Comfortable", "Ready to nudge faster", "#7cb518"),
        new(3, "Just right", "Challenging but clean, stay here", "#eda100"),
        new(4, "Struggling", "Getting sloppy, consider easing off", "#ec835a"),
        new(5, "Too hard", "Slow down and clean it up", "#d03b3b"),
    ];

    public static Level? Get(int? v) => v is >= 1 and <= 5 ? Levels[v.Value - 1] : null;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanStepKind { Piece, Rest }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepMode { Metronome, Timer }

/// <summary>One step of a practice plan: practise an piece for a while, or rest.</summary>
public class PlanStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public PlanStepKind Kind { get; set; } = PlanStepKind.Piece;
    public string? PieceId { get; set; }
    /// <summary>Optional part/section of the piece to practise.</summary>
    public string? SectionId { get; set; }
    public StepMode Mode { get; set; } = StepMode.Metronome;
    /// <summary>Work minutes per round (or rest length for rest steps).</summary>
    public double Minutes { get; set; } = 10;
    public int Rounds { get; set; } = 1;
    /// <summary>Rest between rounds, in minutes.</summary>
    public double RestMin { get; set; }

    // ---- tempo (metronome steps), in the piece's unit (bpm or %)
    /// <summary>Tempo to start at. Null = the last logged tempo.</summary>
    public double? StartTempo { get; set; }
    /// <summary>Speed trainer for this step only: ramps from TrainerStart to TrainerEnd over the step's work time.</summary>
    public bool Trainer { get; set; }
    public double TrainerStart { get; set; } = 80;
    public double TrainerEnd { get; set; } = 120;

    public double WorkMinutes => Minutes * Math.Max(1, Rounds);

    public string TempoText(TempoUnit unit)
    {
        if (Kind != PlanStepKind.Piece || Mode != StepMode.Metronome) return "";
        var u = unit == TempoUnit.Percent ? "%" : " bpm";
        if (Trainer) return $"{TrainerStart:0} → {TrainerEnd:0}{u}";
        return StartTempo is { } t ? $"start {t:0}{u}" : "";
    }

    public double TotalMinutes => Kind == PlanStepKind.Rest ? Minutes : Minutes * Math.Max(1, Rounds) + RestMin * Math.Max(0, Rounds - 1);
    public string IntervalText => Kind == PlanStepKind.Rest ? $"{Dur(Minutes)} rest"
        : Rounds > 1 ? $"{Rounds} × {Dur(Minutes)}{(RestMin > 0 ? $", {Dur(RestMin)} rests" : "")}" : Dur(Minutes);

    static string Dur(double min) => Songler.Services.Stats.DurationMin(min);
}

/// <summary>An ordered practice routine (warm-up, exercises, songs…).</summary>
public class PracticePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My routine";
    public List<PlanStep> Steps { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastRun { get; set; }
    public double TotalMinutes => Steps.Sum(s => s.TotalMinutes);
}

/// <summary>A saved custom metronome (tempo map) that isn't tied to a library piece.</summary>
public class MetronomePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom";
    public List<Section> Sections { get; set; } = new() { new Section() };
    public bool PlayThrough { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RampMode { OverTime, EveryNBars }

public class TrainingSettings
{
    public bool Enabled { get; set; }
    public double Start { get; set; } = 80;
    public double End { get; set; } = 120;
    public RampMode Mode { get; set; } = RampMode.OverTime;
    public double DurationMin { get; set; } = 5;
    public int EveryBars { get; set; } = 4;
    /// <summary>Before Steps existed: "Change by" for Every N bars. Only used to derive <see cref="StepCount"/> for old settings.</summary>
    public double Increment { get; set; } = 2;
    /// <summary>How many equal jumps from Start to End. Null (old settings): derived, see <see cref="StepCount"/>.</summary>
    public int? Steps { get; set; }

    /// <summary>
    /// The number of jumps from Start to End. Unset, it's what the trainer did before Steps existed:
    /// Start–End ÷ Change by every N bars, or one bpm (or %) at a time over time.
    /// </summary>
    [JsonIgnore]
    public int StepCount => Math.Clamp(Steps ?? (Mode == RampMode.EveryNBars
        ? (int)Math.Ceiling(Math.Abs(End - Start) / Math.Max(0.5, Increment))
        : (int)Math.Round(Math.Abs(End - Start))), 1, MaxSteps);

    /// <summary>At most one step per whole bpm (or %), so no step is +0.</summary>
    [JsonIgnore]
    public int MaxSteps => Math.Max(1, (int)Math.Round(Math.Abs(End - Start)));

    /// <summary>
    /// The tempos played, Start first and End last (bpm or %), each a whole number. Rounding spreads the remainder,
    /// so the jumps differ by at most one: 128 → 150 in 4 steps is 128, 134, 139, 145, 150 (+6 +5 +6 +5).
    /// </summary>
    public IReadOnlyList<double> Levels()
    {
        var n = StepCount;
        return Enumerable.Range(0, n + 1)
            .Select(k => Math.Round(Start + (End - Start) * k / n, MidpointRounding.AwayFromZero))
            .ToList();
    }
    public bool StopAtEnd { get; set; }
}

public class IntervalSettings
{
    public bool Enabled { get; set; }
    public double WorkMin { get; set; } = 5;
    public double RestMin { get; set; } = 1;
    public int Rounds { get; set; } = 2;
    public bool VoiceCues { get; set; } = true;
}

/// <summary>A "Load preset" choice for intervals (Settings > Interval presets): work / rest minutes, rounds (0 = endless).</summary>
public class IntervalPreset
{
    public double WorkMin { get; set; } = 5;
    public double RestMin { get; set; }
    public int Rounds { get; set; } = 1;
}

public class MetronomeSettings
{
    public bool CountIn { get; set; } = true;
    public int CountInBars { get; set; } = 1;
    public double Volume { get; set; } = 0.8;
    public string Sound { get; set; } = "click";
    public bool AccentFirst { get; set; } = true;
    public bool Flash { get; set; } = true;
    public TrainingSettings Training { get; set; } = new();
    public IntervalSettings Interval { get; set; } = new();
    /// <summary>The Timer page's countdown (always on; separate from the metronome's intervals).</summary>
    public IntervalSettings Timer { get; set; } = new() { Enabled = true, WorkMin = 10, RestMin = 0, Rounds = 1 };
}

public class AppSettings
{
    public string Id { get; set; } = "app";
    public bool DarkMode { get; set; } = true;
    public MetronomeSettings Metronome { get; set; } = new();
    public List<Section> FreeSections { get; set; } = new() { new Section { Name = "Click", Bpm = 100 } };
    public bool FreePlayThrough { get; set; }
    public int WeeklyGoalMin { get; set; } = 150;
    /// <summary>The Metronome's interval presets; null = the built-in ones (IntervalsEditor.MetronomeDefaults).</summary>
    public List<IntervalPreset>? MetronomeIntervalPresets { get; set; }
    /// <summary>The Timer's countdown presets; null = the built-in ones (IntervalsEditor.TimerDefaults).</summary>
    public List<IntervalPreset>? TimerIntervalPresets { get; set; }
    /// <summary>Plans: save each step's log automatically when it finishes instead of asking.</summary>
    public bool AutoLogPlanSteps { get; set; }
}
