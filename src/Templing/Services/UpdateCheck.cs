using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.JSInterop;

namespace Templing.Services;

/// <summary>
/// Notices when a newer build has been deployed while the app is open. Publishing writes <c>version.json</c>
/// (the informational version, e.g. "0.2.0+3f2a9c1…") beside the app (the WriteVersionFile target in Templing.csproj);
/// this fetches it past the browser cache at start and every few minutes, and raises <see cref="Changed"/> once it
/// differs from the running build. Builds without the file (the dev server) never report an update.
/// </summary>
public sealed class UpdateCheck(HttpClient http, IJSRuntime js, DataStore store) : IAsyncDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    /// <summary>The deployed version once it differs from this build's, else null.</summary>
    public string? Available { get; private set; }

    /// <summary>For display, "0.2.0 (3f2a9c1)".</summary>
    public string? AvailableDisplay => Available == null ? null : AppInfo.Format(Available);

    public event Action? Changed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = RunAsync(_stop.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // a first load can come from the browser's cache of index.html (Pages caches it for 10 min), so check soon
            await Task.Delay(FirstCheck, ct);
            using var timer = new PeriodicTimer(Every);
            do
            {
                if (await CheckAsync(ct)) return;   // found one: nothing more to learn until a reload
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "version.json");
            req.SetBrowserRequestCache(BrowserRequestCache.NoStore);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return false;
            var file = await res.Content.ReadFromJsonAsync<VersionFile>(ct);
            var deployed = file?.Version?.Trim();
            if (string.IsNullOrEmpty(deployed) || deployed == AppInfo.InformationalVersion) return false;
            Available = deployed;
            Changed?.Invoke();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }   // offline, or not JSON: try again later
    }

    /// <summary>Reloads the page. A reload revalidates index.html, where an ordinary navigation could reuse the cached copy.</summary>
    public async ValueTask ReloadAsync()
    {
        await store.FlushSettingsAsync(); // a tempo or slider change may still be waiting to be written
        await js.InvokeVoidAsync("location.reload");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
    }

    private sealed record VersionFile(string? Version);
}
