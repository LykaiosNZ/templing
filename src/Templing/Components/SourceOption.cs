using Templing.Models;
using Templing.Services;

namespace Templing.Components;

public enum SourceKind { Free, Piece, Preset, Heading, Browse }

/// <summary>One entry in <see cref="SourcePicker"/>: something the Metronome or Timer can load, a heading, or "Browse all…".
/// A record, so the picker's selected value compares by content (MudAutocomplete with Strict off needs that).</summary>
public sealed record SourceOption(SourceKind Kind, string Id, string Name)
{
    public string Key => Kind switch
    {
        SourceKind.Piece => $"piece:{Id}",
        SourceKind.Preset => $"preset:{Id}",
        _ => "free",
    };

    public static SourceOption For(Piece p) => new(SourceKind.Piece, p.Id, p.Name);
    public static SourceOption For(MetronomePreset p) => new(SourceKind.Preset, p.Id, p.Name);

    /// <summary>A key's kind and id: "piece:abc" → ("piece", "abc"); "free" → ("free", "").</summary>
    public static (string Kind, string Id) Split(string key) =>
        key.IndexOf(':') is var c and > 0 ? (key[..c], key[(c + 1)..]) : (key, "");

    /// <summary>The Library's search: a piece's name, or its group's.</summary>
    public static bool Matches(DataStore store, Piece p, string text) =>
        p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || store.GetGroup(p.GroupId)?.Name.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
}
