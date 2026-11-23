namespace Templing.Tests;

public class DataStoreTests
{
    [Fact]
    public async Task Loads_every_store_with_pieces_sorted_by_name()
    {
        var (store, _) = await FakeJs.StoreAsync(db =>
        {
            db.Seed("pieces", new Piece { Name = "beta" }, new Piece { Name = "Alpha" });
            db.Seed("logs", new PracticeLog());
            db.Seed("presets", new MetronomePreset());
            db.Seed("plans", new PracticePlan());
            db.Seed("settings", new AppSettings { WeeklyGoalMin = 42 });
        });
        Assert.True(store.Loaded);
        Assert.Equal(["Alpha", "beta"], store.Pieces.Select(p => p.Name));
        Assert.Single(store.Logs);
        Assert.Single(store.Presets);
        Assert.Single(store.Plans);
        Assert.Equal(42, store.Settings.WeeklyGoalMin);
    }

    [Fact]
    public async Task An_empty_database_gets_default_settings()
    {
        var (store, _) = await FakeJs.StoreAsync();
        Assert.Equal("app", store.Settings.Id);
        Assert.Empty(store.Pieces);
    }

    [Fact]
    public async Task Old_settings_load_with_the_volume_clamped()
    {
        var (store, _) = await FakeJs.StoreAsync(db => db.Seed("settings", new { Id = "app", Metronome = new { Volume = 1.5 } }));
        Assert.Equal(1, store.Settings.Metronome.Volume);
    }

