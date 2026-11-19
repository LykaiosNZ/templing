using System.Text.Json;
using Microsoft.JSInterop;
using Songler.Models;

namespace Songler.Services;

public enum MetronomeSource { Free, Piece, Preset }

public class EngineState
{
    public bool running { get; set; }
    public string phase { get; set; } = "stopped";
    public int step { get; set; }
    public int bar { get; set; }
    public double factor { get; set; } = 1;
    public double active { get; set; }
    public double? remaining { get; set; }
    public int round { get; set; } = 1;
    public int? count { get; set; }
    public int beats { get; set; } = 4;
    public int unit { get; set; } = 4;
    public int barInPart { get; set; }
    public double? trainingProgress { get; set; }
    public TrainingNext? trainingNext { get; set; }
    public double? workElapsed { get; set; }
    public string? finished { get; set; }
    public string? @event { get; set; }
    public string? reason { get; set; }
}

public class TrainingNext
{
    public int? bars { get; set; }
    public int? beats { get; set; }
    public double? sec { get; set; }
}

/// <summary>
/// App-wide metronome. Lives for the whole app so it keeps playing while you browse other pages.
/// Tempo is held as a factor over each section's reference BPM so tempo maps scale together.
/// </summary>
public class MetronomeService(IJSRuntime js, DataStore store) : IAsyncDisposable
{
    private IJSObjectReference? _mod;
    private DotNetObjectReference<MetronomeService>? _ref;
    private readonly List<DateTime> _taps = new();

    public MetronomeSource Source { get; private set; } = MetronomeSource.Free;
    public Piece? Piece { get; private set; }
    public MetronomePreset? Preset { get; private set; }
    public List<Section> Sections { get; private set; } = new() { new Section { Name = "Click", Bpm = 100 } };
    public bool PlayThrough { get; set; }
    public bool Loop { get; set; } = true;
    public int CurrentStep { get; private set; }
    public double Factor { get; private set; } = 1;
    public TempoUnit Unit => Source == MetronomeSource.Piece && Piece != null ? Piece.Unit : TempoUnit.Bpm;
    public MetronomeSettings Settings => store.Settings.Metronome;
    public EngineState State { get; private set; } = new();
    public bool IsRunning => State.running;

    // session tracking (survives multiple start/stop cycles until logged or reset)
    public double SessionBankedSec { get; private set; }
    public double SessionActiveSec => SessionBankedSec + (State.running ? State.active : 0);
    public double? SessionMax { get; private set; }
    public double? SessionMin { get; private set; }
    public string? LastFinishReason { get; private set; }

    /// <summary>Timer mode: no click at all (e.g. playing along to Guitar Pro); intervals and cues still run.</summary>
    public bool Silent { get; private set; }
    /// <summary>Click muted for now (the M key). Not saved: the volume setting stays as it is.</summary>
    public bool Muted { get; private set; }
    /// <summary>True while a rest-only countdown is running (not practice time).</summary>
    public bool RestOnlyRunning => IsRunning && _override is { RestOnly: true };

    /// <summary>One-off interval override for timer extras (extend / rest countdown); cleared when the run ends.</summary>
    private record IntervalOverride(bool Enabled, double WorkSec, double RestSec, int Rounds, double RestFirstSec, bool RestOnly);
    private IntervalOverride? _override;
    /// <summary>Intervals pinned by a running plan step; stays until the plan moves on (unlike <see cref="_override"/>).</summary>
    private IntervalOverride? _step;
    public bool StepPinned => _step != null;

    /// <summary>Speed trainer used for the next run: a plan step's own settings, or the user's.</summary>
    public TrainingSettings Training => _stepTraining ?? Settings.Training;
    private TrainingSettings? _stepTraining;
    public bool TrainingPinned => _stepTraining != null;

