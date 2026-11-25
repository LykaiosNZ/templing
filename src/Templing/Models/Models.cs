using System.Text.Json.Serialization;

namespace Templing.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PieceType { Exercise, RiffLick, Song, Other }

/// <summary>How progress on an piece is measured: absolute BPM, or percent of the original/target tempo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TempoUnit { Bpm, Percent }

/// <summary>The tempo range, per unit: the one place it's set. Every tempo field, the slider, Set tempo and
/// <c>MetronomeService.SetValueAsync</c> take their limits from here.</summary>
public static class TempoLimits
{
    public const double MinBpm = 20, MaxBpm = 400;
    public const double MinPercent = 10, MaxPercent = 200;
    public static double Min(TempoUnit unit) => unit == TempoUnit.Percent ? MinPercent : MinBpm;
    public static double Max(TempoUnit unit) => unit == TempoUnit.Percent ? MaxPercent : MaxBpm;
}

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
    /// <summary>Clicks per beat (the division): in 4/4, 1 = 1/4, 2 = 1/8, 3 = 1/8t, 4 = 1/16. See <see cref="Division"/>.</summary>
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

    /// <summary>
    /// Bottom numbers a time signature can have: note values. The engine would play anything (a beat is 4 ÷ unit
    /// quarter notes), but 7ths and the like aren't note values, and Guitar Pro never produces them.
    /// </summary>
    public static readonly int[] BeatUnits = [1, 2, 4, 8, 16, 32];

    // clicks per beat that make a note value: straight (1, 2, 4…) or triplet (3, 6, 12…)
    static readonly int[] DivisionCounts = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    /// <summary>
    /// The division (what clicks) as a note value: clicks per beat on a beat of 1/<paramref name="beatUnit"/>.
    /// In 4/4: 1 → "1/4", 2 → "1/8", 3 → "1/8t", 4 → "1/16". In 6/8 the same counts read "1/8", "1/16", "1/16t"…
    /// </summary>
    public static string DivisionName(int subdivision, int beatUnit)
    {
        var sub = Math.Max(1, subdivision);
        if ((sub & (sub - 1)) == 0) return $"1/{beatUnit * sub}";
        if (sub % 3 == 0 && ((sub / 3) & (sub / 3 - 1)) == 0) return $"1/{beatUnit * sub / 3 * 2}t";
        return $"{sub} per beat";
    }

    /// <summary>
    /// Division options for a beat unit, slowest first: straight up to 1/32, triplets up to 1/16t.
    /// <paramref name="current"/> is kept in the list even if it's outside that range (older data).
    /// </summary>
    public static IReadOnlyList<(int Value, string Label)> Divisions(int beatUnit, int? current = null) =>
        DivisionCounts.Where(n => n == current || (n % 3 == 0 ? beatUnit * n / 3 * 2 <= 16 : beatUnit * n <= 32))
            .Select(n => (n, DivisionName(n, beatUnit))).ToList();

    /// <summary>This section's division as a note value ("1/8t").</summary>
    public string Division => DivisionName(Subdivision, BeatUnit);

    /// <summary>After a meter change: keep the division if it's still offered, else the nearest slower one.</summary>
    public void ClampDivision()
    {
        var options = Divisions(BeatUnit);
        if (options.Any(o => o.Value == Subdivision)) return;
        Subdivision = options.LastOrDefault(o => o.Value <= Subdivision).Value is var v and > 0 ? v : 1;
    }
}

