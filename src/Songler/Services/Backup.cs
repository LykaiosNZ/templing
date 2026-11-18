using System.Text.Json;
using Songler.Models;

namespace Songler.Services;

/// <summary>
/// JSON backup / restore of everything the user owns.
///
/// ⚠ When you add a new persisted entity (a new IndexedDB store / DataStore list), add it here:
/// a nullable field on <see cref="BackupFile"/> (so older backups still load), fill it in <see cref="Export"/>,
/// restore it in <see cref="RestoreAsync"/>, and bump <see cref="Version"/>. See CLAUDE.md → "Persisted data".
/// </summary>
public static class Backup
{
    /// <summary>
    /// Format history:
    /// 1 = items, logs, presets, settings.
    /// 2 = + plans.
    /// </summary>
    public const int Version = 2;

    /// <summary>The backup file. New fields must be nullable with a default so older files still deserialize.</summary>
    public record BackupFile(
        int Version,
        List<PracticeItem>? Items,
        List<PracticeLog>? Logs,
        List<MetronomePreset>? Presets,
        AppSettings? Settings,
        List<PracticePlan>? Plans = null);

    private static readonly JsonSerializerOptions Indented = new(DataStore.Json) { WriteIndented = true };

    public static string Export(DataStore store) =>
        JsonSerializer.Serialize(new BackupFile(Version, store.Items, store.Logs, store.Presets, store.Settings, store.Plans), Indented);

    /// <summary>Merges a backup into the current data (same ids are replaced). Returns a short summary.</summary>
    /// <remarks>
    /// Settings are exported but deliberately not restored, so restoring on another device doesn't overwrite
    /// that device's preferences. Score files (Guitar Pro) are binary blobs in the "files" store and are not
    /// part of the backup; items keep their ScoreFileId and show a "re-import" message if the file is missing.
    /// </remarks>
    public static async Task<string> RestoreAsync(DataStore store, string json)
    {
        var b = JsonSerializer.Deserialize<BackupFile>(json, DataStore.Json) ?? throw new FormatException("Empty backup");
        if (b.Version > Version) throw new FormatException($"This backup is from a newer version of Songler (v{b.Version}).");
        await store.ImportAsync(b.Items ?? [], b.Logs ?? [], b.Presets ?? [], b.Plans ?? []);
        var parts = new List<string> { $"{b.Items?.Count ?? 0} items", $"{b.Logs?.Count ?? 0} log entries" };
        if (b.Plans is { Count: > 0 } pl) parts.Add($"{pl.Count} plans");
        if (b.Presets is { Count: > 0 } pr) parts.Add($"{pr.Count} custom metronomes");
        return "Restored " + string.Join(", ", parts);
    }
}