    /// <summary>Applies a plan step's tempo: optional start tempo and a step-only speed trainer (on or off).</summary>
    public async Task SetStepTempoAsync(double? startValue, TrainingSettings stepTraining)
    {
        _stepTraining = stepTraining;
        if (stepTraining.Enabled) Factor = ToFactor(stepTraining.Start);
        else if (startValue is { } v) Factor = ToFactor(v);
        await PushConfig();
        Changed?.Invoke();
    }

    public async Task SetStepIntervalsAsync(double workMin, double restMin, int rounds)
    {
        _step = new(true, workMin * 60, restMin * 60, Math.Max(1, rounds), 0, false);
        await PushConfig();
        Changed?.Invoke();
    }

    public async Task ClearStepIntervalsAsync()
    {
        if (_step == null && _stepTraining == null) return;
        _step = null;
        _stepTraining = null;
        await PushConfig();
        Changed?.Invoke();
    }

    /// <summary>The user's interval settings for the current mode: the timer's countdown (always on) or the metronome's intervals.</summary>
    private IntervalSettings UserIntervals => Silent ? Settings.Timer : Settings.Interval;
    private bool UserIntervalsOn => Silent || Settings.Interval.Enabled;

    /// <summary>The interval plan the next run will use (step override or the user's settings).</summary>
    public (bool Enabled, double WorkSec, double RestSec, int Rounds) EffectiveIntervals =>
        _step is { } st ? (true, st.WorkSec, st.RestSec, st.Rounds)
        : (UserIntervalsOn, UserIntervals.WorkMin * 60, UserIntervals.RestMin * 60, UserIntervals.Rounds);

    /// <summary>Starts a silent practice countdown using the timer settings.</summary>
    public async Task StartTimerAsync()
    {
        Silent = true; _override = null;
        await StartAsync();
    }

    /// <summary>Another work block, optionally after a rest (e.g. +5 min after 1 min rest).</summary>
    public async Task ExtendAsync(double workMin, double restFirstMin)
    {
        Silent = true;
        _override = new(true, workMin * 60, 0, 1, restFirstMin * 60, false);
        await StartAsync();
    }

    /// <summary>A rest countdown between pieces. Not counted as practice.</summary>
    public async Task StartRestAsync(double minutes)
    {
        Silent = true;
        _override = new(false, 0, 0, 0, minutes * 60, true);
        await StartAsync();
    }

    public async Task SetMutedAsync(bool muted)
    {
        if (Muted == muted) return;
        Muted = muted;
        await PushConfig();
        Changed?.Invoke();
    }

    /// <summary>Leave timer mode so the metronome clicks again.</summary>
    public async Task SetSilentAsync(bool silent)
    {
        if (Silent == silent) return;
        Silent = silent; _override = null;
        await PushConfig();
        Changed?.Invoke();
    }

    public event Action? Changed;
    /// <summary>Raised when an interval / training / play-through run completes on its own.</summary>
    public event Action<string>? Finished;

    /// <summary>The loaded preset or piece was renamed: tell listeners (mini player, page title) to redraw.</summary>
    public void NotifyTitleChanged() => Changed?.Invoke();

    public string Title => Source switch
    {
        MetronomeSource.Piece => Piece?.Name ?? "Piece",
        MetronomeSource.Preset => Preset?.Name ?? "Preset",
        _ => "Metronome"
    };

    public Section RefSection => Sections[Math.Clamp(CurrentStep, 0, Sections.Count - 1)];
    public Section PlayingSection => Sections[Math.Clamp(IsRunning ? State.step : CurrentStep, 0, Sections.Count - 1)];

    /// <summary>The tempo value shown to the user, in <see cref="Unit"/>.</summary>
    public double Value => ToValue(IsRunning ? State.factor : Factor);
    public double PlayingBpm => PlayingSection.Bpm * (IsRunning ? State.factor : Factor);

    public double ToValue(double factor) => Unit == TempoUnit.Percent ? Math.Round(factor * 100, 1) : Math.Round(RefSection.Bpm * factor, 1);
    public double ToFactor(double value) => Unit == TempoUnit.Percent ? value / 100.0 : value / Math.Max(1, RefSection.Bpm);
    public double Min => Unit == TempoUnit.Percent ? 10 : 20;
    public double Max => Unit == TempoUnit.Percent ? 200 : 400;