public class Piece
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PieceType Type { get; set; } = PieceType.Exercise;
    public TempoUnit Unit { get; set; } = TempoUnit.Bpm;
    /// <summary>Goal tempo in <see cref="Unit"/> (BPM, or percent of original tempo).</summary>
    public double? Goal { get; set; }
    /// <summary>
    /// Where the metronome opens for this piece, in <see cref="Unit"/>. Null: the slowest tempo of the last session
    /// (the low end of a "95–155" log), else the last tempo, else the section's tempo. Also the speed trainer's start.
    /// </summary>
    public double? StartTempo { get; set; }
    public List<Section> Sections { get; set; } = new() { new Section() };
    /// <summary>The metronome plays the sections in order (false: it loops the current one). Remembered from the last change.</summary>
    public bool PlayThrough { get; set; }
    /// <summary>When playing through: go back to the first section at the end (false: stop there).</summary>
    public bool Loop { get; set; }
    public string? Notes { get; set; }
    public string? Link { get; set; }
    /// <summary>Attached score file (Guitar Pro / MusicXML) stored in IndexedDB, used for the tab view.</summary>
    public string? ScoreFileId { get; set; }
    public string? ScoreFileName { get; set; }
    public int ScoreTrack { get; set; }
    /// <summary>"tab" or "score" (standard notation + tab).</summary>
    public string ScoreStave { get; set; } = "tab";
    public bool Archived { get; set; }
    /// <summary>Starred: shown under Favourites on the Dashboard.</summary>
    public bool Favourite { get; set; }
    /// <summary>The metronome's click for this piece (a <c>ClickSounds</c> id); null = the default from Settings.</summary>
    public string? Sound { get; set; }
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
    /// <summary>Tempo to start at. Null = where the piece opens (its Start at, else the low end of the last session).</summary>
    public double? StartTempo { get; set; }
    /// <summary>
    /// Speed trainer for this step only: ramps from TrainerStart to TrainerEnd, spread over the step's work time or
    /// one jump every TrainerEveryBars bars, in TrainerSteps whole-number steps (null = one per bpm / %).
    /// </summary>
    public bool Trainer { get; set; }
    public double TrainerStart { get; set; } = 80;
    public double TrainerEnd { get; set; } = 120;
    public RampMode TrainerMode { get; set; } = RampMode.OverTime;
    public int? TrainerSteps { get; set; }
    public int TrainerEveryBars { get; set; } = 4;

    /// <summary>The step's trainer as trainer settings (Over time runs across the step's work time).</summary>
    public TrainingSettings TrainerSettings() => new()
    {
        Enabled = Trainer, Start = TrainerStart, End = TrainerEnd, Mode = TrainerMode, Steps = TrainerSteps,
        EveryBars = TrainerEveryBars, DurationMin = WorkMinutes, StopAtEnd = false,
    };

    public double WorkMinutes => Minutes * Math.Max(1, Rounds);

    public string TempoText(TempoUnit unit)
    {
        if (Kind != PlanStepKind.Piece || Mode != StepMode.Metronome) return "";
        var u = unit == TempoUnit.Percent ? "%" : " bpm";
        if (Trainer) return $"trainer {TrainerStart:0} → {TrainerEnd:0}{u}" + (TrainerMode == RampMode.EveryNBars ? $", every {TrainerEveryBars} bars" : "");
        return StartTempo is { } t ? $"start {t:0}{u}" : "";
    }

    public double TotalMinutes => Kind == PlanStepKind.Rest ? Minutes : Minutes * Math.Max(1, Rounds) + RestMin * Math.Max(0, Rounds - 1);
    public string IntervalText => Kind == PlanStepKind.Rest ? $"{Dur(Minutes)} rest"
        : Rounds > 1 ? $"{Rounds} × {Dur(Minutes)}{(RestMin > 0 ? $", {Dur(RestMin)} rests" : "")}" : Dur(Minutes);

    static string Dur(double min) => Templing.Services.Stats.DurationMin(min);
}

/// <summary>An ordered practice routine (warm-up, exercises, songs…).</summary>
public class PracticePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My routine";
    public List<PlanStep> Steps { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastRun { get; set; }
    /// <summary>Starred: shown under Favourites on the Dashboard.</summary>
    public bool Favourite { get; set; }
    public double TotalMinutes => Steps.Sum(s => s.TotalMinutes);
}

