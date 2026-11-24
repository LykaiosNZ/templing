using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Templing.Services;

namespace Templing.Components.Charts;

/// <summary>
/// Base for charts drawn by Apache ECharts (wwwroot/js/charts.js). A component builds a plain spec in
/// <see cref="BuildSpec"/> (data plus pre-formatted text), renders a div bound to <see cref="El"/>, and the spec is sent
/// to the JS function <see cref="Kind"/> whenever it changes. The theme is part of the spec, so a theme switch redraws.
/// </summary>
public abstract class ChartBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected DataStore Store { get; set; } = default!;

    protected ElementReference El;
    IJSObjectReference? _mod;
    object? _spec;
    string _drawn = "";

    /// <summary>Name of the drawing function exported by charts.js.</summary>
    protected abstract string Kind { get; }

    /// <summary>The spec for charts.js, or null when there's nothing to draw (the component shows an empty state).</summary>
    protected abstract object? BuildSpec();

    protected override void OnParametersSet()
    {
        _spec = BuildSpec();
        if (_spec is null) _drawn = "";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_spec is null || El.Context is null) return;
        var json = JsonSerializer.Serialize(new { theme = Store.Settings.DarkMode, spec = _spec });
        if (json == _drawn) return;
        _drawn = json;
        // a chart that can't draw (charts.js says so in its box when the library won't load) must never stop the app:
        // an exception here reached Blazor's error bar. Forget what was drawn, so the next render tries again.
        try
        {
            _mod ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/charts.js");
            await _mod.InvokeVoidAsync(Kind, El, _spec);
        }
        catch (JSException e)
        {
            _drawn = "";
            Console.Error.WriteLine($"Chart ({Kind}) couldn't be drawn: {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mod is null) return;
        try
        {
            if (El.Context is not null) await _mod.InvokeVoidAsync("dispose", El);
            await _mod.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }

    protected static string IsoDate(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Axis ticks from <paramref name="min"/> to the first step at or above <paramref name="hi"/>, with labels.</summary>
    protected static (double min, double max, double step, Dictionary<string, string> labels) Ticks(double min, double hi, double step, Func<double, string> label)
    {
        var max = Math.Max(min + step, Math.Ceiling(hi / step) * step);
        var labels = new Dictionary<string, string>();
        for (var v = min; v <= max + 1e-9; v += step)
            labels[v.ToString(CultureInfo.InvariantCulture)] = label(v);
        return (min, max, step, labels);
    }

    /// <summary>1, 2 or 5 × 10ⁿ, at least <paramref name="raw"/>.</summary>
    protected static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    /// <summary>Round duration steps (5 min … 10 hr) giving about four ticks.</summary>
    protected static int MinuteStep(double max)
    {
        foreach (var step in new[] { 5, 10, 15, 30, 60, 120, 180, 240, 300, 600 })
            if (max / step <= 5) return step;
        return 600;
    }
}
