using Microsoft.JSInterop;

namespace Songler.Services;

public record ShortcutInfo(string Keys, string Description, string Group);

/// <summary>
/// Global keyboard shortcut registry. Components register handlers (scoped by an owner object)
/// and remove them on dispose. The most recently registered handler for a combo wins.
/// </summary>
public class Shortcuts(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _mod;
    private DotNetObjectReference<Shortcuts>? _ref;
    private readonly List<(object owner, string combo, Func<Task> handler)> _handlers = new();

    /// <summary>Documented shortcuts for the help dialog.</summary>
    public static readonly ShortcutInfo[] All =
    [
        new("Space", "Start / stop metronome (or timer)", "Metronome"),
        new("R", "Restart section (with count-in)", "Metronome"),
        new("↑ / ↓", "Tempo +1 / −1", "Metronome"),
        new("Shift + ↑ / ↓", "Tempo +5 / −5", "Metronome"),
        new("← / →", "Previous / next section", "Metronome"),
        new("T", "Tap tempo", "Metronome"),
        new("C", "Toggle count-in", "Metronome"),
        new("X", "Toggle speed trainer (ramp)", "Metronome"),
        new("I", "Toggle interval trainer", "Metronome"),
        new("L", "Stop and log this session", "Metronome"),
        new("Esc", "Stop", "Metronome"),
        new("N", "Log practice manually", "General"),
        new("/", "Search library", "General"),
        new("?", "Show keyboard shortcuts", "General"),
        new("G then D", "Go to dashboard", "Navigation"),
        new("G then L", "Go to library", "Navigation"),
        new("G then M", "Go to metronome", "Navigation"),
        new("G then T", "Go to practice timer", "Navigation"),
        new("G then H", "Go to history", "Navigation"),
        new("G then S", "Go to stats", "Navigation"),
        new("G then ,", "Go to settings", "Navigation"),
    ];

    public async Task InitAsync()
    {
        if (_mod != null) return;
        _mod = await js.InvokeAsync<IJSObjectReference>("import", "./js/keys.js");
        _ref = DotNetObjectReference.Create(this);
        await _mod.InvokeVoidAsync("init", _ref);
        await Sync();
    }

    public IDisposable Register(object owner, string combo, Func<Task> handler) => Register(owner, new[] { combo }, handler);

    public IDisposable Register(object owner, IEnumerable<string> combos, Func<Task> handler)
    {
        foreach (var c in combos) _handlers.Add((owner, c, handler));
        _ = Sync();
        return new Unreg(this, owner);
    }

    public IDisposable Register(object owner, string combo, Action handler) =>
        Register(owner, combo, () => { handler(); return Task.CompletedTask; });

    public void Unregister(object owner)
    {
        _handlers.RemoveAll(h => ReferenceEquals(h.owner, owner));
        _ = Sync();
    }

    private async Task Sync()
    {
        if (_mod == null) return;
        try { await _mod.InvokeVoidAsync("setBindings", (object)_handlers.Select(h => h.combo).Distinct().ToArray()); }
        catch (JSDisconnectedException) { }
    }

    [JSInvokable]
    public async Task OnShortcut(string combo)
    {
        var h = _handlers.LastOrDefault(x => x.combo == combo);
        if (h.handler != null) await h.handler();
    }

    public ValueTask FocusAsync(string selector) => _mod?.InvokeVoidAsync("focus", selector) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_mod != null) await _mod.DisposeAsync();
        _ref?.Dispose();
    }

    private sealed class Unreg(Shortcuts s, object owner) : IDisposable
    {
        public void Dispose() => s.Unregister(owner);
    }
}
