namespace Templing.Tests;

// The demo build's tests run with -p:Demo=true (build.yml runs the tests both ways); the normal build has no demo to test.
#if DEMO_MODE
public class DemoDataTests
{
    static readonly DateTime Today = new(2026, 11, 24);

    [Fact]
    public async Task The_demo_keeps_to_memory_and_starts_with_the_sample_data()
    {
        var (store, js) = await FakeJs.StoreAsync();

        Assert.True(store.Demo);
        Assert.True(js.Db.UsedMemory);
        Assert.Equal("useMemory", js.Db.Calls[0].fn);   // before anything is read
        Assert.Contains(store.Pieces, p => p.Name == "Spain");
        Assert.NotEmpty(store.Logs);
        Assert.NotEmpty(store.Plans);
        Assert.NotEmpty(store.Presets);
        Assert.Equal(240, store.Settings.WeeklyGoalMin);
    }

    [Fact]
    public void The_sample_is_the_same_every_time_and_ends_today()
    {
        var a = DemoData.Create(Today);
        var b = DemoData.Create(Today.AddHours(15));

        Assert.Equal(a.Logs.Select(l => (l.Id, l.Value, l.Date)), b.Logs.Select(l => (l.Id, l.Value, l.Date)));
        Assert.Equal(Today, a.Logs.Max(l => l.Date));
        Assert.All(a.Logs, l => Assert.True(l.Date <= Today));
        Assert.Equal(a.Logs.Count, a.Logs.Select(l => l.Id).Distinct().Count());
        Assert.True(Stats.Streaks(a.Logs, Today).current >= 10);
    }

    [Fact]
    public void Every_reference_in_the_sample_points_at_something_there()
    {
        var d = DemoData.Create(Today);
        var pieces = d.Pieces.ToDictionary(p => p.Id);

        foreach (var log in d.Logs.Where(l => l.PieceId != null))
        {
            var piece = pieces[log.PieceId!];
            Assert.Equal(piece.Name, log.PieceName);
            Assert.Equal(piece.Unit, log.Unit);
            if (log.SectionId != null) Assert.Contains(piece.Sections, s => s.Id == log.SectionId);
        }
        foreach (var step in d.Plans.SelectMany(p => p.Steps).Where(s => s.Kind == PlanStepKind.Piece))
        {
            var piece = pieces[step.PieceId!];
            if (step.SectionId != null) Assert.Contains(piece.Sections, s => s.Id == step.SectionId);
        }
        Assert.All(d.Pieces, p => Assert.Equal(p.Sections.Count, p.Sections.Select(s => s.Id).Distinct().Count()));
        Assert.All(d.Pieces.Where(p => p.GroupId != null), p => Assert.Contains(d.Groups, g => g.Id == p.GroupId));
        Assert.Contains(d.Pieces, p => p.GroupId == null);   // the Library shows "No group" too
        Assert.All(d.Logs.Where(l => l.Value != null), l => Assert.InRange(l.Value!.Value, TempoLimits.Min(l.Unit), TempoLimits.Max(l.Unit)));
    }

    [Fact]
    public async Task The_sample_survives_a_backup_and_restore()
    {
        var (demo, _) = await FakeJs.StoreAsync();
        var now = DateTimeOffset.Now;
        var json = Backup.Export(demo, now);

        var (target, _) = await FakeJs.StoreAsync();
        await Backup.RestoreAsync(target, Backup.Parse(json), includeSettings: true);

        Assert.Equal(json, Backup.Export(target, now));
    }
}
#else
public class NoDemoTests
{
    [Fact]
    public async Task The_normal_build_has_no_demo()
    {
        var (store, js) = await FakeJs.StoreAsync();
        Assert.False(AppInfo.Demo);
        Assert.False(store.Demo);
        Assert.False(js.Db.UsedMemory);
        Assert.Empty(store.Pieces);
        Assert.Null(typeof(AppInfo).Assembly.GetType("Templing.Services.DemoData"));   // the sample data isn't built in
    }
}
#endif
