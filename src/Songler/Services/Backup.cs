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
    /// 3 = "item" renamed to "piece" everywhere (Items → Pieces, ItemId / ItemName → PieceId / PieceName,
    ///     plan step kind Item → Piece). Versions 1–2 can't be restored (pre-release, no migration kept).
    /// </summary>
    public const int Version = 3;
    const int OldestSupported = 3;

    /// <summary>The backup file. New fields must be nullable with a default so older files still deserialize.</summary>
    public record BackupFile(
        int Version,
        List<Piece>? Pieces,
        List<PracticeLog>? Logs,
        List<MetronomePreset>? Presets,
        AppSettings? Settings,
        List<PracticePlan>? Plans = null);

    private static readonly JsonSerializerOptions Indented = new(DataStore.Json) { WriteIndented = true };

    public static string Export(DataStore store) =>
        JsonSerializer.Serialize(new BackupFile(Version, store.Pieces, store.Logs, store.Presets, store.Settings, store.Plans), Indented);

    /// <summary>Reads and validates a backup file without changing anything.</summary>
    public static BackupFile Parse(string json)
    {
        var b = JsonSerializer.Deserialize<BackupFile>(json, DataStore.Json) ?? throw new FormatException("Empty backup");
        if (b.Version > Version) throw new FormatException($"This backup is from a newer version of Songler (v{b.Version}).");
        if (b.Version < OldestSupported) throw new FormatException($"This backup is from an older version of Songler (v{b.Version}) and can't be restored.");
        return b;
    }

    /// <summary>Short description of what's in a backup, e.g. "28 pieces, 384 log entries, 2 plans".</summary>
    public static string Describe(BackupFile b)
    {
        var parts = new List<string> { $"{b.Pieces?.Count ?? 0} pieces", $"{b.Logs?.Count ?? 0} log entries" };
        if (b.Plans is { Count: > 0 } pl) parts.Add($"{pl.Count} plans");
        if (b.Presets is { Count: > 0 } pr) parts.Add($"{pr.Count} custom metronomes");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Merges a backup into the current data (same ids are replaced). Settings are only applied when
    /// <paramref name="includeSettings"/> is set (the user chooses in the restore dialog); otherwise this
    /// device keeps its own preferences. Returns a short summary.
    /// </summary>
    /// <remarks>
    /// Score files (Guitar Pro) are binary blobs in the "files" store and are not part of the backup;
    /// pieces keep their ScoreFileId and show a "re-import" message if the file is missing.
    /// After restoring settings, reload the app: services hold references into the old settings object.
    /// </remarks>
    public static async Task<string> RestoreAsync(DataStore store, BackupFile b, bool includeSettings)
    {
        await store.ImportAsync(b.Pieces ?? [], b.Logs ?? [], b.Presets ?? [], b.Plans ?? []);
        var restoredSettings = includeSettings && b.Settings != null;
        if (restoredSettings) await store.ReplaceSettingsAsync(b.Settings!);
        return $"Restored {Describe(b)}{(restoredSettings ? " and settings" : "")}";
    }
}
