using System.Text.Json;

namespace Templing.Tests;

public class MetronomeServiceTests
{
    static async Task<(MetronomeService metro, FakeJs js)> Init(Piece piece, params PracticeLog[] logs)
    {
        foreach (var l in logs) l.PieceId = piece.Id;
        var (store, js) = await FakeJs.StoreAsync(db => { db.Seed("pieces", piece); db.Seed("logs", logs); });
        var metro = new MetronomeService(js, store);
        await metro.InitAsync();
        return (metro, js);
    }

    /// <summary>The last config sent to metronome.js.</summary>
    static JsonElement Config(FakeJs js) =>
        JsonDocument.Parse((string)js.Modules["./js/metronome.js"].Calls.Last(c => c.fn == "configure").args[0]!).RootElement;

    [Fact]
    public async Task A_piece_opens_at_its_Start_at()
    {
        var piece = new Piece { StartTempo = 70, Sections = [new Section { Bpm = 140 }] };
        var (metro, _) = await Init(piece, new PracticeLog { Value = 120, ValueMin = 100 });
        await metro.LoadPieceAsync(piece);
        Assert.Equal(70, metro.Value);
    }

    [Fact]
    public async Task Without_Start_at_a_piece_opens_at_the_low_end_of_the_last_range()
    {
        var piece = new Piece { Sections = [new Section { Bpm = 140 }] };
        var (metro, _) = await Init(piece,
            new PracticeLog { Date = new(2026, 1, 1), Value = 130, ValueMin = 110 },
            new PracticeLog { Date = new(2026, 1, 2), Value = 120, ValueMin = 95 });
        await metro.LoadPieceAsync(piece);
        Assert.Equal(95, metro.Value);
    }

    [Fact]
    public async Task Then_at_the_last_tempo_then_the_section_tempo()
    {
        var piece = new Piece { Sections = [new Section { Bpm = 140 }] };
        var (metro, _) = await Init(piece, new PracticeLog { Value = 118 });
        await metro.LoadPieceAsync(piece);
        Assert.Equal(118, metro.Value);

        var fresh = new Piece { Sections = [new Section { Bpm = 140 }] };
        (metro, _) = await Init(fresh);
        await metro.LoadPieceAsync(fresh);
        Assert.Equal(140, metro.Value);

        var percent = new Piece { Unit = TempoUnit.Percent, Sections = [new Section { Bpm = 140 }] };
        (metro, _) = await Init(percent);
        await metro.LoadPieceAsync(percent);
        Assert.Equal(80, metro.Value);
    }

    [Fact]
    public async Task A_pieces_trainer_ramps_from_where_it_opens_to_its_goal()
    {
        var piece = new Piece { Goal = 130, StartTempo = 90, Sections = [new Section { Bpm = 100 }] };
        var (metro, _) = await Init(piece);
        await metro.LoadPieceAsync(piece);
        Assert.Equal(90, metro.Training.Start);
        Assert.Equal(130, metro.Training.End);
    }

    [Fact]
    public async Task The_engine_gets_tempos_as_factors_of_the_section_tempo()
    {
        var piece = new Piece
        {
            StartTempo = 80,
            Sections = [new Section { Bpm = 100, BeatsPerBar = 7, BeatUnit = 8, Subdivision = 2, Bars = 4 }, new Section { Bpm = 160 }],
            PlayThrough = true,
        };
        var (metro, js) = await Init(piece);
        await metro.LoadPieceAsync(piece);

        var cfg = Config(js);
        Assert.Equal(0.8, cfg.GetProperty("factor").GetDouble(), 9);
        Assert.True(cfg.GetProperty("playThrough").GetBoolean());
        var step = cfg.GetProperty("steps")[0];
        Assert.Equal(100, step.GetProperty("bpm").GetDouble());
        Assert.Equal(7, step.GetProperty("beatsPerBar").GetInt32());
        Assert.Equal(8, step.GetProperty("beatUnit").GetInt32());
        Assert.Equal(2, step.GetProperty("subdivision").GetInt32());
        Assert.Equal(4, step.GetProperty("bars").GetInt32());
        Assert.Equal(ClickSounds.Default, cfg.GetProperty("sound").GetString());
        var tr = cfg.GetProperty("training");
        Assert.Equal("time", tr.GetProperty("mode").GetString());
        var levels = tr.GetProperty("levels").EnumerateArray().Select(e => e.GetDouble()).ToList();
        Assert.Equal(0.8, levels[0], 9);
        Assert.Equal(tr.GetProperty("endFactor").GetDouble(), levels[^1], 9);
    }

    [Fact]
    public async Task Muting_sends_zero_volume()
    {
        var piece = new Piece();
        var (metro, js) = await Init(piece);
        await metro.SetMutedAsync(true);
        Assert.Equal(0, Config(js).GetProperty("volume").GetDouble());
    }
}