/// <summary>A saved custom metronome (tempo map) that isn't tied to a library piece.</summary>
public class MetronomePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom";
    public List<Section> Sections { get; set; } = new() { new Section() };
    public bool PlayThrough { get; set; }
    /// <summary>When playing through: go back to the first section at the end (false: stop there).</summary>
    public bool Loop { get; set; } = true;
    /// <summary>The click for this custom metronome (a <c>ClickSounds</c> id); null = the default from Settings.</summary>
    public string? Sound { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RampMode { OverTime, EveryNBars }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BeatDisplayStyle { Strip, Ring, Both }

/// <summary>How a segment fills: all at once on its click, or sweeping across until the next one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BeatFill { OnClick, Sweep }

/// <summary>What the filled segments do when a bar starts: clear, fade out over the first beat, or (every other bar) empty as it plays.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NewBarStyle { Clear, Fade, EmptyOut }

/// <summary>How bold the pulse on each new bar is (<see cref="MetronomeSettings.BarPulse"/>): Strong is the original pulse.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PulseIntensity { Subtle, Medium, Strong }

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
    /// <summary>Volume, 0–1 (shown as 0–100%). The click samples peak at -1 dBFS, so this only turns them down
    /// (1 = the loudest without clipping). Older versions allowed up to 1.5, so saved settings and backups are clamped as they load.</summary>
    public double Volume { get => _volume; set => _volume = Math.Clamp(value, 0, 1); }
    double _volume = 1;
    /// <summary>The default click (a <c>ClickSounds</c> id); pieces and custom metronomes can pick their own. Unknown ids
    /// (the old synthesised "click", "wood", "beep") play <c>ClickSounds.Default</c>.</summary>
    public string Sound { get; set; } = "";
    /// <summary>A higher click on the first beat of each bar. Sound only: the beat display has <see cref="MarkFirstBeat"/>.</summary>
    public bool AccentFirst { get; set; } = true;
    /// <summary>The glow behind the tempo on each beat (stronger on beat 1 while <see cref="MarkFirstBeat"/> is on).</summary>
    public bool Flash { get; set; } = true;
    /// <summary>A slider under the tempo on the Metronome page (off: the buttons, Set tempo and the keys are enough).</summary>
    public bool TempoSlider { get; set; }

    // ---- beat display (Metronome page; js/beatdisplay.js)
    public BeatDisplayStyle BeatDisplay { get; set; } = BeatDisplayStyle.Strip;
    public BeatFill BeatFill { get; set; } = BeatFill.OnClick;
    public NewBarStyle NewBar { get; set; } = NewBarStyle.Clear;
    /// <summary>A ripple out from the strip or ring as each bar starts.</summary>
    public bool BarPulse { get; set; } = true;
    /// <summary>How bold the pulse is: brightness, line width and how far it spreads.</summary>
    public PulseIntensity BarPulseIntensity { get; set; } = PulseIntensity.Medium;
    /// <summary>Beat 1's empty segments are tinted, so you can see where the bar starts.</summary>
    public bool MarkFirstBeat { get; set; } = true;
    /// <summary>A segment per click; off, a segment per beat.</summary>
    public bool ShowSubdivisions { get; set; } = true;
    /// <summary>Segments between beats are drawn thinner (only while <see cref="ShowSubdivisions"/>).</summary>
    public bool ThinSubdivisions { get; set; }
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
    /// <summary>The free metronome's click (a <c>ClickSounds</c> id); null = <see cref="MetronomeSettings.Sound"/>.</summary>
    public string? FreeSound { get; set; }
    public int WeeklyGoalMin { get; set; } = 150;
    /// <summary>Library shows pieces as a list (rows) instead of cards.</summary>
    public bool LibraryList { get; set; }
    /// <summary>The Metronome's interval presets; null = the built-in ones (IntervalsEditor.MetronomeDefaults).</summary>
    public List<IntervalPreset>? MetronomeIntervalPresets { get; set; }
    /// <summary>The Timer's countdown presets; null = the built-in ones (IntervalsEditor.TimerDefaults).</summary>
    public List<IntervalPreset>? TimerIntervalPresets { get; set; }
    /// <summary>Plans: save each step's log automatically when it finishes instead of asking.</summary>
    public bool AutoLogPlanSteps { get; set; }
    /// <summary>Plans: start the next piece step as soon as the last step (or rest) is done, as rests always do.</summary>
    public bool AutoStartPlanSteps { get; set; }
    /// <summary>Plans: start a piece step straight away when Skip or Back moves to it.</summary>
    public bool AutoStartPlanStepsOnSkip { get; set; }
    /// <summary>Settings > Backup: export as gzip (.json.gz) instead of plain JSON. Restore reads either.</summary>
    public bool CompressBackups { get; set; }
    /// <summary>Custom theme colours; null = the built-in ones. See <c>Layout.ThemeColours</c> for the keys.</summary>
    public CustomColours? Colours { get; set; }
    /// <summary>Settings > Data: send anonymous usage stats (Umami page views and a few events, see <c>Services.Analytics</c>).</summary>
    public bool Analytics { get; set; } = true;
}

/// <summary>Colours the user picked, per mode: slot key (ThemeColours.Slots) → "#rrggbb". Slots not listed use the built-in colour.</summary>
public class CustomColours
{
    public Dictionary<string, string> Light { get; set; } = new();
    public Dictionary<string, string> Dark { get; set; } = new();
    public Dictionary<string, string> For(bool dark) => dark ? Dark : Light;
}
