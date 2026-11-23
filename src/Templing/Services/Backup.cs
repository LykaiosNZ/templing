using System.Text;
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

    /// <summary>The backup file as UTF-8 JSON, ready to save (<see cref="DataStore.DownloadAsync(string, byte[], string)"/>).</summary>
    public static byte[] ExportUtf8(DataStore store, DateTimeOffset? now = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new BackupFile(Format, AppInfo.InformationalVersion, now ?? DateTimeOffset.Now,
            store.Pieces, store.Logs, store.Presets, store.Settings, store.Plans), Indented);

    public static string Export(DataStore store, DateTimeOffset? now = null) => Encoding.UTF8.GetString(ExportUtf8(store, now));

    public static BackupFile Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>Reads and validates a backup file (UTF-8 JSON) without changing anything. Throws a <see cref="FormatException"/>
    /// whose message can be shown to the user as it is.</summary>
    public static BackupFile Parse(byte[] utf8)
    {
        try
        {
            // A current backup is read straight from the bytes. Only an older one is loaded as a JSON tree to migrate,
            // which for a big backup takes a few times longer.
            var b = CheckFormat(utf8) == Format
                ? JsonSerializer.Deserialize<BackupFile>(utf8, DataStore.Json)
                : Migrate(utf8).Deserialize<BackupFile>(DataStore.Json);
            return b ?? throw Damaged();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw Damaged();
        }
    }

    /// <summary>The backup's format, once it's known to be one this build can restore (else a <see cref="FormatException"/>).</summary>
    static int CheckFormat(byte[] utf8)
    {
        int? format = null, version = null;
        string? appVersion = null;
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw NotABackup();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("Format")) format = ReadInt(p.Value);
                else if (p.NameEquals("Version")) version = ReadInt(p.Value);
                else if (p.NameEquals("AppVersion") && p.Value.ValueKind == JsonValueKind.String) appVersion = p.Value.GetString();
            }
        }
        catch (JsonException)
        {
            throw NotABackup();
        }
        // formats 1–3 kept the format number in "Version"
        var f = format ?? version ?? throw NotABackup();
        if (f > Format)
        {
            var by = appVersion != null ? $"Templing {appVersion.Split('+')[0]}" : "a newer version of Templing";
            throw new FormatException($"This backup was made by {by}, newer than this one. Reload Templing to update it, then try again.");
        }
        if (f < OldestSupported)
            throw new FormatException("This backup is from an early test version of Templing and can't be restored.");
        return f;
    }

    static int? ReadInt(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;

    public static JsonObject Migrate(string json) => Migrate(Encoding.UTF8.GetBytes(json));

    /// <summary>Checks a backup's format and brings its JSON up to the current <see cref="Format"/>.</summary>
    public static JsonObject Migrate(byte[] utf8)
    {
        var format = CheckFormat(utf8);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(utf8) as JsonObject ?? throw NotABackup();
        }
        catch (JsonException)
        {
            throw NotABackup();
        }

        for (; format < Format; format++)
        {
            Migrations[format](root);
            root["Format"] = format + 1;
        }
        return root;
    }

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
