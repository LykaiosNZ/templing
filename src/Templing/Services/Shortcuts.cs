using Microsoft.JSInterop;

namespace Templing.Services;

/// <summary>
/// Global keyboard shortcut registry. Components register handlers for <see cref="Hotkeys"/> entries (scoped by an
/// owner object) and remove them on dispose. The most recently registered handler for a combo wins.
/// </summary>
public class Shortcuts(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _mod;
    private DotNetObjectReference<Shortcuts>? _ref;
    private readonly List<(object owner, string combo, Func<Task> handler)> _handlers = new();

    public async Task InitAsync()
    {
        if (_mod != null) return;
        _mod = await js.InvokeAsync<IJSObjectReference>("import", "./js/keys.js");
        _ref = DotNetObjectReference.Create(this);
        await _mod.InvokeVoidAsync("init", _ref);
        await Sync();
    }

    /// <summary>Binds a handler to every combo of a hotkey from <see cref="Hotkeys"/> (the only way to bind a key,
    /// so the shortcuts dialog always lists it). Dispose the result, or call <see cref="Unregister"/>, to unbind.</summary>
    public IDisposable Register(object owner, Hotkey key, Func<Task> handler)
    {
        foreach (var c in key.Combos) _handlers.Add((owner, c, handler));
        _ = Sync();
        return new Unreg(this, owner);
    }

    public IDisposable Register(object owner, Hotkey key, Action handler) =>
        Register(owner, key, () => { handler(); return Task.CompletedTask; });

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
