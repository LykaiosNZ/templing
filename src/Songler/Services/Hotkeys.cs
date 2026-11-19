namespace Songler.Services;

/// <summary>
/// One keyboard shortcut: what it does, where it's listed, and every key combo that triggers it.
/// Combos use the keys.js format: "t", "?", "Space", "Escape", "ArrowUp", "Shift+ArrowUp", "g d" (G then D).
/// </summary>
public sealed record Hotkey(string Group, string Description, IReadOnlyList<string> Combos);

/// <summary>
/// The single list of keyboard shortcuts. Handlers bind to these entries (<see cref="Shortcuts.Register(object, Hotkey, Func{Task})"/>)
/// and the shortcuts dialog is generated from <see cref="All"/>, so a key can't work without being listed.
/// Groups and rows appear in the order they're declared here.
/// </summary>
public static class Hotkeys
{
    // declared first: static fields initialise in textual order, and every entry below adds itself to it
    static readonly List<Hotkey> _all = new();

    /// <summary>Every hotkey, in display order.</summary>
    public static IReadOnlyList<Hotkey> All => _all;

    static Hotkey Add(string group, string description, params string[] combos)
    {
        foreach (var c in combos)
            if (_all.FirstOrDefault(h => h.Combos.Contains(c)) is { } clash)
                throw new InvalidOperationException($"Hotkey \"{c}\" is used by both \"{clash.Description}\" and \"{description}\".");
        var h = new Hotkey(group, description, combos);
        _all.Add(h);
        return h;
    }

    const string General = "General", Navigation = "Navigation", Metronome = "Metronome", Plan = "Plan";

    /// <summary>Short note shown beside a group's heading in the shortcuts dialog.</summary>
    public static string? GroupNote(string group) => group switch
    {
        Metronome => "on every page",
        Plan => "while a plan runs",
        _ => null,
    };

    public static readonly Hotkey LogPractice = Add(General, "Log practice", "n");
    public static readonly Hotkey SearchLibrary = Add(General, "Search library", "/");
    public static readonly Hotkey Help = Add(General, "Keyboard shortcuts", "?");

    public static readonly Hotkey GoDashboard = Add(Navigation, "Go to dashboard", "g d");
    public static readonly Hotkey GoLibrary = Add(Navigation, "Go to library", "g l");
    public static readonly Hotkey GoMetronome = Add(Navigation, "Go to metronome", "g m");
    public static readonly Hotkey GoTimer = Add(Navigation, "Go to timer", "g t");
    public static readonly Hotkey GoPlans = Add(Navigation, "Go to plans", "g p");
    public static readonly Hotkey GoHistory = Add(Navigation, "Go to history", "g h");
    public static readonly Hotkey GoStats = Add(Navigation, "Go to stats", "g s");
    public static readonly Hotkey GoSettings = Add(Navigation, "Go to settings", "g ,");

    public static readonly Hotkey StartStop = Add(Metronome, "Start / stop the metronome or timer", "Space");
    public static readonly Hotkey Stop = Add(Metronome, "Stop", "Escape");
    public static readonly Hotkey RestartSection = Add(Metronome, "Restart section (with count-in)", "r");
    public static readonly Hotkey TempoUp = Add(Metronome, "Tempo +1", "ArrowUp", "+", "=");
    public static readonly Hotkey TempoDown = Add(Metronome, "Tempo −1", "ArrowDown", "-");
    public static readonly Hotkey TempoUp5 = Add(Metronome, "Tempo +5", "Shift+ArrowUp");
    public static readonly Hotkey TempoDown5 = Add(Metronome, "Tempo −5", "Shift+ArrowDown");
    public static readonly Hotkey PrevSection = Add(Metronome, "Previous section", "ArrowLeft", "[");
    public static readonly Hotkey NextSection = Add(Metronome, "Next section", "ArrowRight", "]");
    public static readonly Hotkey Tap = Add(Metronome, "Tap tempo", "t");
    public static readonly Hotkey Mute = Add(Metronome, "Sound on / off", "m");
    public static readonly Hotkey CountIn = Add(Metronome, "Count-in on / off", "c");
    public static readonly Hotkey SpeedTrainer = Add(Metronome, "Speed trainer on / off", "x");
    public static readonly Hotkey IntervalTrainer = Add(Metronome, "Interval trainer on / off", "i");
    public static readonly Hotkey LogSession = Add(Metronome, "Stop and log", "l");

    public static readonly Hotkey PrevPlanStep = Add(Plan, "Previous step", "Shift+ArrowLeft");
    public static readonly Hotkey NextPlanStep = Add(Plan, "Next step (finish on the last)", "Shift+ArrowRight");

    /// <summary>
    /// How a combo reads in the shortcuts dialog: key caps and separators,
    /// e.g. "Shift+ArrowUp" → [Shift] + [↑], "g d" → [G] then [D].
    /// </summary>
    public static IEnumerable<(string text, bool key)> Display(string combo)
    {
        var steps = combo.Split(' ');
        for (int s = 0; s < steps.Length; s++)
        {
            if (s > 0) yield return ("then", false);
            // "+" on its own is a key, not a separator
            var parts = steps[s] == "+" ? ["+"] : steps[s].Split('+');
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) yield return ("+", false);
                yield return (KeyName(parts[p]), true);
            }
        }
    }

    static string KeyName(string k) => k switch
    {
        "ArrowUp" => "↑",
        "ArrowDown" => "↓",
        "ArrowLeft" => "←",
        "ArrowRight" => "→",
        "Escape" => "Esc",
        _ => k.Length == 1 ? k.ToUpperInvariant() : k,
    };
}