    public async Task InitAsync()
    {
        if (_mod != null) return;
        await store.EnsureLoadedAsync();
        _mod = await js.InvokeAsync<IJSObjectReference>("import", "./js/metronome.js");
        _ref = DotNetObjectReference.Create(this);
        await _mod.InvokeVoidAsync("init", _ref);
        LoadFreeInternal();
        await PushConfig();
        Changed?.Invoke(); // listeners that rendered before init (the mini player) show the loaded tempo
    }

    // ------------------------------------------------------------ loading
    private void LoadFreeInternal()
    {
        Source = MetronomeSource.Free; Piece = null; Preset = null;
        // the free metronome is a single click: tempo maps belong to custom metronomes and pieces
        Sections = [store.Settings.FreeSections.FirstOrDefault() ?? new Section { Name = "Click", Bpm = 100 }];
        PlayThrough = false;
        CurrentStep = 0; Factor = 1;
    }

    public async Task LoadFreeAsync()
    {
        if (Source == MetronomeSource.Free) return;
        await StopAsync();
        LoadFreeInternal();
        ResetSession();
        await PushConfig();
        Changed?.Invoke();
    }

    public async Task LoadPieceAsync(Piece piece, string? sectionId = null)
    {
        if (Source == MetronomeSource.Piece && Piece?.Id == piece.Id)
        {
            if (sectionId != null) await SetStepAsync(Math.Max(0, Piece.Sections.FindIndex(s => s.Id == sectionId)));
            return;
        }
        await StopAsync();
        Source = MetronomeSource.Piece; Piece = piece; Preset = null;
        Sections = piece.Sections.Count > 0 ? piece.Sections.Select(s => s.Clone()).ToList() : [new Section()];
        PlayThrough = false;
        CurrentStep = sectionId == null ? 0 : Math.Max(0, Sections.FindIndex(s => s.Id == sectionId));
        var stats = Stats.ForPiece(piece, store.Logs);
        var start = stats.Last ?? (piece.Unit == TempoUnit.Percent ? 80 : RefSection.Bpm);
        Factor = ToFactor(start);
        var tr = Training;
        tr.End = Math.Round(stats.Last ?? piece.Goal ?? start);
        tr.Start = Math.Round(tr.End * 0.85);
        ResetSession();
        await PushConfig();
        Changed?.Invoke();
    }

    public async Task LoadPresetAsync(MetronomePreset p)
    {
        await StopAsync();
        Source = MetronomeSource.Preset; Preset = p; Piece = null;
        Sections = p.Sections.Select(s => s.Clone()).ToList();
        if (Sections.Count == 0) Sections.Add(new Section());
        PlayThrough = p.PlayThrough;
        CurrentStep = 0; Factor = 1;
        ResetSession();
        await PushConfig();
        Changed?.Invoke();
    }

    /// <summary>Replace the tempo map of a custom metronome (persisted by saving the preset).</summary>
    public async Task ReplaceSectionsAsync(List<Section> sections, bool playThrough, bool loop)
    {
        await StopAsync();
        Sections = sections.Count > 0 ? sections : [new Section()];
        PlayThrough = playThrough && Sections.Count > 1; Loop = loop;
        if (CurrentStep >= Sections.Count) CurrentStep = Sections.Count - 1;
        await PushConfig();
        Changed?.Invoke();
    }

    /// <summary>Copy the current tempo map into a new free/custom metronome.</summary>
    public MetronomePreset ToPreset(string name) => new() { Name = name, Sections = Sections.Select(s => s.Clone()).ToList(), PlayThrough = PlayThrough };

    // ------------------------------------------------------------ transport
    public async Task StartAsync()
    {
        await PushConfig();
        LastFinishReason = null;
        await _mod!.InvokeVoidAsync("start");
    }

