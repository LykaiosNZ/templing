using System.Text.Json;
using Microsoft.JSInterop;
using Songler.Models;

namespace Songler.Services;

/// <summary>In-memory cache over IndexedDB. Load once at startup; writes go through to the DB.</summary>
public class DataStore(IJSRuntime js)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    private IJSObjectReference? _db;
    private Task? _loading;

    public List<PracticeItem> Items { get; private set; } = new();
    public List<PracticeLog> Logs { get; private set; } = new();
    public List<MetronomePreset> Presets { get; private set; } = new();
    public List<PracticePlan> Plans { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();
    public bool Loaded { get; private set; }

    /// <summary>Raised after any write.</summary>
    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        _db = await js.InvokeAsync<IJSObjectReference>("import", "./js/db.js");
        Items = await GetAll<PracticeItem>("items");
        Logs = await GetAll<PracticeLog>("logs");
        Presets = await GetAll<MetronomePreset>("presets");
        Plans = await GetAll<PracticePlan>("plans");
        var settings = await GetAll<AppSettings>("settings");
        Settings = settings.FirstOrDefault() ?? new AppSettings();
        Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Loaded = true;
        Changed?.Invoke();
    }

    private async Task<List<T>> GetAll<T>(string store)
    {
        var json = await _db!.InvokeAsync<string>("getAll", store);
        return JsonSerializer.Deserialize<List<T>>(json, Json) ?? new();
    }

    private Task Put<T>(string store, T value) => _db!.InvokeVoidAsync("put", store, JsonSerializer.Serialize(value, Json)).AsTask();
    private Task PutMany<T>(string store, IEnumerable<T> values) => _db!.InvokeVoidAsync("putMany", store, JsonSerializer.Serialize(values, Json)).AsTask();
    private Task Remove(string store, string id) => _db!.InvokeVoidAsync("remove", store, id).AsTask();

    // ---------- Items ----------
    public PracticeItem? GetItem(string? id) => id is null ? null : Items.FirstOrDefault(i => i.Id == id);

    public PracticeItem? FindItemByName(string name) =>
        Items.FirstOrDefault(i => string.Equals(i.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task SaveItemAsync(PracticeItem item)
    {
        var idx = Items.FindIndex(i => i.Id == item.Id);
        if (idx >= 0) Items[idx] = item; else Items.Add(item);
        Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        // keep log name snapshots in sync after a rename
        var renamed = Logs.Where(l => l.ItemId == item.Id && l.ItemName != item.Name).ToList();
        foreach (var l in renamed) l.ItemName = item.Name;
        await Put("items", item);
        if (renamed.Count > 0) await PutMany("logs", renamed);
        Changed?.Invoke();
    }

    public async Task DeleteItemAsync(PracticeItem item, bool deleteLogs)
    {
        if (item.ScoreFileId != null && !Items.Any(i => i.Id != item.Id && i.ScoreFileId == item.ScoreFileId))
            await _db!.InvokeVoidAsync("removeFile", item.ScoreFileId);
        Items.RemoveAll(i => i.Id == item.Id);
        await Remove("items", item.Id);
        var logs = Logs.Where(l => l.ItemId == item.Id).ToList();
        foreach (var l in logs)
        {
            if (deleteLogs) { Logs.Remove(l); await Remove("logs", l.Id); }
            else l.ItemId = null;
        }
        if (!deleteLogs && logs.Count > 0) await PutMany("logs", logs);
        Changed?.Invoke();
    }

    // ---------- Logs ----------
    public IEnumerable<PracticeLog> LogsFor(string itemId) => Logs.Where(l => l.ItemId == itemId);

    public async Task SaveLogAsync(PracticeLog log)
    {
        var idx = Logs.FindIndex(l => l.Id == log.Id);
        if (idx >= 0) { log.Logged ??= Logs[idx].Logged; Logs[idx] = log; }
        else { log.Logged ??= DateTime.Now; Logs.Add(log); }
        await Put("logs", log);
        Changed?.Invoke();
    }

    public async Task DeleteLogAsync(PracticeLog log)
    {
        Logs.RemoveAll(l => l.Id == log.Id);
        await Remove("logs", log.Id);
        Changed?.Invoke();
    }

    // ---------- Presets ----------
    public async Task SavePresetAsync(MetronomePreset p)
    {
        var idx = Presets.FindIndex(x => x.Id == p.Id);
        if (idx >= 0) Presets[idx] = p; else Presets.Add(p);
        await Put("presets", p);
        Changed?.Invoke();
    }

    public async Task DeletePresetAsync(MetronomePreset p)
    {
        Presets.RemoveAll(x => x.Id == p.Id);
        await Remove("presets", p.Id);
        Changed?.Invoke();
    }

    /// <summary>Items for pickers: most recently logged first, never-logged last (see DESIGN.md).</summary>
    public List<PracticeItem> ItemsByRecent(bool includeArchived = false, string? keepId = null)
    {
        var last = Logs.Where(l => l.ItemId != null).GroupBy(l => l.ItemId!).ToDictionary(g => g.Key, g => g.Max(l => l.Date));
        return Items.Where(i => includeArchived || !i.Archived || i.Id == keepId)
            .OrderByDescending(i => last.TryGetValue(i.Id, out var d) ? d : DateTime.MinValue).ThenBy(i => i.Name).ToList();
    }

    // ---------- Plans ----------
    public PracticePlan? GetPlan(string? id) => Plans.FirstOrDefault(p => p.Id == id);

    public async Task SavePlanAsync(PracticePlan p)
    {
        var idx = Plans.FindIndex(x => x.Id == p.Id);
        if (idx >= 0) Plans[idx] = p; else Plans.Add(p);
        await Put("plans", p);
        Changed?.Invoke();
    }

    public async Task DeletePlanAsync(PracticePlan p)
    {
        Plans.RemoveAll(x => x.Id == p.Id);
        await Remove("plans", p.Id);
        Changed?.Invoke();
    }

    // ---------- Session (transient state that survives a refresh; deliberately not backed up) ----------
    public async Task<string?> GetSessionAsync(string id) => await _db!.InvokeAsync<string?>("get", "session", id);
    public Task PutSessionAsync<T>(T value) => Put("session", value);
    public Task RemoveSessionAsync(string id) => Remove("session", id);

    // ---------- Settings ----------
    /// <summary>Replaces all settings (backup restore). Reload the app afterwards.</summary>
    public async Task ReplaceSettingsAsync(AppSettings settings)
    {
        settings.Id = "app";
        Settings = settings;
        await Put("settings", Settings);
        Changed?.Invoke();
    }

    public async Task SaveSettingsAsync()
    {
        await Put("settings", Settings);
        Changed?.Invoke();
    }

    // ---------- Bulk ----------
    /// <summary>Bulk upsert used by backup restore. New persisted entity? Add it here and in Backup.cs (see CLAUDE.md).</summary>
    public async Task ImportAsync(IEnumerable<PracticeItem> items, IEnumerable<PracticeLog> logs,
        IEnumerable<MetronomePreset>? presets = null, IEnumerable<PracticePlan>? plans = null)
    {
        var il = items.ToList(); var ll = logs.ToList(); var pl = presets?.ToList() ?? new(); var pp = plans?.ToList() ?? new();
        foreach (var i in il) { Items.RemoveAll(x => x.Id == i.Id); Items.Add(i); }
        foreach (var l in ll) { Logs.RemoveAll(x => x.Id == l.Id); Logs.Add(l); }
        foreach (var p in pl) { Presets.RemoveAll(x => x.Id == p.Id); Presets.Add(p); }
        foreach (var p in pp) { Plans.RemoveAll(x => x.Id == p.Id); Plans.Add(p); }
        Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (il.Count > 0) await PutMany("items", il);
        if (ll.Count > 0) await PutMany("logs", ll);
        if (pl.Count > 0) await PutMany("presets", pl);
        if (pp.Count > 0) await PutMany("plans", pp);
        Changed?.Invoke();
    }

    public async Task ClearAllAsync()
    {
        foreach (var s in new[] { "items", "logs", "presets", "files", "plans", "session" }) await _db!.InvokeVoidAsync("clear", s);
        Items.Clear(); Logs.Clear(); Presets.Clear(); Plans.Clear();
        Changed?.Invoke();
    }

    public ValueTask DownloadAsync(string filename, string text, string mime) =>
        _db!.InvokeVoidAsync("downloadText", filename, text, mime);
}
