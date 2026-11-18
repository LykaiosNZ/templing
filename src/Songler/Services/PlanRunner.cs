using Microsoft.AspNetCore.Components;
using Songler.Models;

namespace Songler.Services;

public enum StepState { Ready, Running, Finished }

/// <summary>
/// Walks through a practice plan one step at a time. Each step loads its item into the metronome
/// (or the click-free timer) with the step's intervals pinned; rest steps run a rest countdown.
/// When a step finishes, <see cref="StepFinished"/> fires so the UI can log it (ask or auto) and move on.
/// </summary>
public class PlanRunner
{
    private readonly MetronomeService _metro;
    private readonly DataStore _store;
    private readonly NavigationManager _nav;

    public PlanRunner(MetronomeService metro, DataStore store, NavigationManager nav)
    {
        _metro = metro; _store = store; _nav = nav;
        _metro.Finished += OnMetroFinished;
        _metro.Changed += OnMetroChanged;
    }

    public PracticePlan? Plan { get; private set; }
    public int Index { get; private set; }
    public StepState State { get; private set; }
    public bool Active => Plan != null;
    public PlanStep? Current => Plan != null && Index < Plan.Steps.Count ? Plan.Steps[Index] : null;
    public PlanStep? NextStep => Plan != null && Index + 1 < Plan.Steps.Count ? Plan.Steps[Index + 1] : null;
    public bool IsLast => Plan != null && Index >= Plan.Steps.Count - 1;
    /// <summary>Step ids that were logged (or completed, for rests) in this run.</summary>
    public HashSet<string> Completed { get; } = new();

    public event Action? Changed;
    /// <summary>An item step finished on its own; the handler should log it and then call <see cref="AdvanceAsync"/>.</summary>
    public event Func<PlanStep, Task>? StepFinished;

    public string Describe(PlanStep? s)
    {
        if (s == null) return "";
        if (s.Kind == PlanStepKind.Rest) return "Rest";
        var item = _store.GetItem(s.ItemId);
        var part = item?.Sections.FirstOrDefault(x => x.Id == s.SectionId)?.Name;
        return (item?.Name ?? "Missing item") + (part != null ? $" · {part}" : "");
    }

    public async Task StartAsync(PracticePlan plan)
    {
        if (plan.Steps.Count == 0) return;
        await _metro.StopAsync();
        Plan = plan; Index = 0; Completed.Clear();
        plan.LastRun = DateTime.Now;
        await _store.SavePlanAsync(plan);
        await LoadStepAsync();
    }

    public async Task GoToAsync(int index)
    {
        if (Plan == null) return;
        await _metro.StopAsync();
        Index = Math.Clamp(index, 0, Plan.Steps.Count - 1);
        await LoadStepAsync();
    }

    public Task SkipAsync() => IsLast ? EndAsync() : GoToAsync(Index + 1);
    public Task BackAsync() => GoToAsync(Math.Max(0, Index - 1));

    /// <summary>Called after the finished step was logged (or not): move to the next step, or end.</summary>
    public async Task AdvanceAsync()
    {
        if (Plan == null) return;
        if (Current != null) Completed.Add(Current.Id);
        if (IsLast) { await EndAsync(finished: true); return; }
        Index++;
        await LoadStepAsync();
    }

    public bool FinishedAll { get; private set; }

    public async Task EndAsync(bool finished = false)
    {
        await _metro.StopAsync();
        await _metro.ClearStepIntervalsAsync();
        FinishedAll = finished;
        Plan = null;
        Changed?.Invoke();
    }

    public void DismissSummary() { FinishedAll = false; Changed?.Invoke(); }

    /// <summary>Start (or restart) the current step's clock.</summary>
    public async Task StartStepAsync()
    {
        var s = Current;
        if (s == null) return;
        if (s.Kind == PlanStepKind.Rest) await _metro.StartRestAsync(s.Minutes);
        else if (s.Mode == StepMode.Timer) await _metro.StartTimerAsync();
        else await _metro.StartAsync();
        State = StepState.Running;
        Changed?.Invoke();
    }

    private async Task LoadStepAsync()
    {
        var s = Current;
        if (s == null) { await EndAsync(true); return; }
        State = StepState.Ready;
        FinishedAll = false;
        if (s.Kind == PlanStepKind.Rest)
        {
            await _metro.ClearStepIntervalsAsync();
            _nav.NavigateTo("timer");
            Changed?.Invoke();
            await StartStepAsync(); // rests start straight away
            return;
        }
        var item = _store.GetItem(s.ItemId);
        if (item == null) { Changed?.Invoke(); return; } // item deleted: user can skip
        await _metro.LoadItemAsync(item, s.SectionId);
        _metro.ResetSession();
        await _metro.SetStepIntervalsAsync(s.Minutes, s.RestMin, s.Rounds);
        if (s.Mode == StepMode.Metronome)
            await _metro.SetStepTempoAsync(s.StartTempo, new TrainingSettings
            {
                Enabled = s.Trainer, Start = s.TrainerStart, End = s.TrainerEnd,
                Mode = RampMode.OverTime, DurationMin = s.WorkMinutes, StopAtEnd = false,
            });
        var url = s.Mode == StepMode.Timer ? $"timer/item/{item.Id}"
            : s.SectionId != null ? $"metronome/item/{item.Id}/{s.SectionId}" : $"metronome/item/{item.Id}";
        _nav.NavigateTo(url);
        Changed?.Invoke();
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