    public async Task StopAsync()
    {
        if (_mod != null && IsRunning) await _mod.InvokeVoidAsync("stop");
    }

    public Task ToggleAsync() => IsRunning ? StopAsync() : StartAsync();

    public async Task RestartSectionAsync()
    {
        if (_mod != null) await _mod.InvokeVoidAsync("restartSection");
    }

    public async Task SetStepAsync(int i)
    {
        if (Sections.Count == 0) return;
        i = (i % Sections.Count + Sections.Count) % Sections.Count;
        // in BPM mode keep the *displayed* tempo relative to the new reference section's own tempo
        CurrentStep = i;
        await PushConfig();
        if (_mod != null) await _mod.InvokeVoidAsync("setStep", i);
        Changed?.Invoke();
    }

    public Task NextStepAsync() => SetStepAsync((IsRunning ? State.step : CurrentStep) + 1);
    public Task PrevStepAsync() => SetStepAsync((IsRunning ? State.step : CurrentStep) - 1);

    // ------------------------------------------------------------ tempo
    public async Task SetValueAsync(double value)
    {
        value = Math.Clamp(Math.Round(value), Min, Max);
        if (Source != MetronomeSource.Piece && Sections.Count == 1)
        {
            // single-section free metronome: tempo *is* the section tempo
            Sections[0].Bpm = value; Factor = 1;
            if (Source == MetronomeSource.Free) _ = store.SaveSettingsAsync();
        }
        else Factor = ToFactor(value);
        if (Training.Enabled && IsRunning) Training.Enabled = false; // manual override ends ramp
        await PushConfig();
        Changed?.Invoke();
    }

    public Task NudgeAsync(double delta) => SetValueAsync(Value + delta);

    public async Task TapAsync()
    {
        var now = DateTime.UtcNow;
        if (_taps.Count > 0 && (now - _taps[^1]).TotalSeconds > 2) _taps.Clear();
        _taps.Add(now);
        if (_taps.Count > 6) _taps.RemoveAt(0);
        if (_taps.Count < 2) { Changed?.Invoke(); return; }
        var avg = (_taps[^1] - _taps[0]).TotalSeconds / (_taps.Count - 1);
        var bpm = 60 / avg;
        var value = Unit == TempoUnit.Percent ? bpm / RefSection.Bpm * 100 : bpm;
        if (Source != MetronomeSource.Piece && Sections.Count > 1) value = bpm; // relative to ref section
        await SetValueAsync(value);
    }

    public int TapCount => _taps.Count;

    public async Task ResetFactorAsync()
    {
        Factor = 1; await PushConfig(); Changed?.Invoke();
    }

    // ------------------------------------------------------------ config
    public async Task ApplyAsync(bool persist = true)
    {
        if (CurrentStep >= Sections.Count) CurrentStep = Math.Max(0, Sections.Count - 1);
        await PushConfig();
        if (persist)
        {
            if (Source == MetronomeSource.Free) { store.Settings.FreeSections = Sections; store.Settings.FreePlayThrough = PlayThrough; }
            await store.SaveSettingsAsync();
        }
        Changed?.Invoke();
    }

