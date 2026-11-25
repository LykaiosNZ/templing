using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace Templing.Services;

/// <summary>A device paired with this one (wwwroot/js/link.js keeps the list and their keys in the 'devices' store).</summary>
public sealed record PairedDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("paired")] DateTime? Paired,
    [property: JsonPropertyName("connected")] bool Connected);

/// <summary>
/// Pairing has connected and proved itself: both screens show <paramref name="Code"/>. The device showing the QR code
/// (<paramref name="Confirming"/>) asks whether to pair with <paramref name="Name"/>; <paramref name="Again"/> when it's
/// already paired with that device (this pairing replaces the old one).
/// </summary>
public sealed record PairCheck(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("again")] bool Again,
    [property: JsonPropertyName("confirming")] bool Confirming);

/// <summary>A backup on its way to or from a paired device.</summary>
public sealed record Transfer(string DeviceId, bool Incoming, long Done, long Size);

/// <summary>
/// Paired devices and the link to them (js/link.js, through the relay at <see cref="AppInfo.RelayUrl"/>): pairing with a
/// QR code, going online while Settings is open, and sending or asking for a backup. The link only ever moves a backup
/// (<see cref="Backup.ExportGzip"/>); what to do with one that arrives is the page's (the usual restore dialog).
/// Pairings are deliberately not backed up: a key belongs to the two devices that made it.
/// </summary>
public sealed class Devices(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _link;
    private DotNetObjectReference<Devices>? _ref;
    private Task? _starting;

    public string SelfId { get; private set; } = "";
    public string SelfName { get; private set; } = "";
    public IReadOnlyList<PairedDevice> List { get; private set; } = [];
    public Transfer? Transfer { get; private set; }

    /// <summary>The secret from a pairing link (the /pair page), until Settings picks it up and joins.</summary>
    public string? PendingJoin { get; set; }

    public event Action? Changed;
    /// <summary>Paired with a device (either side). For the page's message and <see cref="Analytics"/>.</summary>
    public event Action<PairedDevice>? Paired;
    /// <summary>Pairing didn't work: "unreachable" (no relay), "timeout", "closed", "proof", "left", "declined" (cancelled on the other device)…</summary>
    public event Action<string>? PairFailed;
    /// <summary>Pairing has joined its relay room: the code can be used (or the link joined).</summary>
    public event Action? PairReady;
    /// <summary>Pairing connected: show the matching code (and, on the device showing the QR code, ask to confirm).</summary>
    public event Action<PairCheck>? PairChecking;
    /// <summary>A connected device wants to send its backup (size in bytes): <see cref="AcceptAsync"/> or <see cref="DeclineAsync"/>.</summary>
    public event Action<PairedDevice, long>? Offered;
    /// <summary>A connected device asks for this device's backup.</summary>
    public event Action<PairedDevice>? Requested;
    /// <summary>A connected device said no to a request or a backup.</summary>
    public event Action<PairedDevice>? Declined;
    /// <summary>The other device forgot this one, so it's gone here too.</summary>
    public event Action<string>? Forgotten;
    /// <summary>A backup arrived from a connected device.</summary>
    public event Func<PairedDevice, byte[], Task>? BackupReceived;
    /// <summary>A backup sent to a device arrived there. For <see cref="Analytics"/>.</summary>
    public event Action? Sent;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task EnsureStartedAsync() => _starting ??= StartAsync();

    private async Task StartAsync()
    {
        _link = await js.InvokeAsync<IJSObjectReference>("import", "./js/link.js");
        _ref = DotNetObjectReference.Create(this);
        ReadSelf(await _link.InvokeAsync<string>("init", AppInfo.RelayUrl, _ref));
        await LoadListAsync(_link);   // not RefreshAsync: that waits for this start to finish
    }

    private void ReadSelf(string json)
    {
        var self = JsonSerializer.Deserialize<JsonElement>(json);
        SelfId = self.GetProperty("id").GetString() ?? "";
        SelfName = self.GetProperty("name").GetString() ?? "";
    }

    private async Task<IJSObjectReference> Link()
    {
        await EnsureStartedAsync();
        return _link!;
    }

    public PairedDevice? Get(string id) => List.FirstOrDefault(d => d.Id == id);

    public async Task RefreshAsync() => await LoadListAsync(await Link());

    private async Task LoadListAsync(IJSObjectReference link)
    {
        var json = await link.InvokeAsync<string>("list");
        List = (JsonSerializer.Deserialize<List<PairedDevice>>(json, Json) ?? []).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Changed?.Invoke();
    }

    public async Task GoOnlineAsync() => await (await Link()).InvokeVoidAsync("goOnline");

    public async Task GoOfflineAsync()
    {
        if (_link == null) return;
        await _link.InvokeVoidAsync("goOffline");
        await RefreshAsync();
    }

    /// <summary>Starts pairing as the device showing the code: the secret for the link.</summary>
    public async Task<string> PairStartAsync() => await (await Link()).InvokeAsync<string>("pairStart");

    /// <summary>Joins the pairing a link's secret belongs to. False if the secret isn't one.</summary>
    public async Task<bool> PairJoinAsync(string secret) => await (await Link()).InvokeAsync<bool>("pairJoin", secret);

    /// <summary>The device showing the code: the codes match (pair) or not (cancel).</summary>
    public async Task PairConfirmAsync(bool ok) => await (await Link()).InvokeVoidAsync("pairConfirm", ok);

    public async Task PairCancelAsync()
    {
        if (_link != null) await _link.InvokeVoidAsync("pairCancel");
    }

    public async Task RenameSelfAsync(string name)
    {
        SelfName = await (await Link()).InvokeAsync<string>("renameSelf", name);
        Changed?.Invoke();
    }

    /// <summary>Sends a backup to a connected device: "received", "declined", "closed", "busy" (one is under way) or "offline".</summary>
    public async Task<string> SendAsync(string id, byte[] backup)
    {
        if (Transfer != null) return "busy";   // and the one under way keeps its progress
        try
        {
            Transfer = new Transfer(id, false, 0, backup.Length);
            Changed?.Invoke();
            var result = await (await Link()).InvokeAsync<string>("send", id, backup);
            if (result == "received") Sent?.Invoke();
            return result;
        }
        finally
        {
            Transfer = null;
            Changed?.Invoke();
        }
    }

    public async Task<bool> RequestAsync(string id) => await (await Link()).InvokeAsync<bool>("request", id);

    /// <summary>Takes the backup a device offered.</summary>
    public async Task AcceptAsync(string id) => await (await Link()).InvokeVoidAsync("accept", id);

    /// <summary>Says no to a device's request, or to the backup it offered.</summary>
    public async Task DeclineAsync(string id) => await (await Link()).InvokeVoidAsync("decline", id);

    public async Task ForgetAsync(string id)
    {
        await (await Link()).InvokeVoidAsync("forget", id);
        await RefreshAsync();
    }

    /// <summary>After "Delete all data": the pairings went with it, and this device starts again with a new id.</summary>
    public async Task ResetAsync()
    {
        if (_link == null) return;
        ReadSelf(await _link.InvokeAsync<string>("reset"));
        await RefreshAsync();
    }

    [JSInvokable]
    public async Task OnLinkEvent(string type, string json)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(json);
        PairedDevice? Device() => data.TryGetProperty("name", out _) ? data.Deserialize<PairedDevice>(Json) : Get(data.GetProperty("id").GetString() ?? "");
        switch (type)
        {
            case "progress":
                Transfer = new Transfer(data.GetProperty("id").GetString() ?? "", data.GetProperty("incoming").GetBoolean(),
                    data.GetProperty("done").GetInt64(), data.GetProperty("size").GetInt64());
                Changed?.Invoke();
                return;
            case "paired":
                await RefreshAsync();
                if (Device() is { } paired) Paired?.Invoke(paired);
                return;
            case "transfer-ended":
                // a transfer that stopped part way (or a send that's done): no progress left showing
                if (Transfer?.DeviceId == data.GetProperty("id").GetString()) { Transfer = null; Changed?.Invoke(); }
                return;
            case "disconnected":
                if (Transfer?.DeviceId == data.GetProperty("id").GetString()) Transfer = null;
                await RefreshAsync();
                return;
            case "pair-ready":
                PairReady?.Invoke();
                return;
            case "pair-check":
                if (data.Deserialize<PairCheck>(Json) is { } check) PairChecking?.Invoke(check);
                return;
            case "offered":
                if (Device() is { } offering) Offered?.Invoke(offering, data.GetProperty("size").GetInt64());
                return;
            case "pair-failed":
                PairFailed?.Invoke(data.GetProperty("reason").GetString() ?? "");
                return;
            case "requested":
                if (Device() is { } asking) Requested?.Invoke(asking);
                return;
            case "declined":
                if (Device() is { } declined) Declined?.Invoke(declined);
                return;
            case "forgotten":
                await RefreshAsync();
                Forgotten?.Invoke(data.GetProperty("name").GetString() ?? "");
                return;
            default:   // connected, renamed
                await RefreshAsync();
                return;
        }
    }

    [JSInvokable]
    public async Task OnBackupReceived(string id, byte[] bytes)
    {
        Transfer = null;
        Changed?.Invoke();
        if (Get(id) is { } from && BackupReceived != null) await BackupReceived(from, bytes);
    }

    public async ValueTask DisposeAsync()
    {
        if (_link != null)
        {
            try { await _link.InvokeVoidAsync("goOffline"); await _link.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
        _ref?.Dispose();
    }
}
