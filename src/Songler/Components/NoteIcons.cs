using Songler.Models;

namespace Songler.Components;

/// <summary>
/// Note-value icons for divisions (what clicks): a note for the value, plus a small "3" for triplets
/// (see <see cref="DivisionIcon"/>). Whole to sixteenth notes come from Pictogrammers MDI (<see cref="Mdi"/>).
/// </summary>
public static class NoteIcons
{
    /// <summary>
    /// A thirty-second note, which MDI lacks: its sixteenth note (same head and stem) with three flags in the space
    /// where that has two. Hand-made, so it lives here rather than in the generated Mdi.g.cs.
    /// </summary>
    public const string ThirtySecond = "<path d=\"M18 5.6V3H12V13.55C11.41 13.21 10.73 13 10 13C7.79 13 6 14.79 6 17S7.79 21 10 21 14 19.21 14 17V13H18V10.4H14V9.3H18V6.7H14V5.6H18Z\"/>";

    static readonly Dictionary<int, (string Icon, string Name)> Notes = new()
    {
        [1] = (Mdi.MusicNoteWhole, "whole"),
        [2] = (Mdi.MusicNoteHalf, "half"),
        [4] = (Mdi.MusicNoteQuarter, "quarter"),
        [8] = (Mdi.MusicNoteEighth, "eighth"),
        [16] = (Mdi.MusicNoteSixteenth, "sixteenth"),
        [32] = (ThirtySecond, "thirty-second"),
    };

    /// <summary>
    /// What a division looks like and is called: the note icon (null if there's none, e.g. 1/64), whether it's a
    /// triplet, and a name for tooltips and screen readers ("Eighth notes (1/8)", "Eighth-note triplets (1/8t)").
    /// </summary>
    public static (string? Icon, bool Triplet, string Name) ForDivision(int subdivision, int beatUnit)
    {
        var label = Section.DivisionName(subdivision, beatUnit); // "1/8t"
        var triplet = label.EndsWith('t');
        if (!label.StartsWith("1/") || !int.TryParse(label[2..].TrimEnd('t'), out var value) || !Notes.TryGetValue(value, out var note))
            return (null, false, label);
        var name = triplet ? $"{note.Name}-note triplets" : $"{note.Name} notes";
        return (note.Icon, triplet, $"{char.ToUpperInvariant(name[0])}{name[1..]} ({label})");
    }
}
