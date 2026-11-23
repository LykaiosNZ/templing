using System.Text.Json;
using Microsoft.JSInterop;
using Templing.Models;

namespace Templing.Services;

public enum MetronomeSource { Free, Piece, Preset }

public class EngineState
{
    public bool running { get; set; }
    /// <summary>Paused mid-run: still <see cref="running"/> (the run goes on when resumed), but the clock is stopped.</summary>
    public bool paused { get; set; }
    public string phase { get; set; } = "stopped";
    /// <summary>The section the scheduler is on (it runs ahead of what's heard): the one the app keeps as current.</summary>
    public int step { get; set; }
    /// <summary>The section sounding now, for display. <see cref="bar"/>, <see cref="beats"/> and <see cref="unit"/> are what's sounding too.</summary>
    public int? heardStep { get; set; }
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
    /// <summary>The level the change lands on: an index into <see cref="TrainingSettings.Levels"/>. Over time, it can skip levels when they're shorter than a bar.</summary>
    public int? level { get; set; }
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

    /// <summary>The loaded piece's, custom metronome's or free metronome's own click; null = the default from Settings.</summary>
    public string? SourceSound { get; private set; }
    /// <summary>The click that plays (a <see cref="ClickSounds"/> id).</summary>
    public string Sound => ClickSounds.Resolve(SourceSound ?? Settings.Sound);
    public EngineState State { get; private set; } = new();
    /// <summary>A run is under way, playing or paused.</summary>
    public bool IsRunning => State.running;
    /// <summary>Paused mid-run: <see cref="IsRunning"/> stays true, and play resumes from where it was.</summary>
    public bool IsPaused => State.running && State.paused;

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

    /// <summary>
    /// Speed trainer used for the next run: a plan step's own settings, else the user's with a piece's Start/End
    /// laid over them while that piece is loaded (<see cref="_pieceTraining"/>), else the user's.
    /// </summary>
    public TrainingSettings Training => _stepTraining ?? _pieceTraining ?? Settings.Training;
    private TrainingSettings? _stepTraining;
    /// <summary>
    /// While a piece is loaded: the user's trainer settings with Start = the piece's opening tempo and End = its goal
    /// (or last tempo). Edits to it last until another source is loaded and are never saved, so a piece never
    /// rewrites the trainer you set up yourself. Switching it on or off still follows (and saves) yours.
    /// </summary>
    private TrainingSettings? _pieceTraining;
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
    /// <summary>The section sounding now (while running, the scheduler can already be a beat into the next one).</summary>
    public int PlayingStep => Math.Clamp(IsRunning ? State.heardStep ?? State.step : CurrentStep, 0, Sections.Count - 1);
    public Section PlayingSection => Sections[PlayingStep];

    /// <summary>The tempo value shown to the user, in <see cref="Unit"/>.</summary>
    public double Value => ToValue(IsRunning ? State.factor : Factor);
    public double PlayingBpm => PlayingSection.Bpm * (IsRunning ? State.factor : Factor);

    public double ToValue(double factor) => Unit == TempoUnit.Percent ? Math.Round(factor * 100, 1) : Math.Round(RefSection.Bpm * factor, 1);
    public double ToFactor(double value) => Unit == TempoUnit.Percent ? value / 100.0 : value / Math.Max(1, RefSection.Bpm);
    public double Min => TempoLimits.Min(Unit);
    public double Max => TempoLimits.Max(Unit);

    /// <summary>Draws the Metronome page's beat display as the run stands. False until the engine has loaded.</summary>
    public async Task<bool> RefreshDisplayAsync()
    {
        if (_mod == null) return false;
        await _mod.InvokeVoidAsync("refreshDisplay");
        return true;
    }

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
        Source = MetronomeSource.Free; Piece = null; Preset = null; _pieceTraining = null;
        SourceSound = store.Settings.FreeSound;
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

    /// <summary>
    /// Where a piece opens: its Start at, else the slowest tempo of the last session (the low end of "95–155"),
    /// else the last tempo, else the section's tempo (80% for % pieces). Never the last tempo by default, since
    /// practice usually builds back up to it.
    /// </summary>
    public double OpeningTempo(Piece piece)
    {
        var st = store.StatsFor(piece);
        return piece.StartTempo ?? st.LastMin ?? st.Last ?? (piece.Unit == TempoUnit.Percent ? 80 : RefSection.Bpm);
    }

