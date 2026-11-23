using System.Text.Json;
using System.Text.Json.Serialization;

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
        var now = DateTimeOffset.Now;
        var json = Backup.Export(source, now);

        var (target, _) = await FakeJs.StoreAsync();
        await Backup.RestoreAsync(target, Backup.Parse(json), includeSettings: true);

        Assert.Equal(json, Backup.Export(target, now));
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

    [Fact]
    public void A_newer_backup_is_refused_naming_the_app_version_that_made_it()
    {
        var e = Assert.Throws<FormatException>(() =>
            Backup.Parse($$"""{"Format":{{Backup.Format + 1}},"AppVersion":"9.1.0+abcdef0123","Pieces":[]}"""));
        Assert.Equal("This backup was made by Templing 9.1.0, newer than this one. Reload Templing to update it, then try again.", e.Message);
    }

    [Fact]
    public void A_backup_older_than_the_oldest_supported_format_is_refused()
    {
        var e = Assert.Throws<FormatException>(() => Backup.Parse("""{"Version":2,"Items":[]}"""));
        Assert.Contains("early test version", e.Message);
    }

    [Theory]
    [InlineData("""{"name":"templing","version":"1.0.0"}""")]
    [InlineData("""{"Version":"3"}""")]
    [InlineData("""[1, 2, 3]""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public void A_file_that_is_not_a_backup_says_so(string json)
    {
        var e = Assert.Throws<FormatException>(() => Backup.Parse(json));
        Assert.Equal("That file isn't a Templing backup.", e.Message);
    }

    [Fact]
    public void A_damaged_backup_says_so()
    {
        var e = Assert.Throws<FormatException>(() => Backup.Parse($$"""{"Format":{{Backup.Format}},"Pieces":"oops"}"""));
        Assert.Equal("That backup couldn't be read. The file may be damaged.", e.Message);
    }

    [Fact]
    public void A_backup_without_newer_fields_still_loads()
    {
        var b = Backup.Parse("""{"Version":3,"Pieces":[{"Name":"Old"}],"Logs":null,"Presets":null,"Settings":null}""");
        Assert.Null(b.Plans);
        Assert.Equal(Backup.Format, b.Format);
        Assert.Null(Backup.Origin(b));
        Assert.Equal("1 piece and 0 log entries", Backup.Describe(b, withSettings: false));
    }

    [Fact]
    public async Task A_backup_records_the_app_version_and_when_it_was_made()
    {
        var made = new DateTimeOffset(2025, 3, 14, 9, 0, 0, TimeSpan.Zero);
        var b = Backup.Parse(Backup.Export(await Filled(), made));
        Assert.Equal(AppInfo.InformationalVersion, b.AppVersion);
        Assert.Equal(made, b.Exported);
        Assert.Equal($"Made by Templing {AppInfo.Version} on {Stats.Day(made.LocalDateTime)}", Backup.Origin(b));
    }

    [Fact]
    public void Describe_lists_what_is_there()
    {
        var b = new Backup.BackupFile(Backup.Format, null, null, [new(), new()], [new()], [], new AppSettings(), []);
        Assert.Equal("2 pieces, 1 log entry and settings", Backup.Describe(b, withSettings: true));
    }

    // ---- old backups keep restoring ----


    static string SamplePath(int format) => Path.Combine(AppContext.BaseDirectory, "Backups", $"format-{format}.json");

    public static TheoryData<int> SupportedFormats() => new(Enumerable.Range(Backup.OldestSupported, Backup.Format - Backup.OldestSupported + 1));

    /// <summary>Bumping <see cref="Backup.Format"/> needs a migration from the previous format.</summary>
    [Fact]
    public void Every_older_format_has_a_migration()
    {
        for (var f = Backup.OldestSupported; f < Backup.Format; f++)
            Assert.True(Backup.Migrations.ContainsKey(f), $"No migration from backup format {f} to {f + 1}");
    }

    /// <summary>
    /// Each format has a sample backup in Backups/format-N.json: the same data (see <see cref="Filled"/>), as that
    /// format wrote it. Add one when you bump the format (export <see cref="Filled"/> with the new code) and never
    /// edit the old ones: they stand for backups people have.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public async Task A_backup_in_every_supported_format_restores(int format)
    {
        var path = SamplePath(format);
        Assert.True(File.Exists(path), $"No sample backup for format {format} (tests/Templing.Tests/Backups/format-{format}.json)");

        var (store, _) = await FakeJs.StoreAsync();
        await Backup.RestoreAsync(store, Backup.Parse(File.ReadAllText(path)), includeSettings: true);

        var piece = Assert.Single(store.Pieces);
        Assert.Equal("Spain", piece.Name);
        Assert.Equal(140, piece.Goal);
        var section = Assert.Single(piece.Sections);
        Assert.Equal(("Intro", 90), (section.Name, section.Bpm));
        Assert.Equal([[0, 4, 4], [1, 3, 4]], section.ScoreBars!);

        var log = Assert.Single(store.Logs);
        Assert.Equal((piece.Id, "Spain", 100, 90, 2), (log.PieceId, log.PieceName, log.Value, log.ValueMin, log.Effort));

        Assert.Equal("Odd", Assert.Single(store.Presets).Name);
        var step = Assert.Single(Assert.Single(store.Plans).Steps);
        Assert.Equal(piece.Id, step.PieceId);
        Assert.True(step.Trainer);

        Assert.Equal(99, store.Settings.WeeklyGoalMin);
        Assert.False(store.Settings.DarkMode);
    }

    /// <summary>
    /// Nothing in an old backup may be silently dropped: after migrating, every field in a sample has to be one the
    /// current model reads. Renaming or removing a field without a migration fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public void Every_field_in_an_old_backup_is_still_read(int format)
    {
        var root = Backup.Migrate(File.ReadAllText(SamplePath(format)));
        var strict = new JsonSerializerOptions(DataStore.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        root.Deserialize<Backup.BackupFile>(strict);
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
