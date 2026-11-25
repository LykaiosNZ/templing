using System.Text.Json;
using Microsoft.JSInterop;
using Templing.Models;

namespace Templing.Services;

/// <summary>In-memory cache over IndexedDB. Load once at startup; writes go through to the DB.</summary>
public class DataStore(IJSRuntime js)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    private IJSObjectReference? _db;
    private Task? _loading;

    public List<Piece> Pieces { get; private set; } = new();
    public List<PracticeLog> Logs { get; private set; } = new();
    public List<MetronomePreset> Presets { get; private set; } = new();
    public List<PracticePlan> Plans { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();
    public bool Loaded { get; private set; }
    /// <summary>
    /// The demo build (<see cref="AppInfo.Demo"/>): db.js keeps everything in memory, seeded with sample data
    /// (DemoData, only in that build) at each start, so the browser's real data is never read or written.
    /// </summary>
    public bool Demo => AppInfo.Demo;

    /// <summary>Raised after any write.</summary>
    public event Action? Changed;
    /// <summary>Raised after a new piece is saved (not an edit, an import or a restore). For <see cref="Analytics"/>.</summary>
    public event Action<Piece>? PieceAdded;
    /// <summary>Raised after a new log is saved (not an edit, an import or a restore). For <see cref="Analytics"/>.</summary>
    public event Action<PracticeLog>? LogAdded;

    // Lookups built from the lists on first use and dropped on every change, so screens don't scan
    // every log for every piece (pieces × logs) each time they refresh.
    private Dictionary<string, List<PracticeLog>>? _logsByPiece;
    private Dictionary<string, Piece>? _pieceById;
    // each piece's stats, worked out when first asked for (often only for the cards on screen) and kept until the next change,
    // so Home, Library, Stats and the Metronome share them, and opening a screen again costs nothing
    // (keyed by id, and only for the same Piece object: stats depend on its goal and unit, and an unsaved copy may differ)
    private readonly Dictionary<string, (Piece of, PieceStats stats)> _stats = new();
    private readonly Dictionary<string, (Piece of, PracticeLog? log)> _lastRated = new();
    private readonly Dictionary<string, (Piece of, List<SectionStats> parts)> _parts = new();

    private void Invalidate() { _logsByPiece = null; _pieceById = null; _stats.Clear(); _lastRated.Clear(); _parts.Clear(); }
    private void Notify() { Invalidate(); Changed?.Invoke(); }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        _db = await js.InvokeAsync<IJSObjectReference>("import", "./js/db.js");
#if DEMO_MODE
        await _db.InvokeVoidAsync("useMemory");   // loads js/memorydb.js
        await SeedDemoAsync(DemoData.Create(DateTime.Today));
#endif
        Pieces = await GetAll<Piece>("pieces");
        Logs = await GetAll<PracticeLog>("logs");
        Presets = await GetAll<MetronomePreset>("presets");
        Plans = await GetAll<PracticePlan>("plans");
        var settings = await GetAll<AppSettings>("settings");
        Settings = settings.FirstOrDefault() ?? new AppSettings();
        Settings.Colours?.Clean();
        Pieces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Loaded = true;
        Notify();
    }

#if DEMO_MODE
    // written to the (in-memory) stores and then loaded as any other start would load them
    private async Task SeedDemoAsync(DemoData.Set d)
    {
        await PutMany("pieces", d.Pieces);
        await PutMany("logs", d.Logs);
        await PutMany("presets", d.Presets);
        await PutMany("plans", d.Plans);
        await Put("settings", d.Settings);
    }
