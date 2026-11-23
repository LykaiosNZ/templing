using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Templing.Models;

namespace Templing.Services;

public enum StepState { Ready, Running, Finished }

/// <summary>Shown after the last step: what got done.</summary>
public record PlanSummary(string Name, int Steps, int Done, int Logs, int LoggedSec);

/// <summary>
/// Walks through a practice plan one step at a time. Each step loads its piece into the metronome
/// (or the click-free timer) with the step's intervals pinned; rest steps run a rest countdown.
/// When a step finishes, <see cref="StepFinished"/> fires so the UI can log it (ask or auto) and move on.
/// The run (plan, step, progress) is saved in the "session" store so a page refresh resumes it.
/// A running plan can be edited (<see cref="ApplyEditAsync"/>): the changes are saved to the plan, or kept for this run only.
/// </summary>
public class PlanRunner
{
    private readonly MetronomeService _metro;
    private readonly DataStore _store;
    private readonly NavigationManager _nav;
    private const string RunKey = "planrun";

    /// <summary>Persisted run state (transient: deliberately not part of backups). <c>Edited</c>: the run's own copy of the plan, when it was changed for this run only.</summary>
    private record RunState(string Id, string PlanId, int Index, List<string> Completed, int Logs, int LoggedSec, PracticePlan? Edited = null);

    public PlanRunner(MetronomeService metro, DataStore store, NavigationManager nav)
    {
        _metro = metro; _store = store; _nav = nav;
        _metro.Finished += OnMetroFinished;
        _metro.Changed += OnMetroChanged;
    }

    /// <summary>The plan being run: the saved plan, or this run's own copy once it was changed for this session only.</summary>
    public PracticePlan? Plan { get; private set; }
    /// <summary>The run's steps were changed for this session only (the saved plan is as it was).</summary>
    public bool EditedForSession { get; private set; }
    public int Index { get; private set; }
    public StepState State { get; private set; }
    public bool Active => Plan != null;
    public PlanStep? Current => Plan != null && Index < Plan.Steps.Count ? Plan.Steps[Index] : null;
    public PlanStep? NextStep => Plan != null && Index + 1 < Plan.Steps.Count ? Plan.Steps[Index + 1] : null;
    public bool IsLast => Plan != null && Index >= Plan.Steps.Count - 1;
    /// <summary>Step ids that were logged (or completed, for rests) in this run.</summary>
    public HashSet<string> Completed { get; } = new();
    public int Logs { get; private set; }
    public int LoggedSec { get; private set; }
    /// <summary>Set when the plan finishes; cleared on dismiss or a new run.</summary>
    public PlanSummary? Summary { get; private set; }

    /// <summary>Planned minutes left: the current step and every later step not yet done.</summary>
    public double MinutesLeft => Plan == null ? 0
        : Plan.Steps.Skip(Index).Where(s => !Completed.Contains(s.Id)).Sum(s => s.TotalMinutes);

    public event Action? Changed;
    /// <summary>An piece step finished on its own; the handler should log it and then call <see cref="AdvanceAsync"/>.</summary>
    public event Func<PlanStep, Task>? StepFinished;
    /// <summary>Somewhere asked to edit the running plan; the plan bar handles it (it owns the "log this step first?" questions).</summary>
    public event Func<Task>? EditRequested;

    public Task RequestEditAsync() => EditRequested?.Invoke() ?? Task.CompletedTask;

    public string Describe(PlanStep? s)
    {
        if (s == null) return "";
        if (s.Kind == PlanStepKind.Rest) return "Rest";
        var piece = _store.GetPiece(s.PieceId);
        var part = piece?.Sections.FirstOrDefault(x => x.Id == s.SectionId)?.Name;
        return (piece?.Name ?? "Missing piece") + (part != null ? $" · {part}" : "");
    }

    /// <summary>Start a run, from the first step or from <paramref name="startIndex"/> (earlier steps count as skipped).</summary>
    public async Task StartAsync(PracticePlan plan, int startIndex = 0)
    {
        if (plan.Steps.Count == 0) return;
        await _metro.StopAsync();
        Plan = plan; EditedForSession = false; Index = Math.Clamp(startIndex, 0, plan.Steps.Count - 1); Completed.Clear(); Logs = 0; LoggedSec = 0; Summary = null;
        plan.LastRun = DateTime.Now;
        await _store.SavePlanAsync(plan);
        await LoadStepAsync(navigate: true, autoStartRest: true);
    }

