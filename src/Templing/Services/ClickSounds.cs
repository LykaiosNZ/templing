namespace Templing.Services;

/// <summary>A metronome click: a hi/lo pair of samples, <c>wwwroot/sounds/{Id}_hi.wav</c> and <c>{Id}_lo.wav</c>.</summary>
public record ClickSound(string Id, string Name, string Group);

/// <summary>
/// The metronome's click sounds. Samples by Ludwig Peter Müller (CC0, see <c>wwwroot/sounds/Credits.txt</c>).
/// The hi sample plays the accented first beat, the lo one the other beats and subdivisions.
/// Audio cues (intervals, countdowns) don't use these: they're synthesised in metronome.js.
/// </summary>
public static class ClickSounds
{
    /// <summary>The click when nothing has been chosen (Settings > Metronome > Click sound).</summary>
    public const string Default = "Perc_MetronomeQuartz";

    static readonly Dictionary<string, string> Names = new()
    {
        ["ClickToy"] = "Click toy",
        ["HeadKnock"] = "Head knock",
        ["MetronomeQuartz"] = "Quartz metronome",
        ["MouthPop"] = "Mouth pop",
        ["MusicStand"] = "Music stand",
        ["PracticePad"] = "Practice pad",
        ["Tamb"] = "Tambourine",
        ["TrashCan"] = "Trash can",
        ["WhistleParty"] = "Party whistle",
        ["WhistleRef"] = "Referee whistle",
    };

    static readonly string[] Ids =
    [
        "Perc_Can", "Perc_Castanet", "Perc_Chair", "Perc_Clackhead", "Perc_Clap", "Perc_ClickToy", "Perc_Glass",
        "Perc_HeadKnock", "Perc_Keyboard", "Perc_Metal", "Perc_MetronomeQuartz", "Perc_MouthPop", "Perc_MusicStand",
        "Perc_PracticePad", "Perc_Snap", "Perc_Squeak", "Perc_Stick", "Perc_Tamb_A", "Perc_Tamb_B", "Perc_Tamb_C",
        "Perc_Tamb_D", "Perc_Teeth", "Perc_Tongue", "Perc_TrashCan", "Perc_WhistleParty", "Perc_WhistleRef",
        "Synth_Bell_A", "Synth_Bell_B",
        "Synth_Block_A", "Synth_Block_B", "Synth_Block_C", "Synth_Block_D", "Synth_Block_E", "Synth_Block_F", "Synth_Block_G", "Synth_Block_H",
        "Synth_Sine_A", "Synth_Sine_B", "Synth_Sine_C", "Synth_Sine_D", "Synth_Sine_E", "Synth_Sine_F",
        "Synth_Square_A", "Synth_Square_B", "Synth_Square_C", "Synth_Square_D", "Synth_Square_E",
        "Synth_Tick_A", "Synth_Tick_B", "Synth_Tick_C", "Synth_Tick_D", "Synth_Tick_E", "Synth_Tick_F", "Synth_Tick_G", "Synth_Tick_H",
        "Synth_Weird_A", "Synth_Weird_B", "Synth_Weird_C", "Synth_Weird_D", "Synth_Weird_E",
    ];

    /// <summary>Every sound, percussion first, in the order the pickers list them.</summary>
    public static readonly IReadOnlyList<ClickSound> All = Ids.Select(Make).ToList();

    static ClickSound Make(string id)
    {
        // <Type>_<Name>[_<Variation>]
        var parts = id.Split('_');
        var name = Names.TryGetValue(parts[1], out var n) ? n : Words(parts[1]);
        if (parts.Length > 2) name += " " + parts[2];
        return new ClickSound(id, name, parts[0] == "Perc" ? "Percussion" : "Synth");
    }

    /// <summary>"ClickToy" → "Click toy".</summary>
    static string Words(string camel) =>
        string.Concat(camel.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    public static ClickSound? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>A sound that exists: <paramref name="id"/>, else the default (old settings say "click", "wood" or "beep").</summary>
    public static string Resolve(string? id) => Find(id)?.Id ?? Default;

    public static string NameOf(string? id) => (Find(id) ?? Find(Default)!).Name;
}