    private async Task PushConfig()
    {
        if (_mod == null) return;
        var s = Settings;
        var tr = Training;
        var iv = UserIntervals;
        double quantum = Unit == TempoUnit.Percent ? 0.01 : 1.0 / Math.Max(1, RefSection.Bpm);
        var cfg = new
        {
            steps = Sections.Select(x => new { bpm = x.Bpm, beatsPerBar = x.BeatsPerBar, beatUnit = x.BeatUnit, subdivision = x.Subdivision, bars = x.Length, scoreBars = x.ScoreBars }),
            playThrough = PlayThrough && Sections.Count > 1,
            loop = Loop,
            step = CurrentStep,
            factor = Factor,
            countIn = new { enabled = s.CountIn, bars = s.CountInBars },
            volume = Muted ? 0 : s.Volume,
            sound = s.Sound,
            accentFirst = s.AccentFirst,
            flash = s.Flash,
            training = new
            {
                enabled = tr.Enabled,
                startFactor = ToFactor(tr.Start),
                endFactor = ToFactor(tr.End),
                mode = tr.Mode == RampMode.EveryNBars ? "bars" : "time",
                durationSec = tr.DurationMin * 60,
                everyBars = tr.EveryBars,
                incFactor = Unit == TempoUnit.Percent ? tr.Increment / 100 : tr.Increment / Math.Max(1, RefSection.Bpm),
                quantum,
                stopAtEnd = tr.StopAtEnd,
            },
            silent = Silent,
            restFirstSec = (_override ?? _step)?.RestFirstSec ?? 0,
            restOnly = (_override ?? _step)?.RestOnly ?? false,
            interval = new
            {
                enabled = (_override ?? _step)?.Enabled ?? UserIntervalsOn,
                workSec = (_override ?? _step)?.WorkSec ?? iv.WorkMin * 60,
                restSec = (_override ?? _step)?.RestSec ?? iv.RestMin * 60,
                rounds = (_override ?? _step)?.Rounds ?? iv.Rounds,
                cues = iv.VoiceCues || Silent,
            }
        };
        await _mod.InvokeVoidAsync("configure", JsonSerializer.Serialize(cfg));
    }

    public async Task PreviewSoundAsync()
    {
        await PushConfig();
        await _mod!.InvokeVoidAsync("previewClick");
    }

    // ------------------------------------------------------------ engine callbacks
    [JSInvokable]
    public void OnEngineEvent(string json)
    {
        var s = JsonSerializer.Deserialize<EngineState>(json);
        if (s == null) return;
        var wasRunning = State.running;
        State = s;
        if (s.running && s.phase == "work" && !Silent)
        {
            var v = ToValue(s.factor);
            SessionMax = SessionMax is null ? v : Math.Max(SessionMax.Value, v);
            SessionMin = SessionMin is null ? v : Math.Min(SessionMin.Value, v);
        }
        if (s.running) CurrentStep = s.step;
        if (s.@event is "stopped" or "finished")
        {
            if (_override is { RestOnly: true }) s.active = 0; // rest countdown isn't practice
            _override = null;
            SessionBankedSec += s.active;
            if (Training.Enabled)
            {
                // keep where the ramp got to
                if (Source != MetronomeSource.Piece && Sections.Count == 1)
                {
                    Sections[0].Bpm = Math.Round(Sections[0].Bpm * s.factor);
                    Factor = 1;
                }
                else Factor = s.factor;
            }
        }
        if (s.@event == "finished")
        {
            LastFinishReason = s.reason;
            Finished?.Invoke(s.reason ?? "end");
        }
        _ = wasRunning;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ session
    public void ResetSession()
    {
        SessionBankedSec = 0; SessionMax = null; SessionMin = null;
        Changed?.Invoke();
    }

    public PracticeLog BuildSessionLog()
    {
        var piece = Source == MetronomeSource.Piece ? Piece : null;
        var max = SessionMax ?? Value;
        var min = SessionMin;
        return new PracticeLog
        {
            Date = DateTime.Today,
            PieceId = piece?.Id,
            PieceName = piece?.Name ?? (Preset?.Name ?? "Metronome"),
            // timer mode usually means playing along to the whole song: default to a full run-through
            SectionId = piece != null && piece.Sections.Count > 1 && !PlayThrough && !Silent ? RefSection.Id : null,
            Unit = Unit,
            // timer mode has no click, so there's no tempo to record (type it in if you know it)
            Value = Silent ? null : Math.Round(max),
            ValueMin = !Silent && min.HasValue && Math.Round(min.Value) < Math.Round(max) - 0.5 ? Math.Round(min.Value) : null,
            DurationSec = (int)Math.Round(SessionActiveSec),
            Source = "metronome"
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_mod != null) { try { await _mod.InvokeVoidAsync("stop"); await _mod.DisposeAsync(); } catch { } }
        _ref?.Dispose();
    }
}