    /// <summary>Resume a run saved before a refresh. Stays on the current page; rests wait for Start.</summary>
    public async Task RestoreAsync()
    {
        if (Plan != null) return;
        RunState? run;
        try { run = JsonSerializer.Deserialize<RunState>(await _store.GetSessionAsync(RunKey) ?? "null", DataStore.Json); }
        catch (JsonException) { run = null; }
        var plan = _store.GetPlan(run?.PlanId);
        if (run == null || plan == null || plan.Steps.Count == 0) { await _store.RemoveSessionAsync(RunKey); return; }
        EditedForSession = run.Edited is { Steps.Count: > 0 };
        if (EditedForSession) plan = run.Edited!;
        Plan = plan;
        Index = Math.Clamp(run.Index, 0, plan.Steps.Count - 1);
        Completed.Clear(); foreach (var id in run.Completed) Completed.Add(id);
        Logs = run.Logs; LoggedSec = run.LoggedSec;
        await LoadStepAsync(navigate: false, autoStartRest: false);
    }

    /// <summary>Go to a step. Rests start straight away; a piece step only with <paramref name="autoStartPiece"/>.</summary>
    public async Task GoToAsync(int index, bool autoStartPiece = false)
    {
        if (Plan == null) return;
        await _metro.StopAsync();
        Index = Math.Clamp(index, 0, Plan.Steps.Count - 1);
        await LoadStepAsync(navigate: true, autoStartRest: true, autoStartPiece);
    }

    /// <summary>The next step (or end, after the last). Starts a piece step with <see cref="AppSettings.AutoStartPlanStepsOnSkip"/> on.</summary>
    public Task SkipAsync() => IsLast ? EndAsync() : GoToAsync(Index + 1, _store.Settings.AutoStartPlanStepsOnSkip);
    /// <summary>The previous step. Starts a piece step with <see cref="AppSettings.AutoStartPlanStepsOnSkip"/> on.</summary>
    public Task BackAsync() => GoToAsync(Math.Max(0, Index - 1), _store.Settings.AutoStartPlanStepsOnSkip);

    /// <summary>Record a log saved for the current step (for the end-of-plan summary).</summary>
    public async Task RecordLoggedAsync(int? seconds)
    {
        Logs++;
        LoggedSec += seconds ?? 0;
        await SaveRunAsync();
    }

    /// <summary>Called after the finished step was logged (or not): move to the next step, or end. With <see cref="AppSettings.AutoStartPlanSteps"/> on, a piece step starts straight away.</summary>
    public async Task AdvanceAsync()
    {
        if (Plan == null) return;
        if (Current != null) Completed.Add(Current.Id);
        if (IsLast) { await EndAsync(finished: true); return; }
        Index++;
        await LoadStepAsync(navigate: true, autoStartRest: true, autoStartPiece: _store.Settings.AutoStartPlanSteps);
    }

    /// <summary>Would running <paramref name="edited"/> change (or remove) the current step? Then applying it restarts that step.</summary>
    public bool ChangesCurrentStep(PracticePlan edited)
    {
        if (Current is not { } cur) return false;
        var now = edited.Steps.FirstOrDefault(s => s.Id == cur.Id);
        return now == null || JsonSerializer.Serialize(now, DataStore.Json) != JsonSerializer.Serialize(cur, DataStore.Json);
    }

    /// <summary>
    /// Carry on the run with an edited copy of its plan, saved to the plan (<paramref name="savePlan"/>) or for this run only.
    /// The run stays on the current step wherever it moved to; if it was removed, on the next step that's left. A changed
    /// current step is loaded again, so ask about its practice time first (see <see cref="ChangesCurrentStep"/>);
    /// changes to other steps leave whatever is playing alone.
    /// </summary>
    public async Task ApplyEditAsync(PracticePlan edited, bool savePlan)
    {
        if (Plan == null || edited.Steps.Count == 0) return;
        var restart = ChangesCurrentStep(edited);
        // on the old step's page: go to the new one (elsewhere, e.g. the Plans page, stay put)
        var onStepPage = Current is { } cur && OnPage(StepUrl(cur));
        var index = Plan.Steps.Skip(Index).Select(s => edited.Steps.FindIndex(e => e.Id == s.Id))
            .FirstOrDefault(i => i >= 0, Math.Min(Index, edited.Steps.Count - 1));
        if (savePlan)
        {
            // the dialog edits steps and name; anything changed on the plan meanwhile (star, last run) stays
            if (_store.GetPlan(edited.Id) is { } saved) { edited.Favourite = saved.Favourite; edited.LastRun = saved.LastRun; edited.Created = saved.Created; }
            await _store.SavePlanAsync(edited);
        }
        Plan = edited;
        EditedForSession = !savePlan;
        Index = index;
        Completed.RemoveWhere(id => !edited.Steps.Any(s => s.Id == id));
        if (restart)
        {
            await _metro.StopAsync();
            await LoadStepAsync(navigate: onStepPage, autoStartRest: false);
        }
        else
        {
            await SaveRunAsync();
            Changed?.Invoke();
        }
    }

    public bool FinishedAll => Summary != null;