    [Fact]
    public async Task Renaming_a_piece_renames_its_logs_in_the_database()
    {
        var piece = new Piece { Name = "Old" };
        var log = new PracticeLog { PieceId = piece.Id, PieceName = "Old" };
        var (store, js) = await FakeJs.StoreAsync(db => { db.Seed("pieces", piece); db.Seed("logs", log); });
        var changed = 0;
        store.Changed += () => changed++;

        var renamed = store.GetPiece(piece.Id)!;
        renamed.Name = "New";
        await store.SavePieceAsync(renamed);

        Assert.Equal("New", js.Db.Row<PracticeLog>("logs", log.Id)!.PieceName);
        Assert.Equal("New", js.Db.Row<Piece>("pieces", piece.Id)!.Name);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Deleting_a_piece_can_keep_its_logs_unlinked()
    {
        var piece = new Piece { Name = "P" };
        var log = new PracticeLog { PieceId = piece.Id, PieceName = "P" };
        var (store, js) = await FakeJs.StoreAsync(db => { db.Seed("pieces", piece); db.Seed("logs", log); });

        await store.DeletePieceAsync(store.Pieces[0], deleteLogs: false);

        Assert.Empty(store.Pieces);
        Assert.Equal(0, js.Db.Count("pieces"));
        var kept = js.Db.Row<PracticeLog>("logs", log.Id)!;
        Assert.Null(kept.PieceId);
        Assert.Equal("P", kept.PieceName);
    }

    [Fact]
    public async Task Deleting_a_piece_can_delete_its_logs()
    {
        var piece = new Piece();
        var (store, js) = await FakeJs.StoreAsync(db =>
        {
            db.Seed("pieces", piece);
            db.Seed("logs", new PracticeLog { PieceId = piece.Id }, new PracticeLog { PieceId = "other" });
        });
        await store.DeletePieceAsync(store.Pieces[0], deleteLogs: true);
        Assert.Single(store.Logs);
        Assert.Equal(1, js.Db.Count("logs"));
    }

    [Fact]
    public async Task A_score_file_is_deleted_with_the_last_piece_using_it()
    {
        var a = new Piece { ScoreFileId = "f1" };
        var b = new Piece { ScoreFileId = "f1" };
        var (store, js) = await FakeJs.StoreAsync(db => { db.Seed("pieces", a, b); db.Seed("files", new { Id = "f1" }); });

        await store.DeletePieceAsync(store.GetPiece(a.Id)!, deleteLogs: true);
        Assert.Equal(1, js.Db.Count("files"));
        await store.DeletePieceAsync(store.GetPiece(b.Id)!, deleteLogs: true);
        Assert.Equal(0, js.Db.Count("files"));
    }

    [Fact]
    public async Task Editing_a_log_keeps_when_it_was_first_logged()
    {
        var first = new DateTime(2026, 1, 1, 9, 0, 0);
        var log = new PracticeLog { Logged = first };
        var (store, _) = await FakeJs.StoreAsync(db => db.Seed("logs", log));

        await store.SaveLogAsync(new PracticeLog { Id = log.Id, Value = 100 });
        Assert.Equal(first, store.Logs.Single().Logged);

        var fresh = new PracticeLog();
        await store.SaveLogAsync(fresh);
        Assert.NotNull(fresh.Logged);
    }

    [Fact]
    public async Task Import_replaces_by_id_and_adds_the_rest()
    {
        var piece = new Piece { Name = "Mine" };
        var (store, js) = await FakeJs.StoreAsync(db => db.Seed("pieces", piece));

        await store.ImportAsync([new Piece { Id = piece.Id, Name = "Theirs" }, new Piece { Name = "Another" }], [new PracticeLog()],
            [new MetronomePreset()], [new PracticePlan()]);

        Assert.Equal(["Another", "Theirs"], store.Pieces.Select(p => p.Name));
        Assert.Equal(2, js.Db.Count("pieces"));
        Assert.Equal(1, js.Db.Count("logs"));
        Assert.Equal(1, js.Db.Count("presets"));
        Assert.Equal(1, js.Db.Count("plans"));
    }

    [Fact]
    public async Task Delete_all_clears_every_store_but_settings()
    {
        var (store, js) = await FakeJs.StoreAsync(db =>
        {
            db.Seed("pieces", new Piece());
            db.Seed("logs", new PracticeLog());
            db.Seed("presets", new MetronomePreset());
            db.Seed("plans", new PracticePlan());
            db.Seed("files", new { Id = "f" });
            db.Seed("session", new { Id = "planrun" });
            db.Seed("settings", new AppSettings());
        });
        await store.ClearAllAsync();
        Assert.Empty(store.Pieces); Assert.Empty(store.Logs); Assert.Empty(store.Presets); Assert.Empty(store.Plans);
        foreach (var s in new[] { "pieces", "logs", "presets", "plans", "files", "session" }) Assert.Equal(0, js.Db.Count(s));
        Assert.Equal(1, js.Db.Count("settings"));
    }

    [Fact]
    public async Task Pickers_list_recently_logged_pieces_first()
    {
        var never = new Piece { Name = "A never" };
        var old = new Piece { Name = "B old" };
        var recent = new Piece { Name = "C recent" };
        var archived = new Piece { Name = "D archived", Archived = true };
        var (store, _) = await FakeJs.StoreAsync(db =>
        {
            db.Seed("pieces", never, old, recent, archived);
            db.Seed("logs",
                new PracticeLog { PieceId = old.Id, Date = new(2026, 1, 1) },
                new PracticeLog { PieceId = recent.Id, Date = new(2026, 2, 1) },
                new PracticeLog { PieceId = archived.Id, Date = new(2026, 3, 1) });
        });
        Assert.Equal(["C recent", "B old", "A never"], store.PiecesByRecent().Select(p => p.Name));
        Assert.Equal("D archived", store.PiecesByRecent(keepId: archived.Id)[0].Name);
    }

    [Fact]
    public async Task FindPieceByName_ignores_case_and_spaces()
    {
        var (store, _) = await FakeJs.StoreAsync(db => db.Seed("pieces", new Piece { Name = "Spain " }));
        Assert.NotNull(store.FindPieceByName("  spain"));
        Assert.Null(store.FindPieceByName("Spai"));
    }
}