    /// <summary>
    /// Where a piece's trainer ramps to: the goal, else the latest tempo, else the best, whichever is first above the
    /// start (a trainer only ramps up). Past the goal already? It ramps to the latest tempo; nothing above? It holds.
    /// </summary>
    double TrainerEnd(Piece piece, double start)
    {
        var st = store.StatsFor(piece);
        return new[] { piece.Goal, st.Last, st.Best }.FirstOrDefault(v => v > start) ?? start;
    }

    /// <summary>Switches the speed trainer on or off. With a piece loaded this also sets (and saves) the user's own switch.</summary>
    public async Task SetTrainingEnabledAsync(bool on)
    {
        Training.Enabled = on;
        if (_pieceTraining != null && _stepTraining == null) Settings.Training.Enabled = on;
        await ApplyAsync();
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
        SourceSound = piece.Sound;
        Sections = piece.Sections.Count > 0 ? piece.Sections.Select(s => s.Clone()).ToList() : [new Section()];
        PlayThrough = piece.PlayThrough && Sections.Count > 1; Loop = piece.Loop;
        CurrentStep = sectionId == null ? 0 : Math.Max(0, Sections.FindIndex(s => s.Id == sectionId));
        var start = OpeningTempo(piece);
        Factor = ToFactor(start);
        var own = Settings.Training;
        _pieceTraining = new TrainingSettings
        {
            Enabled = own.Enabled, Mode = own.Mode, DurationMin = own.DurationMin, EveryBars = own.EveryBars, StopAtEnd = own.StopAtEnd,
            Start = Math.Round(start), End = Math.Round(TrainerEnd(piece, start)),
        };
        ResetSession();
        await PushConfig();
        Changed?.Invoke();
    }

