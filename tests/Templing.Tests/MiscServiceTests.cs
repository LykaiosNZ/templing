using System.Globalization;

namespace Templing.Tests;

public class LogCsvTests
{
    [Theory]
    [InlineData("70 - 90", 70.0, 90.0, TempoUnit.Bpm)]
    [InlineData("65- 145", 65.0, 145.0, TempoUnit.Bpm)]
    [InlineData("145-65", 65.0, 145.0, TempoUnit.Bpm)]
    [InlineData("95", null, 95.0, TempoUnit.Bpm)]
    [InlineData("70%", null, 70.0, TempoUnit.Percent)]
    [InlineData("60 - 72.5 %", 60.0, 72.5, TempoUnit.Percent)]
    [InlineData("fast", null, null, TempoUnit.Bpm)]
    [InlineData("  ", null, null, TempoUnit.Bpm)]
    [InlineData(null, null, null, TempoUnit.Bpm)]
    public void ParseSpeed(string? text, double? min, double? max, TempoUnit unit) =>
        Assert.Equal((min, max, unit), LogCsv.ParseSpeed(text));

    [Fact]
    public async Task Export_quotes_fields_and_writes_ranges()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var piece = new Piece { Name = "Scales, major", Sections = [new Section { Id = "s1", Name = "Up" }] };
        var (store, _) = await FakeJs.StoreAsync(db =>
        {
            db.Seed("pieces", piece);
            db.Seed("logs",
                new PracticeLog { PieceId = piece.Id, PieceName = piece.Name, SectionId = "s1", Date = new(2026, 3, 2), ValueMin = 80, Value = 100, DurationSec = 330, Effort = 3, Notes = "said \"ok\"" },
                new PracticeLog { PieceName = "Gone", Date = new(2026, 3, 1), Value = 75, Unit = TempoUnit.Percent });
        });
        var lines = LogCsv.ExportLogCsv(store).Split('\n');
        Assert.Equal("Date,Exercise,Section,BPM/Speed,Time (min),Feel,Notes", lines[0]);
        Assert.Equal("01/03/2026,Gone,,75%,,,", lines[1]); // oldest first
        Assert.Equal("02/03/2026,\"Scales, major\",Up,80 - 100,5.5,Just right,\"said \"\"ok\"\"\"", lines[2]);
    }
}

public class HotkeyTests
{
    [Fact]
    public void Every_combo_belongs_to_one_hotkey()
    {
        // Hotkeys throws while initialising on a clash; this also makes sure it initialises at all
        var combos = Hotkeys.All.SelectMany(h => h.Combos).ToList();
        Assert.Equal(combos.Count, combos.Distinct().Count());
        Assert.All(Hotkeys.All, h => Assert.NotEmpty(h.Combos));
    }

    [Theory]
    [InlineData("Shift+ArrowUp", "[Shift] + [↑]")]
    [InlineData("g d", "[G] then [D]")]
    [InlineData("+", "[+]")]
    [InlineData("Escape", "[Esc]")]
    [InlineData("Space", "[Space]")]
    public void Display(string combo, string expected) =>
        Assert.Equal(expected, string.Join(" ", Hotkeys.Display(combo).Select(p => p.key ? $"[{p.text}]" : p.text)));
}

public class ClickSoundTests
{
    [Fact]
    public void Every_sound_has_its_samples()
    {
        var dir = Path.Combine(Repo.Root, "src", "Templing", "wwwroot", "sounds");
        Assert.All(ClickSounds.All, s =>
        {
            Assert.True(File.Exists(Path.Combine(dir, $"{s.Id}_hi.wav")), $"{s.Id}_hi.wav");
            Assert.True(File.Exists(Path.Combine(dir, $"{s.Id}_lo.wav")), $"{s.Id}_lo.wav");
        });
    }

    [Fact]
    public void Unknown_ids_play_the_default()
    {
        Assert.Equal(ClickSounds.Default, ClickSounds.Resolve("wood"));
        Assert.Equal(ClickSounds.Default, ClickSounds.Resolve(null));
        Assert.Equal("Perc_Snap", ClickSounds.Resolve("Perc_Snap"));
        Assert.NotNull(ClickSounds.Find(ClickSounds.Default));
    }

    [Theory]
    [InlineData("Perc_ClickToy", "Click toy", "Percussion")]
    [InlineData("Perc_Tamb_B", "Tambourine B", "Percussion")]
    [InlineData("Perc_Can", "Can", "Percussion")]
    [InlineData("Synth_Block_C", "Block C", "Synth")]
    public void Names_and_groups(string id, string name, string group) =>
        Assert.Equal(new ClickSound(id, name, group), ClickSounds.Find(id));
}

public class AppInfoTests
{
    [Theory]
    [InlineData("0.1.0+3f2a9c1d8e7f", "0.1.0 (3f2a9c1)")]
    [InlineData("0.1.0+abc", "0.1.0 (abc)")]
    [InlineData("0.1.0+", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    public void Format(string info, string expected) => Assert.Equal(expected, AppInfo.Format(info));
}

static class Repo
{
    /// <summary>The checkout's root (where Templing.slnx is).</summary>
    public static string Root { get; } = Find();

    static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Templing.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("Templing.slnx not found above " + AppContext.BaseDirectory);
    }
}
