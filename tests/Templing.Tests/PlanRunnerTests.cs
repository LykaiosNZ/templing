using Microsoft.AspNetCore.Components;

namespace Templing.Tests;

public class PlanRunnerTests
{
    sealed class FakeNav : NavigationManager
    {
        public FakeNav() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }

    static readonly Piece PieceA = new() { Id = "a", Name = "A" }, PieceB = new() { Id = "b", Name = "B" };

    static PracticePlan NewPlan() => new()
    {
        Id = "p", Name = "Routine",
        Steps =
        [
            new PlanStep { Id = "s1", PieceId = "a", Minutes = 5 },
            new PlanStep { Id = "s2", Kind = PlanStepKind.Rest, Minutes = 2 },
            new PlanStep { Id = "s3", PieceId = "b", Minutes = 10 },
        ],
    };

    static async Task<(PlanRunner runner, DataStore store, FakeJs js, FakeNav nav)> Init()
    {
        var (store, js) = await FakeJs.StoreAsync(db => { db.Seed("pieces", PieceA, PieceB); db.Seed("plans", NewPlan()); });
        var metro = new MetronomeService(js, store);
        await metro.InitAsync();
        var nav = new FakeNav();
        var runner = new PlanRunner(metro, store, nav);
        await runner.StartAsync(store.GetPlan("p")!);
        return (runner, store, js, nav);
    }

    /// <summary>A copy of the run's plan, as the plan dialog edits it.</summary>
    static PracticePlan Copy(PracticePlan p) =>
        System.Text.Json.JsonSerializer.Deserialize<PracticePlan>(System.Text.Json.JsonSerializer.Serialize(p, DataStore.Json), DataStore.Json)!;

    [Fact]
    public async Task A_session_only_edit_changes_the_run_but_not_the_saved_plan()
    {
        var (runner, store, js, _) = await Init();
        var edited = Copy(runner.Plan!);
        edited.Steps[2].Minutes = 20;

        Assert.False(runner.ChangesCurrentStep(edited));
        await runner.ApplyEditAsync(edited, savePlan: false);

        Assert.True(runner.EditedForSession);
        Assert.Equal(20, runner.Plan!.Steps[2].Minutes);
        Assert.Equal(10, store.GetPlan("p")!.Steps[2].Minutes);
        Assert.Equal(10, js.Db.Row<PracticePlan>("plans", "p")!.Steps[2].Minutes);
    }

    [Fact]
    public async Task A_session_only_edit_survives_a_refresh()
    {
        var (runner, store, js, _) = await Init();
        var edited = Copy(runner.Plan!);
        edited.Steps.RemoveAt(1);
        await runner.ApplyEditAsync(edited, savePlan: false);

        var metro = new MetronomeService(js, store);
        await metro.InitAsync();
        var restored = new PlanRunner(metro, store, new FakeNav());
        await restored.RestoreAsync();

        Assert.True(restored.EditedForSession);
        Assert.Equal(["s1", "s3"], restored.Plan!.Steps.Select(s => s.Id));
        Assert.Equal(3, store.GetPlan("p")!.Steps.Count);
    }

    [Fact]
    public async Task Saving_to_the_plan_keeps_its_star()
    {
        var (runner, store, js, _) = await Init();
        var edited = Copy(runner.Plan!);
        store.GetPlan("p")!.Favourite = true; // starred on the Plans page while the dialog was open
        edited.Name = "Morning";

        await runner.ApplyEditAsync(edited, savePlan: true);

        Assert.False(runner.EditedForSession);
        Assert.Equal("Morning", js.Db.Row<PracticePlan>("plans", "p")!.Name);
        Assert.True(store.GetPlan("p")!.Favourite);
    }

    [Fact]
    public async Task The_run_follows_the_current_step_when_steps_move()
    {
        var (runner, _, _, _) = await Init();
        await runner.GoToAsync(1); // the rest
        var edited = Copy(runner.Plan!);
        edited.Steps.Insert(0, new PlanStep { Id = "s0", PieceId = "b" });

        await runner.ApplyEditAsync(edited, savePlan: false);

        Assert.Equal(2, runner.Index);
        Assert.Equal("s2", runner.Current!.Id);
    }

    [Fact]
    public async Task Removing_the_current_step_moves_on_to_the_next_one_left()
    {
        var (runner, _, _, nav) = await Init();
        var edited = Copy(runner.Plan!);
        edited.Steps.RemoveAt(0);

        Assert.True(runner.ChangesCurrentStep(edited));
        await runner.ApplyEditAsync(edited, savePlan: false);

        Assert.Equal("s2", runner.Current!.Id);
        Assert.Equal(0, runner.Index);
        Assert.Equal("http://localhost/timer", nav.Uri); // was on the removed step's page: goes to the new one
    }

    [Fact]
    public async Task Changing_the_current_step_counts_as_a_change_to_it()
    {
        var (runner, _, _, _) = await Init();
        var edited = Copy(runner.Plan!);
        Assert.False(runner.ChangesCurrentStep(edited));
        edited.Steps[0].StartTempo = 90;
        Assert.True(runner.ChangesCurrentStep(edited));
    }

    [Theory]
    [InlineData(false, StepState.Ready)]
    [InlineData(true, StepState.Running)]
    public async Task The_next_piece_step_starts_by_itself_only_with_the_setting_on(bool autoStart, StepState expected)
    {
        var (runner, store, _, nav) = await Init();
        store.Settings.AutoStartPlanSteps = autoStart;
        await runner.GoToAsync(1); // the rest, which starts by itself either way
        Assert.Equal(StepState.Running, runner.State);

        await runner.AdvanceAsync(); // the rest ran out

        Assert.Equal("s3", runner.Current!.Id);
        Assert.Equal(expected, runner.State);
        Assert.Equal("http://localhost/metronome/piece/b", nav.Uri);
    }

    [Fact]
    public async Task Skipping_to_a_piece_step_waits_for_start_even_with_auto_start_on()
    {
        var (runner, store, _, _) = await Init();
        store.Settings.AutoStartPlanSteps = true;
        await runner.GoToAsync(2);
        Assert.Equal(StepState.Ready, runner.State);
    }
}