    public async Task LoadPresetAsync(MetronomePreset p)
    {
        await StopAsync();
        Source = MetronomeSource.Preset; Preset = p; Piece = null; _pieceTraining = null;
        SourceSound = p.Sound;
        Sections = p.Sections.Select(s => s.Clone()).ToList();
        if (Sections.Count == 0) Sections.Add(new Section());
        PlayThrough = p.PlayThrough; Loop = p.Loop;
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
    public MetronomePreset ToPreset(string name) => new() { Name = name, Sections = Sections.Select(s => s.Clone()).ToList(), PlayThrough = PlayThrough, Loop = Loop, Sound = SourceSound };

    /// <summary>
    /// How sections play: loop the current one, or play through in order (then stop, or <paramref name="loop"/> back to
    /// the first). Takes effect straight away, even mid-run. A piece remembers it unless <paramref name="remember"/> is false
    /// (a plan step on one section); a custom metronome keeps it when saved.
    /// </summary>
    public async Task SetPlaybackAsync(bool playThrough, bool loop, bool remember = true)
    {
        PlayThrough = playThrough && Sections.Count > 1; Loop = loop;
        await PushConfig();
        if (remember && Source == MetronomeSource.Piece && Piece != null)
        {
            Piece.PlayThrough = PlayThrough; Piece.Loop = Loop;
            await store.SavePieceAsync(Piece);
        }
        Changed?.Invoke();
    }

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

    /// <summary>Freezes the run where it is: the click, the work / rest countdown, the speed trainer and practice time all wait.</summary>
    public async Task PauseAsync()
    {
        if (_mod != null && IsRunning && !IsPaused) await _mod.InvokeVoidAsync("pause");
    }

    /// <summary>Carries on from a pause.</summary>
    public async Task ResumeAsync()
    {
        if (_mod != null && IsPaused) await _mod.InvokeVoidAsync("resume");
    }

    /// <summary>The play button: starts, pauses, or resumes. <paramref name="start"/> replaces a plain start (the Timer's countdown).</summary>
    public Task PlayPauseAsync(Func<Task>? start = null) =>
        !IsRunning ? (start ?? StartAsync)() : IsPaused ? ResumeAsync() : PauseAsync();

    public async Task RestartSectionAsync()
    {
        if (_mod != null) await _mod.InvokeVoidAsync("restartSection");
    }

    /// <summary>True while a rest is counting down (between interval rounds, before an extra block, or a rest-only countdown).</summary>
    public bool Resting => IsRunning && State.phase == "rest";

    /// <summary>Ends the current rest now: the next round starts (a rest-only countdown finishes).</summary>
    public async Task SkipRestAsync()
    {
        if (_mod != null && Resting) await _mod.InvokeVoidAsync("skipRest");
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
            if (Source == MetronomeSource.Free) store.SaveSettingsSoon(); // +/− and held keys change it many times a second
        }
        else Factor = ToFactor(value);
        if (Training.Enabled && IsRunning) Training.Enabled = false; // manual override ends ramp
        // the engine plays the new tempo straight away: show it now, not the old one until its next update
        // (which also made + and - step from a stale value when pressed quickly)
        if (IsRunning) State.factor = Factor;
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
            store.SaveSettingsSoon(); // sliders call this on every step
        }
        Changed?.Invoke();
    }

    private async Task PushConfig()
    {
        if (_mod == null) return;
        var s = Settings;
        var tr = Training;
        var iv = UserIntervals;
        var cfg = new
        {
            steps = Sections.Select(x => new { bpm = x.Bpm, beatsPerBar = x.BeatsPerBar, beatUnit = x.BeatUnit, subdivision = x.Subdivision, bars = x.Length, scoreBars = x.ScoreBars }),
            playThrough = PlayThrough && Sections.Count > 1,
            loop = Loop,
            step = CurrentStep,
            factor = Factor,
            countIn = new { enabled = s.CountIn, bars = s.CountInBars },
            volume = Muted ? 0 : s.Volume,
            sound = Sound,
            accentFirst = s.AccentFirst,
            training = new
            {
                enabled = tr.Enabled,
                startFactor = ToFactor(tr.Start),
                endFactor = ToFactor(tr.End),
                mode = tr.Mode == RampMode.EveryNBars ? "bars" : "time",
                durationSec = tr.DurationMin * 60,
                everyBars = tr.EveryBars,
                levels = tr.Levels().Select(ToFactor),
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
                cues = iv.VoiceCues,
            }
        };
        await _mod.InvokeVoidAsync("configure", JsonSerializer.Serialize(cfg));
    }

    /// <summary>
    /// The click for what's loaded, saved with it straight away: the piece, the custom metronome or the free metronome.
    /// Null goes back to the default from Settings. A running metronome changes sound on its next click.
    /// </summary>
    public async Task SetSoundAsync(string? id)
    {
        SourceSound = id;
        switch (Source)
        {
            case MetronomeSource.Piece when Piece != null: Piece.Sound = id; await store.SavePieceAsync(Piece); break;
            case MetronomeSource.Preset when Preset != null: Preset.Sound = id; await store.SavePresetAsync(Preset); break;
            default: store.Settings.FreeSound = id; await store.SaveSettingsAsync(); break;
        }
        await PushConfig();
        Changed?.Invoke();
    }

    /// <summary>Plays a bar of a click (hi, then three lo), <paramref name="id"/> or the one that plays now.</summary>
    public async Task PreviewSoundAsync(string? id = null)
    {
        if (_mod == null) return;
        await PushConfig();
        await _mod.InvokeVoidAsync("previewSound", ClickSounds.Resolve(id ?? Sound));
    }

    /// <summary>Starts loading a click's samples, so the first preview or click isn't silent.</summary>
    public async Task PreloadSoundAsync(string id)
    {
        if (_mod != null) await _mod.InvokeVoidAsync("loadSound", ClickSounds.Resolve(id));
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
            // keep showing where the ramp got to, as a factor only: the free metronome's saved tempo stays as you set it
            if (Training.Enabled) Factor = s.factor;
        }
        if (s.@event == "finished")
        {
            LastFinishReason = s.reason;
            if (s.reason == "end") CurrentStep = 0; // played through to the end: the next run starts from the top
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
