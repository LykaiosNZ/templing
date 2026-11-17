using System.Text.Json.Serialization;

namespace Songler.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemType { Exercise, RiffLick, Song, Other }

/// <summary>How progress on an item is measured: absolute BPM, or percent of the original/target tempo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TempoUnit { Bpm, Percent }

public static class ItemTypeExt
{
    public static string Label(this ItemType t) => t switch
    {
        ItemType.RiffLick => "Riff / Lick",
        _ => t.ToString()
    };

    public static string Icon(this ItemType t) => t switch
    {
        ItemType.Exercise => MudBlazor.Icons.Material.Rounded.FitnessCenter,
        ItemType.RiffLick => MudBlazor.Icons.Material.Rounded.Bolt,
        ItemType.Song => MudBlazor.Icons.Material.Rounded.LibraryMusic,
        _ => MudBlazor.Icons.Material.Rounded.MusicNote
    };

    public static string Description(this ItemType t) => t switch
    {
        ItemType.Song => "A whole song, broken into sections (intro, verse, solo…). Track each part and the full run-through.",
        ItemType.RiffLick => "A one-off riff or lick, usually a single passage.",
        ItemType.Exercise => "Technique work tracked by metronome BPM.",
        _ => "Anything else: scales, theory, ear training…"
    };

    /// <summary>Display order: songs first.</summary>
    public static readonly ItemType[] Ordered = [ItemType.Song, ItemType.RiffLick, ItemType.Exercise, ItemType.Other];

    /// <summary>True when an item is practised as separate parts (songs, or anything with several sections).</summary>
    public static bool HasParts(this PracticeItem i) => i.Sections.Count > 1;

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

    public Section Clone() => (Section)MemberwiseClone();
    public string Meter => $"{BeatsPerBar}/{BeatUnit}";
}

public class PracticeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ItemType Type { get; set; } = ItemType.Exercise;
    public TempoUnit Unit { get; set; } = TempoUnit.Bpm;
    /// <summary>Goal tempo in <see cref="Unit"/> (BPM, or percent of original tempo).</summary>
    public double? Goal { get; set; }
    public List<Section> Sections { get; set; } = new() { new Section() };
    public string? Notes { get; set; }
    public string? Link { get; set; }
    public bool Archived { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

public class PracticeLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Today;
    public string? ItemId { get; set; }
    /// <summary>Name snapshot (also used for free-metronome sessions with no item).</summary>
    public string ItemName { get; set; } = "";
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

/// <summary>A saved custom metronome (tempo map) that isn't tied to a library item.</summary>
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
    public double Increment { get; set; } = 2;
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
}

public class AppSettings
{
    public string Id { get; set; } = "app";
    public bool DarkMode { get; set; } = true;
    public MetronomeSettings Metronome { get; set; } = new();
    public List<Section> FreeSections { get; set; } = new() { new Section { Name = "Click", Bpm = 100 } };
    public bool FreePlayThrough { get; set; }
    public int WeeklyGoalMin { get; set; } = 150;
}
