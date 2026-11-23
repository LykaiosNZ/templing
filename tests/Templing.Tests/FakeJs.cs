using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;

namespace Templing.Tests;

/// <summary>
/// A stand-in for the browser: <c>import("./js/db.js")</c> gives an in-memory IndexedDB (stores of JSON rows keyed by Id),
/// any other module records its calls and returns defaults. Enough to run <see cref="DataStore"/>, <see cref="Backup"/>
/// and the C# side of <see cref="MetronomeService"/> without a browser.
/// </summary>
public sealed class FakeJs : IJSRuntime
{
    public FakeDb Db { get; } = new();
    public Dictionary<string, FakeModule> Modules { get; } = new();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier != "import") throw new NotSupportedException($"window.{identifier}");
        var path = (string)args![0]!;
        IJSObjectReference module = path.EndsWith("/db.js") ? Db : Modules.TryGetValue(path, out var m) ? m : Modules[path] = new FakeModule();
        return ValueTask.FromResult((TValue)module);
    }

    /// <summary>A loaded test store: <paramref name="seed"/> written straight into the fake database first.</summary>
    public static async Task<(DataStore store, FakeJs js)> StoreAsync(Action<FakeDb>? seed = null)
    {
        var js = new FakeJs();
        seed?.Invoke(js.Db);
        var store = new DataStore(js);
        await store.EnsureLoadedAsync();
        return (store, js);
    }
}

/// <summary>A JS module that records what was called on it.</summary>
public class FakeModule : IJSObjectReference
{
    public List<(string fn, object?[] args)> Calls { get; } = new();

    public virtual ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public virtual ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Calls.Add((identifier, args ?? []));
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>db.js in memory: the same functions, the same JSON-string interface.</summary>
public sealed class FakeDb : FakeModule
{
    public Dictionary<string, Dictionary<string, JsonObject>> Stores { get; } = new();

    Dictionary<string, JsonObject> Store(string name) => Stores.TryGetValue(name, out var s) ? s : Stores[name] = new();

    /// <summary>Writes a row as DataStore would (for seeding a test).</summary>
    public void Seed<T>(string store, params T[] rows)
    {
        foreach (var r in rows) Put(store, JsonSerializer.Serialize(r, DataStore.Json));
    }

    /// <summary>A stored row, read back the way DataStore reads it.</summary>
    public T? Row<T>(string store, string id) =>
        Store(store).TryGetValue(id, out var row) ? row.Deserialize<T>(DataStore.Json) : default;

    public int Count(string store) => Store(store).Count;

    void Put(string store, string json)
    {
        var row = JsonNode.Parse(json)!.AsObject();
        Store(store)[(string)row["Id"]!] = row;
    }

    public override ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        base.InvokeAsync<TValue>(identifier, cancellationToken, args);
        var a = args ?? [];
        object? result = null;
        switch (identifier)
        {
            case "getAll": result = System.Text.Encoding.UTF8.GetBytes(new JsonArray(Store((string)a[0]!).Values.Select(r => (JsonNode)r.DeepClone()).ToArray()).ToJsonString()); break;
            case "get": result = Store((string)a[0]!).TryGetValue((string)a[1]!, out var row) ? row.ToJsonString() : null; break;
            case "put": Put((string)a[0]!, (string)a[1]!); break;
            case "putMany": foreach (var r in JsonNode.Parse((string)a[1]!)!.AsArray()) Put((string)a[0]!, r!.ToJsonString()); break;
            case "remove": Store((string)a[0]!).Remove((string)a[1]!); break;
            case "removeFile": Store("files").Remove((string)a[0]!); break;
            case "clear": Store((string)a[0]!).Clear(); break;
            case "downloadText": break;
            default: throw new NotSupportedException($"db.js has no {identifier}");
        }
        return ValueTask.FromResult(result is null ? default(TValue)! : (TValue)result);
    }
}