    public async Task EndAsync(bool finished = false)
    {
        await _metro.StopAsync();
        await _metro.ClearStepIntervalsAsync();
        Summary = finished && Plan != null ? new PlanSummary(Plan.Name, Plan.Steps.Count, Completed.Count, Logs, LoggedSec) : null;
        Plan = null;
        EditedForSession = false;
        await _store.RemoveSessionAsync(RunKey);
        Changed?.Invoke();
    }

    public void DismissSummary() { Summary = null; Changed?.Invoke(); }

    /// <summary>The page a step is practised on: the Timer for rests and timer steps, else the Metronome (with its section).</summary>
    public static string StepUrl(PlanStep s) =>
        s.Kind == PlanStepKind.Rest ? "timer"
        : s.Mode == StepMode.Timer ? $"timer/piece/{s.PieceId}"
        : s.SectionId != null ? $"metronome/piece/{s.PieceId}/{s.SectionId}" : $"metronome/piece/{s.PieceId}";

    /// <summary>
    /// Start (or restart) the current step's clock. Practice stays on its page, so this goes to the step's page first
    /// (Start step / Resume can be pressed from anywhere), and a metronome step always clicks: the engine may still be
    /// in the timer's silent mode from a rest or timer step.
    /// </summary>
    public async Task StartStepAsync()
    {
        var s = Current;
        if (s == null) return;
        var url = StepUrl(s);
        if (!OnPage(url)) _nav.NavigateTo(url);
        if (s.Kind == PlanStepKind.Rest) await _metro.StartRestAsync(s.Minutes);
        else if (s.Mode == StepMode.Timer) await _metro.StartTimerAsync();
        else
        {
            await _metro.SetSilentAsync(false);
            await _metro.StartAsync();
        }
        State = StepState.Running;
        Changed?.Invoke();
    }

    private bool OnPage(string url) =>
        string.Equals(_nav.ToBaseRelativePath(_nav.Uri).Split('?', '#')[0], url, StringComparison.OrdinalIgnoreCase);

    private Task SaveRunAsync() => Plan == null ? Task.CompletedTask
        : _store.PutSessionAsync(new RunState(RunKey, Plan.Id, Index, Completed.ToList(), Logs, LoggedSec, EditedForSession ? Plan : null));

    /// <summary>Load the current step. Rests start on their own unless restoring; a piece step only when the plan moved on by itself and the setting is on.</summary>
    private async Task LoadStepAsync(bool navigate, bool autoStartRest, bool autoStartPiece = false)
    {
        var s = Current;
        if (s == null) { await EndAsync(true); return; }
        // a new step: whatever was playing belongs to the last one (and practice pages ask before you leave them while running)
        if (navigate && _metro.IsRunning) await _metro.StopAsync();
        State = StepState.Ready;
        Summary = null;
        await SaveRunAsync();
        if (s.Kind == PlanStepKind.Rest)
        {
            await _metro.ClearStepIntervalsAsync();
            if (navigate) _nav.NavigateTo(StepUrl(s));
            Changed?.Invoke();
            if (autoStartRest) await StartStepAsync(); // rests start straight away
            return;
        }
        var piece = _store.GetPiece(s.PieceId);
        if (piece == null) { Changed?.Invoke(); return; } // piece deleted: user can skip
        await _metro.LoadPieceAsync(piece, s.SectionId);
        // a step on one section stays on it, whatever the piece usually does
        if (s.SectionId != null && _metro.PlayThrough) await _metro.SetPlaybackAsync(false, _metro.Loop, remember: false);
        _metro.ResetSession();
        await _metro.SetStepIntervalsAsync(s.Minutes, s.RestMin, s.Rounds);
        if (s.Mode == StepMode.Metronome)
            await _metro.SetStepTempoAsync(s.StartTempo, s.TrainerSettings());
        if (navigate) _nav.NavigateTo(StepUrl(s));
        Changed?.Invoke();
        if (autoStartPiece) await StartStepAsync();
    }

    private async void OnMetroFinished(string reason)
    {
        var s = Current;
        if (s == null || State != StepState.Running) return;
        if (s.Kind == PlanStepKind.Rest)
        {
            if (reason == "rest") await AdvanceAsync();
            return;
        }
        if (reason is "interval" or "end" or "training")
        {
            State = StepState.Finished;
            Changed?.Invoke();
            if (StepFinished != null) await StepFinished.Invoke(s);
        }
    }

    private void OnMetroChanged()
    {
        // started with Space / the play button instead of the plan bar
        if (Active && State != StepState.Running && _metro.IsRunning)
        {
            State = StepState.Running;
            Changed?.Invoke();
        }
        // a manual stop mid-step puts the step back to "ready" (time so far is kept in the session)
        // (only an explicit "stopped" event counts: the engine also reports running=false just before "finished")
        else if (Active && State == StepState.Running && !_metro.IsRunning && _metro.State.@event == "stopped")
        {
            State = StepState.Ready;
            Changed?.Invoke();
        }
    }
}
