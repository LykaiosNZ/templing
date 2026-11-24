using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Templing.Models;

namespace Templing.Services;

/// <summary>
/// A few Umami events (index.html loads the tracker, only on the deployed site): practice logged, piece added (and how),
/// plan started / finished, and unhandled errors (the exception's type only). Nothing from the user's library is sent:
/// no names, notes, tempos or ids. The demo build sends none, so its sample data doesn't count as practice.
/// <see cref="AppSettings.Analytics"/> (Settings > Data) turns all of it off, page views too: the boot script in index.html
/// only loads the tracker if the saved setting allows, and <see cref="Apply"/> keeps it in step after that.
/// </summary>
public class Analytics
{
    private readonly IJSRuntime _js;
    private readonly DataStore _store;
    private bool? _applied;

    public Analytics(IJSRuntime js, DataStore store, PlanRunner runner)
    {
        _js = js;
        _store = store;
        store.Changed += () => { if (store.Loaded) Apply(); };
        store.LogAdded += _ => Track("practice-logged");
        store.PieceAdded += p => Track("piece-added", new { source = Source(p) });
        runner.RunStarted += () => Track("plan-started");
        runner.RunFinished += () => Track("plan-finished");
    }

    /// <summary>How a new piece came in: typed in, or from a score file (by its extension).</summary>
    public static string Source(Piece p)
    {
        if (p.ScoreFileId == null) return "manual";
        var ext = Path.GetExtension(p.ScoreFileName ?? "").TrimStart('.').ToLowerInvariant();
        return ext.StartsWith("gp") ? "guitar-pro"
            : ext is "xml" or "musicxml" or "mxl" ? "musicxml"
            : "score";
    }

    /// <summary>
    /// Tells index.html whether analytics is on (loading the tracker if it wasn't): once the store has loaded (MainLayout),
    /// then whenever the setting changes (switched in Settings, or reset by "Delete all data").
    /// </summary>
    public void Apply()
    {
        var on = _store.Settings.Analytics;
        if (_applied == on) return;
        _applied = on;
        try { _ = _js.InvokeVoidAsync("templingAnalytics", on).AsTask().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        catch { }
    }

    public void Track(string name, object? data = null) => Send(_js, name, data);

    // fire and forget: analytics must never get in the way, and the tracker may not have loaded (or be blocked)
    internal static void Send(IJSRuntime js, string name, object? data)
    {
#if DEMO_MODE
        return;
#else
        try { _ = js.InvokeVoidAsync("templingTrack", name, data).AsTask().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        catch { }
#endif
    }
}

/// <summary>
/// Reports unhandled exceptions (the ones Blazor logs as Critical and shows the "An unhandled error has occurred" bar for)
/// as an Umami "error" event with the exception's type, once per type per page load. No message or stack: they can hold user data.
/// </summary>
public sealed class ErrorReporter(IJSRuntime js) : ILoggerProvider, ILogger
{
    private readonly HashSet<string> _sent = new();

    public ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Critical;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel != LogLevel.Critical || exception == null) return;
        var type = (exception is AggregateException { InnerException: { } inner } ? inner : exception).GetType().Name;
        lock (_sent) if (!_sent.Add(type)) return;
        Analytics.Send(js, "error", new { type });
    }
}
