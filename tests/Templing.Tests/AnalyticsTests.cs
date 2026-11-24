using Microsoft.AspNetCore.Components;

namespace Templing.Tests;

/// <summary>The store and plan runner events <see cref="Analytics"/> sends to Umami, and what a new piece reports as its source.</summary>
public class AnalyticsTests
{
    sealed class FakeNav : NavigationManager
    {
        public FakeNav() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }

    [Fact]
    public async Task Only_new_pieces_and_logs_are_reported()
    {
        var (store, _) = await FakeJs.StoreAsync();
        var pieces = new List<Piece>(); var logs = new List<PracticeLog>();
        store.PieceAdded += pieces.Add;
        store.LogAdded += logs.Add;

        var piece = new Piece { Name = "A" };
        await store.SavePieceAsync(piece);
        piece.Name = "B";
        await store.SavePieceAsync(piece);
        var log = new PracticeLog { PieceId = piece.Id };
        await store.SaveLogAsync(log);
        log.DurationSec = 60;
        await store.SaveLogAsync(log);
        await store.ImportAsync([new Piece()], [new PracticeLog()], [], []);

        Assert.Equal([piece], pieces);
        Assert.Equal([log], logs);
    }

    [Fact]
    public async Task A_plan_run_reports_starting_and_finishing_but_not_ending_early()
    {
        var plan = new PracticePlan { Id = "p", Steps = [new PlanStep { Id = "s", Kind = PlanStepKind.Rest, Minutes = 1 }] };
        var (store, js) = await FakeJs.StoreAsync(db => db.Seed("plans", plan));
        var metro = new MetronomeService(js, store);
        await metro.InitAsync();
        var runner = new PlanRunner(metro, store, new FakeNav());
        var events = new List<string>();
        runner.RunStarted += () => events.Add("started");
        runner.RunFinished += () => events.Add("finished");

        await runner.StartAsync(store.GetPlan("p")!);
        await runner.EndAsync();
        await runner.StartAsync(store.GetPlan("p")!);
        await runner.EndAsync(finished: true);

        Assert.Equal(["started", "started", "finished"], events);
    }

    [Theory]
    [InlineData(null, null, "manual")]
    [InlineData("f", "Song.gp5", "guitar-pro")]
    [InlineData("f", "Song.GPX", "guitar-pro")]
    [InlineData("f", "Song.gp", "guitar-pro")]
    [InlineData("f", "Song.musicxml", "musicxml")]
    [InlineData("f", "Song.mxl", "musicxml")]
    [InlineData("f", "Song.xml", "musicxml")]
    [InlineData("f", "Song", "score")]
    public void A_new_piece_reports_where_it_came_from(string? fileId, string? fileName, string source) =>
        Assert.Equal(source, Analytics.Source(new Piece { ScoreFileId = fileId, ScoreFileName = fileName }));
}
