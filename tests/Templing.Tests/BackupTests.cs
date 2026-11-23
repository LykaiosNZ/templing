using System.Text.Json;

namespace Templing.Tests;

public class BackupTests
{
    static async Task<DataStore> Filled()
    {
        var piece = new Piece { Name = "Spain", Goal = 140, Sections = [new Section { Name = "Intro", Bpm = 90, ScoreBars = [[0, 4, 4], [1, 3, 4]] }] };
        var (store, _) = await FakeJs.StoreAsync(db =>
        {
            db.Seed("pieces", piece);
            db.Seed("logs", new PracticeLog { PieceId = piece.Id, PieceName = piece.Name, Value = 100, ValueMin = 90, Effort = 2 });
            db.Seed("presets", new MetronomePreset { Name = "Odd" });
            db.Seed("plans", new PracticePlan { Steps = [new PlanStep { PieceId = piece.Id, Trainer = true }] });
            db.Seed("settings", new AppSettings { WeeklyGoalMin = 99, DarkMode = false });
        });
        return store;
    }

    [Fact]
    public async Task A_backup_restores_into_an_empty_store_unchanged()
    {
        var source = await Filled();
        var json = Backup.Export(source);

        var (target, _) = await FakeJs.StoreAsync();
        await Backup.RestoreAsync(target, Backup.Parse(json), includeSettings: true);

        Assert.Equal(json, Backup.Export(target));
    }

    [Fact]
    public async Task Settings_are_only_restored_when_asked()
    {
        var b = Backup.Parse(Backup.Export(await Filled()));
        var (target, _) = await FakeJs.StoreAsync();

        var summary = await Backup.RestoreAsync(target, b, includeSettings: false);

        Assert.Equal(150, target.Settings.WeeklyGoalMin);
        Assert.Single(target.Pieces);
        Assert.Equal("Restored 1 piece, 1 log entry, 1 plan and 1 custom metronome", summary);
    }

    [Fact]
    public async Task Restored_settings_keep_the_app_settings_id()
    {
        var b = Backup.Parse(Backup.Export(await Filled())) with { Settings = new AppSettings { Id = "something-else", WeeklyGoalMin = 5 } };
        var (target, js) = await FakeJs.StoreAsync();
        await Backup.RestoreAsync(target, b, includeSettings: true);
        Assert.Equal(5, js.Db.Row<AppSettings>("settings", "app")!.WeeklyGoalMin);
    }

    [Theory]
    [InlineData(Backup.Version + 1, "newer version")]
    [InlineData(2, "older version")]
    public void Backups_from_other_versions_are_refused(int version, string message)
    {
        var e = Assert.Throws<FormatException>(() => Backup.Parse($$"""{"Version":{{version}},"Pieces":[]}"""));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void A_backup_without_newer_fields_still_loads()
    {
        var b = Backup.Parse("""{"Version":3,"Pieces":[{"Name":"Old"}],"Logs":null,"Presets":null,"Settings":null}""");
        Assert.Null(b.Plans);
        Assert.Equal("1 piece and 0 log entries", Backup.Describe(b, withSettings: false));
    }

    [Fact]
    public void Describe_lists_what_is_there()
    {
        var b = new Backup.BackupFile(3, [new(), new()], [new()], [], new AppSettings(), []);
        Assert.Equal("2 pieces, 1 log entry and settings", Backup.Describe(b, withSettings: true));
    }

    /// <summary>
    /// The "Persisted data" checklist in CLAUDE.md: every list DataStore keeps must be in the backup, or it's lost on a
    /// device move. A new list on DataStore without a BackupFile field of the same name fails here.
    /// </summary>
    [Fact]
    public void Every_list_in_the_store_is_backed_up()
    {
        var stored = typeof(DataStore).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .Select(p => (p.Name, p.PropertyType)).ToList();
        var backedUp = typeof(Backup.BackupFile).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);

        Assert.NotEmpty(stored);
        Assert.All(stored, s =>
        {
            Assert.True(backedUp.TryGetValue(s.Name, out var t), $"DataStore.{s.Name} is not in Backup.BackupFile");
            Assert.Equal(s.PropertyType, t);
        });
        Assert.Equal(typeof(AppSettings), backedUp[nameof(DataStore.Settings)]);
    }

    [Fact]
    public async Task Backups_use_PascalCase_names_and_string_enums()
    {
        var json = Backup.Export(await Filled());
        using var doc = JsonDocument.Parse(json);
        var piece = doc.RootElement.GetProperty("Pieces")[0];
        Assert.Equal("Spain", piece.GetProperty("Name").GetString());
        Assert.Equal(JsonValueKind.String, piece.GetProperty("Type").ValueKind);
    }
}
