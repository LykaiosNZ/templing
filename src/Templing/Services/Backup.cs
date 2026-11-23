using System.Text.Json;
using System.Text.Json.Nodes;
using Templing.Models;

namespace Templing.Services;

/// <summary>
/// JSON backup / restore of everything the user owns.
///
/// ⚠ When you add a new persisted entity (a new IndexedDB store / DataStore list), add it here:
/// a nullable field on <see cref="BackupFile"/> (so older backups still load), fill it in <see cref="Export"/>,
/// restore it in <see cref="RestoreAsync"/>, and bump <see cref="Format"/>. See CLAUDE.md → "Persisted data".
/// </summary>
/// <remarks>
/// A backup carries two versions: <see cref="BackupFile.Format"/>, the layout of the file, which only changes when
/// the file does, and <see cref="BackupFile.AppVersion"/>, the Templing build that wrote it, which is only for people
/// (the restore dialog and error messages). Compatibility is decided by the format alone.
///
/// Backups must keep restoring: a file from any format since <see cref="OldestSupported"/> is brought up to the
/// current one by <see cref="Migrations"/> before it's read. Every format has a sample file in
/// tests/Templing.Tests/Backups that has to restore, so a change that breaks an old backup fails the tests.
/// </remarks>
public static class Backup
{
    /// <summary>
    /// The current backup format. Bump it (and add a migration from the previous one, and a sample file) when a
    /// backup written now would be read wrongly by the current code without help, or by an older build: a new list,
    /// a field renamed, moved or removed, a value that changes meaning, a new enum value. A new nullable field with a
    /// default on an existing type doesn't need it.
    ///
    /// Format history:
    /// 1 = items, logs, presets, settings.
    /// 2 = + plans.
    /// 3 = "item" renamed to "piece" everywhere (Items → Pieces, ItemId / ItemName → PieceId / PieceName,
    ///     plan step kind Item → Piece). Formats 1–2 can't be restored (pre-release, no migration kept).
    /// 4 = the format number is "Format" (it was "Version", which read like the app's version), and the file
    ///     records the app version that wrote it ("AppVersion") and when ("Exported").
    /// </summary>
    public const int Format = 4;

    /// <summary>The oldest format that still restores. Never raise it: that breaks people's backups.</summary>
    public const int OldestSupported = 3;

    /// <summary>
    /// Upgrades a backup's JSON from format <c>key</c> to <c>key + 1</c>, in place (<see cref="Migrate"/> then sets
    /// the format number). Work on the raw JSON, not the model classes: they only know the current shape.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, Action<JsonObject>> Migrations = new Dictionary<int, Action<JsonObject>>
    {
        [3] = b => b.Remove("Version"),
    };

    /// <summary>The backup file. New fields must be nullable with a default so older files still deserialize.</summary>
    public record BackupFile(
        int Format,
        string? AppVersion,
        DateTimeOffset? Exported,
        List<Piece>? Pieces,
        List<PracticeLog>? Logs,
        List<MetronomePreset>? Presets,
        AppSettings? Settings,
        List<PracticePlan>? Plans = null);

    private static readonly JsonSerializerOptions Indented = new(DataStore.Json) { WriteIndented = true };

    public static string Export(DataStore store, DateTimeOffset? now = null) =>
        JsonSerializer.Serialize(new BackupFile(Format, AppInfo.InformationalVersion, now ?? DateTimeOffset.Now,
            store.Pieces, store.Logs, store.Presets, store.Settings, store.Plans), Indented);

    /// <summary>Reads and validates a backup file without changing anything. Throws a <see cref="FormatException"/>
    /// whose message can be shown to the user as it is.</summary>
    public static BackupFile Parse(string json)
    {
        var root = Migrate(json);
        try
        {
            return root.Deserialize<BackupFile>(DataStore.Json) ?? throw Damaged();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw Damaged();
        }
    }

    /// <summary>Checks a backup's format and brings its JSON up to the current <see cref="Format"/>.</summary>
    public static JsonObject Migrate(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw NotABackup();
        }
        catch (JsonException)
        {
            throw NotABackup();
        }

        // formats 1–3 kept the format number in "Version"
        var format = ReadInt(root["Format"]) ?? ReadInt(root["Version"]) ?? throw NotABackup();
        if (format > Format)
        {
            var by = root["AppVersion"] is JsonValue v && v.TryGetValue<string>(out var app) ? $"Templing {app.Split('+')[0]}" : "a newer version of Templing";
            throw new FormatException($"This backup was made by {by}, newer than this one. Reload Templing to update it, then try again.");
        }
        if (format < OldestSupported)
            throw new FormatException("This backup is from an early test version of Templing and can't be restored.");

        for (; format < Format; format++)
        {
            Migrations[format](root);
            root["Format"] = format + 1;
        }
        return root;
    }

    static int? ReadInt(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    static FormatException NotABackup() => new("That file isn't a Templing backup.");
    static FormatException Damaged() => new("That backup couldn't be read. The file may be damaged.");

    /// <summary>Short description of what's in a backup, e.g. "28 pieces, 384 log entries and settings".</summary>
    public static string Describe(BackupFile b, bool withSettings)
    {
        var parts = new List<string> { Count(b.Pieces?.Count ?? 0, "piece"), Count(b.Logs?.Count ?? 0, "log entry", "log entries") };
        if (b.Plans is { Count: > 0 } pl) parts.Add(Count(pl.Count, "plan"));
        if (b.Presets is { Count: > 0 } pr) parts.Add(Count(pr.Count, "custom metronome"));
        if (withSettings) parts.Add("settings");
        return parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.SkipLast(1))} and {parts[^1]}";
    }

    /// <summary>Where a backup came from, e.g. "Made by Templing 1.2.0 (3f2a9c1) on Mon 23 Nov", or null when the
    /// file doesn't say (format 3 and older).</summary>
    public static string? Origin(BackupFile b)
    {
        if (b.AppVersion == null && b.Exported == null) return null;
        var text = "Made by Templing";
        if (b.AppVersion != null) text += $" {AppInfo.Format(b.AppVersion)}";
        if (b.Exported is { } when) text += $" on {Stats.Day(when.LocalDateTime)}";
        return text;
    }

    static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

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
        return $"Restored {Describe(b, restoredSettings)}";
    }
}