#endif

    private async Task<List<T>> GetAll<T>(string store)
    {
        var json = await _db!.InvokeAsync<byte[]>("getAll", store); // UTF-8 JSON (see db.js)
        return JsonSerializer.Deserialize<List<T>>(json, Json) ?? new();
    }

    private Task Put<T>(string store, T value) => _db!.InvokeVoidAsync("put", store, JsonSerializer.Serialize(value, Json)).AsTask();
    // UTF-8 bytes: a restore writes thousands of rows, and a string would be JSON-encoded again by interop
    private Task PutMany<T>(string store, IEnumerable<T> values) => _db!.InvokeVoidAsync("putMany", store, JsonSerializer.SerializeToUtf8Bytes(values, Json)).AsTask();
    private Task Remove(string store, string id) => _db!.InvokeVoidAsync("remove", store, id).AsTask();

    // ---------- Pieces ----------
    public Piece? GetPiece(string? id) =>
        id is null ? null : (_pieceById ??= Pieces.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);

    public Piece? FindPieceByName(string name) =>
        Pieces.FirstOrDefault(i => string.Equals(i.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task SavePieceAsync(Piece piece)
    {
        var idx = Pieces.FindIndex(i => i.Id == piece.Id);
        if (idx >= 0) Pieces[idx] = piece; else Pieces.Add(piece);
        Invalidate();
        Pieces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        // keep log name snapshots in sync after a rename
        var renamed = Logs.Where(l => l.PieceId == piece.Id && l.PieceName != piece.Name).ToList();
        foreach (var l in renamed) l.PieceName = piece.Name;
        await Put("pieces", piece);
        if (renamed.Count > 0) await PutMany("logs", renamed);
        Notify();
        if (idx < 0) PieceAdded?.Invoke(piece);
    }

    public async Task DeletePieceAsync(Piece piece, bool deleteLogs)
    {
        if (piece.ScoreFileId != null && !Pieces.Any(i => i.Id != piece.Id && i.ScoreFileId == piece.ScoreFileId))
            await _db!.InvokeVoidAsync("removeFile", piece.ScoreFileId);
        Pieces.RemoveAll(i => i.Id == piece.Id);
        Invalidate();
        await Remove("pieces", piece.Id);
        var logs = Logs.Where(l => l.PieceId == piece.Id).ToList();
        foreach (var l in logs)
        {
            if (deleteLogs) Logs.Remove(l);
            else l.PieceId = null;
        }
        Invalidate();
        if (deleteLogs) foreach (var l in logs) await Remove("logs", l.Id);
        if (!deleteLogs && logs.Count > 0) await PutMany("logs", logs);
        Notify();
    }

    // ---------- Logs ----------
    private static readonly List<PracticeLog> NoLogs = new();

    /// <summary>A piece's logs, oldest first. Pass these to <see cref="Stats"/> rather than <see cref="Logs"/>.</summary>
    public IReadOnlyList<PracticeLog> LogsFor(string? pieceId) =>
        pieceId is null ? NoLogs
        : (_logsByPiece ??= Logs.Where(l => l.PieceId != null).OrderBy(l => l.Date).GroupBy(l => l.PieceId!).ToDictionary(g => g.Key, g => g.ToList()))
            .GetValueOrDefault(pieceId, NoLogs);

    /// <summary>The piece's stats over all its logs (<see cref="Stats.ForPiece"/>), kept until the data changes.</summary>
    public PieceStats StatsFor(Piece piece) =>
        _stats.TryGetValue(piece.Id, out var c) && c.of == piece ? c.stats : (_stats[piece.Id] = (piece, Stats.ForPiece(piece, LogsFor(piece.Id)))).stats;

    /// <summary>The piece's latest log with a feel rating (<see cref="Stats.LastRated"/>), kept until the data changes.</summary>
    public PracticeLog? LastRatedFor(Piece piece) =>
        _lastRated.TryGetValue(piece.Id, out var c) && c.of == piece ? c.log : (_lastRated[piece.Id] = (piece, Stats.LastRated(piece, LogsFor(piece.Id)))).log;

    /// <summary>Per-section stats (<see cref="Stats.ForSections"/>), kept until the data changes.</summary>
    public List<SectionStats> PartsFor(Piece piece) =>
        _parts.TryGetValue(piece.Id, out var c) && c.of == piece ? c.parts : (_parts[piece.Id] = (piece, Stats.ForSections(piece, LogsFor(piece.Id)))).parts;

    public async Task SaveLogAsync(PracticeLog log)
    {
        var idx = Logs.FindIndex(l => l.Id == log.Id);
        if (idx >= 0) { log.Logged ??= Logs[idx].Logged; Logs[idx] = log; }
        else { log.Logged ??= DateTime.Now; Logs.Add(log); }
        Invalidate();
        await Put("logs", log);
        Notify();
        if (idx < 0) LogAdded?.Invoke(log);
    }

    public async Task DeleteLogAsync(PracticeLog log)
    {
        Logs.RemoveAll(l => l.Id == log.Id);
        Invalidate();
        await Remove("logs", log.Id);
        Notify();
    }

    // ---------- Presets ----------
    public async Task SavePresetAsync(MetronomePreset p)
    {
        var idx = Presets.FindIndex(x => x.Id == p.Id);
        if (idx >= 0) Presets[idx] = p; else Presets.Add(p);
        await Put("presets", p);
        Notify();
    }

    public async Task DeletePresetAsync(MetronomePreset p)
    {
        Presets.RemoveAll(x => x.Id == p.Id);
        await Remove("presets", p.Id);
        Notify();
    }

    /// <summary>Pieces for pickers: most recently logged first, never-logged last (see DESIGN.md).</summary>
    public List<Piece> PiecesByRecent(bool includeArchived = false, string? keepId = null)
    {
        return Pieces.Where(i => includeArchived || !i.Archived || i.Id == keepId)
            .OrderByDescending(i => LogsFor(i.Id) is { Count: > 0 } l ? l[^1].Date : DateTime.MinValue).ThenBy(i => i.Name).ToList();
    }

    // ---------- Plans ----------
    public PracticePlan? GetPlan(string? id) => Plans.FirstOrDefault(p => p.Id == id);

    public async Task SavePlanAsync(PracticePlan p)
    {
        var idx = Plans.FindIndex(x => x.Id == p.Id);
        if (idx >= 0) Plans[idx] = p; else Plans.Add(p);
        await Put("plans", p);
        Notify();
    }

    public async Task DeletePlanAsync(PracticePlan p)
    {
        Plans.RemoveAll(x => x.Id == p.Id);
        await Remove("plans", p.Id);
        Notify();
    }

    // ---------- Session (transient state that survives a refresh; deliberately not backed up) ----------
    public async Task<string?> GetSessionAsync(string id) => await _db!.InvokeAsync<string?>("get", "session", id);
    public Task PutSessionAsync<T>(T value) => Put("session", value);
    public Task RemoveSessionAsync(string id) => Remove("session", id);

    // ---------- Settings ----------
    /// <summary>Replaces all settings (backup restore). Reload the app afterwards.</summary>
    public async Task ReplaceSettingsAsync(AppSettings settings)
    {
        CancelSettingsSave();
        settings.Id = "app";
        settings.Colours?.Clean();   // a backup's colours go into a style sheet: only real colours
        Settings = settings;
        await Put("settings", Settings);
        Changed?.Invoke(); // settings don't feed the lookups
    }

    public async Task SaveSettingsAsync()
    {
        CancelSettingsSave();
        await Put("settings", Settings);
        Changed?.Invoke(); // settings don't feed the lookups
    }

    private CancellationTokenSource? _settingsSave;
    private Task _pendingSettingsSave = Task.CompletedTask;
    public const int SettingsSaveDelayMs = 400;

    /// <summary>
    /// Saves settings once they stop changing for <see cref="SettingsSaveDelayMs"/>: for controls that change many times a second
    /// (tempo +/−, sliders). The in-memory settings are already current, so only the write (and <see cref="Changed"/>) waits.
    /// </summary>
    public void SaveSettingsSoon()
    {
        CancelSettingsSave();
        var cts = _settingsSave = new CancellationTokenSource();
        _pendingSettingsSave = SaveLaterAsync(cts.Token);
    }

    private async Task SaveLaterAsync(CancellationToken ct)
    {
        try { await Task.Delay(SettingsSaveDelayMs, ct); }
        catch (OperationCanceledException) { return; }
        await SaveSettingsAsync();
    }

    /// <summary>Writes a pending <see cref="SaveSettingsSoon"/> now (also lets tests wait for it).</summary>
    public async Task FlushSettingsAsync()
    {
        if (_settingsSave is null) { await _pendingSettingsSave; return; }
        await SaveSettingsAsync();
    }

    private void CancelSettingsSave() { _settingsSave?.Cancel(); _settingsSave = null; }

    // ---------- Bulk ----------
    /// <summary>Bulk upsert used by backup restore. New persisted entity? Add it here and in Backup.cs (see CLAUDE.md).</summary>
    public async Task ImportAsync(IEnumerable<Piece> pieces, IEnumerable<PracticeLog> logs,
        IEnumerable<MetronomePreset>? presets = null, IEnumerable<PracticePlan>? plans = null)
    {
        var il = pieces.ToList(); var ll = logs.ToList(); var pl = presets?.ToList() ?? new(); var pp = plans?.ToList() ?? new();
        Replace(Pieces, il, x => x.Id); Replace(Logs, ll, x => x.Id); Replace(Presets, pl, x => x.Id); Replace(Plans, pp, x => x.Id);
        Invalidate();
        Pieces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (il.Count > 0) await PutMany("pieces", il);
        if (ll.Count > 0) await PutMany("logs", ll);
        if (pl.Count > 0) await PutMany("presets", pl);
        if (pp.Count > 0) await PutMany("plans", pp);
        Notify();
    }

    /// <summary>Drops the rows <paramref name="incoming"/> replaces, then adds it: one pass, not one per row (a big restore took minutes).</summary>
    private static void Replace<T>(List<T> list, List<T> incoming, Func<T, string> id)
    {
        if (incoming.Count == 0) return;
        var ids = incoming.Select(id).ToHashSet();
        list.RemoveAll(x => ids.Contains(id(x)));
        list.AddRange(incoming);
    }

    public async Task ClearAllAsync()
    {
        // "devices" (js/link.js: this device's identity and its pairings) goes too; Devices.ResetAsync starts the link afresh
        foreach (var s in new[] { "pieces", "logs", "presets", "files", "plans", "session", "devices" }) await _db!.InvokeVoidAsync("clear", s);
        Pieces.Clear(); Logs.Clear(); Presets.Clear(); Plans.Clear();
        Invalidate();
        Notify();
    }

    public ValueTask DownloadAsync(string filename, string text, string mime) =>
        _db!.InvokeVoidAsync("download", filename, text, mime);

    /// <summary>Saves UTF-8 bytes as a file. Use it for big files: bytes cross to JS as they are, where a string is JSON-encoded again.</summary>
    public ValueTask DownloadAsync(string filename, byte[] utf8, string mime) =>
        _db!.InvokeVoidAsync("download", filename, utf8, mime);
}
