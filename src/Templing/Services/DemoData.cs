using Templing.Models;

namespace Templing.Services;

/// <summary>
/// The sample library the demo build starts with (<c>-p:Demo=true</c>; see <see cref="DataStore.Demo"/>): a guitarist's few months
/// of practice, ending today, so every screen has something to show. The same every time (a fixed random seed),
/// so a scripted run-through (tools/demo) finds the same pieces and numbers; only the dates move with today.
/// </summary>
public static class DemoData
{
    public record Set(List<Piece> Pieces, List<PracticeLog> Logs, List<MetronomePreset> Presets, List<PracticePlan> Plans, AppSettings Settings);

    const int Days = 150;

    // a piece and how its practice went: tempo at the first session → now (in its unit), how often it comes up
    record Plan(Piece Piece, double From, double To, double Weight, int StartDay = 0, int? StopDay = null);

    public static Set Create(DateTime today)
    {
        today = today.Date;
        var rng = new Random(53);
        var first = today.AddDays(-Days);
        DateTime On(int day) => first.AddDays(day);

        Section S(string name, double bpm, int beats = 4, int unit = 4, int sub = 1, int bars = 0) =>
            new() { Id = Slug(name), Name = name, Bpm = bpm, BeatsPerBar = beats, BeatUnit = unit, Subdivision = sub, Bars = bars };

        var spain = new Piece
        {
            Id = "demo-spain", Name = "Spain", Type = PieceType.Song, Goal = 150, Favourite = true, PlayThrough = true,
            Sections = [S("Intro", 150, bars: 8), S("Head", 150, sub: 2, bars: 32), S("Solo", 150, sub: 2, bars: 64), S("Coda", 150, bars: 16)],
            Notes = "Head: keep the syncopation in bar 9 relaxed. Solo: thumb behind the neck on the stretches.",
            Created = On(0),
        };
        var dover = new Piece
        {
            Id = "demo-dover", Name = "Cliffs of Dover", Type = PieceType.Song, Unit = TempoUnit.Percent, Goal = 100, StartTempo = 75,
            Sections = [S("Intro", 156, bars: 12), S("Main theme", 156, sub: 4, bars: 16), S("Bridge", 156, sub: 4, bars: 8), S("Outro", 156, bars: 8)],
            Notes = "Practise at a percentage of the record tempo.",
            Link = "https://www.youtube.com/results?search_query=cliffs+of+dover",
            Created = On(20),
        };
        var wing = new Piece
        {
            Id = "demo-wing", Name = "Little Wing", Type = PieceType.Song, Goal = 70,
            Sections = [S("Intro", 70, bars: 8), S("Verse", 70, bars: 16), S("Solo", 70, sub: 4, bars: 16)],
            Created = On(5),
        };
        var odd = new Piece
        {
            Id = "demo-odd", Name = "Schism (verse riff)", Type = PieceType.RiffLick, Goal = 106,
            Sections = [S("Riff", 106, beats: 13, unit: 8, sub: 1)],
            Notes = "Count it 6/8 + 7/8.",
            Created = On(40),
        };
        var sweep = new Piece
        {
            Id = "demo-sweep", Name = "Three-string sweep lick", Type = PieceType.RiffLick, Goal = 140, Favourite = true,
            Sections = [S("Main", 140, sub: 3)],
            Created = On(10),
        };
        var chromatic = new Piece
        {
            Id = "demo-chromatic", Name = "Chromatic 1-2-3-4", Type = PieceType.Exercise, Goal = 160,
            Sections = [S("Main", 160, sub: 4)], Notes = "Warm-up. Every string, up and back.",
            Created = On(0),
        };
        var alternate = new Piece
        {
            Id = "demo-alternate", Name = "Alternate picking: 16ths", Type = PieceType.Exercise, Goal = 180, Favourite = true,
            Sections = [S("Main", 180, sub: 4)], Created = On(0),
        };
        var legato = new Piece
        {
            Id = "demo-legato", Name = "Legato: three notes per string", Type = PieceType.Exercise, Goal = 150,
            Sections = [S("Main", 150, sub: 3)], Created = On(30),
        };
        var skipping = new Piece
        {
            Id = "demo-skipping", Name = "String skipping arpeggios", Type = PieceType.Exercise, Goal = 120,
            Sections = [S("Main", 120, sub: 2)], Created = On(60),
        };
        var trills = new Piece
        {
            Id = "demo-trills", Name = "Trill endurance", Type = PieceType.Exercise, Goal = 120, Archived = true,
            Sections = [S("Main", 120, sub: 4)], Notes = "Retired: folded into the legato exercise.", Created = On(0),
        };

        var plans = new List<Plan>
        {
            new(chromatic, 100, 148, 1.6),
            new(alternate, 110, 166, 1.4),
            new(legato, 90, 132, 0.9, StartDay: 30),
            new(skipping, 70, 96, 0.6, StartDay: 60),
            new(trills, 80, 104, 0.6, StopDay: 70),
            new(sweep, 70, 118, 1.0, StartDay: 10),
            new(odd, 72, 98, 0.7, StartDay: 40),
            new(spain, 96, 138, 1.3),
            new(dover, 60, 88, 1.1, StartDay: 20),
            new(wing, 56, 68, 0.6, StartDay: 5, StopDay: 120),
        };

        var logs = new List<PracticeLog>();
        string[] notes =
        [
            "Clean at the top, a bit tense at the end.", "Sloppy on the string changes.", "Felt great today.",
            "Tired hands, kept it short.", "Recorded a take: timing drifts in bar 3.", "New personal best.",
            "Focused on staying relaxed.", "Backed off and cleaned it up.",
        ];
        for (var day = 0; day <= Days; day++)
        {
            var date = On(day);
            // most days, a little less at weekends; the last ten days without a gap, so there's a streak
            var chance = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.55 : 0.8;
            if (day < Days - 10 && rng.NextDouble() > chance) continue;

            var active = plans.Where(p => day >= p.StartDay && (p.StopDay is null || day <= p.StopDay)).ToList();
            var count = Math.Min(active.Count, 2 + rng.Next(3));
            var picked = new List<Plan>();
            while (picked.Count < count)
            {
                var roll = rng.NextDouble() * active.Sum(p => p.Weight);
                foreach (var p in active)
                {
                    if ((roll -= p.Weight) > 0) continue;
                    if (!picked.Contains(p)) picked.Add(p);
                    break;
                }
            }
            // warm-ups first, songs last, as a session usually goes
            picked = picked.OrderByDescending(p => Array.IndexOf(PieceTypeExt.Ordered, p.Piece.Type)).ToList();

            var time = date.AddHours(day % 3 == 0 ? 7.5 : 18.5).AddMinutes(rng.Next(40));
            foreach (var p in picked)
            {
                var piece = p.Piece;
                var span = Math.Max(1, (p.StopDay ?? Days) - p.StartDay);
                var progress = Math.Clamp((day - p.StartDay) / (double)span, 0, 1);
                // fast at first, slower later, with good and bad days
                var top = p.From + (p.To - p.From) * (1 - Math.Pow(1 - progress, 1.6)) + (rng.NextDouble() - 0.6) * 6;
                top = Math.Round(Math.Clamp(top, p.From - 4, p.To + 2));
                var ranged = piece.Type == PieceType.Exercise && rng.NextDouble() < 0.5;
                var section = piece.HasParts() ? piece.Sections[rng.Next(piece.Sections.Count)] : null;
                var minutes = piece.Type == PieceType.Song ? 10 + rng.Next(16) : 4 + rng.Next(10);
                var effort = rng.NextDouble() < 0.8 ? Math.Clamp(3 + (int)Math.Round((rng.NextDouble() - 0.5) * 2.4), 1, 5) : (int?)null;
                logs.Add(new PracticeLog
                {
                    Id = $"demo-log-{logs.Count + 1:00000}",
                    Date = date,
                    PieceId = piece.Id,
                    PieceName = piece.Name,
                    SectionId = section?.Id,
                    Unit = piece.Unit,
                    Value = top,
                    ValueMin = ranged ? top - 10 - rng.Next(4) * 5 : null,
                    DurationSec = minutes * 60 + rng.Next(60),
                    Effort = effort,
                    Notes = rng.NextDouble() < 0.12 ? notes[rng.Next(notes.Length)] : null,
                    Logged = time,
                    Source = rng.NextDouble() < 0.85 ? "metronome" : "manual",
                });
                time = time.AddMinutes(minutes + 2);
            }
            // now and then, a free-metronome session with no piece
            if (rng.NextDouble() < 0.08)
                logs.Add(new PracticeLog
                {
                    Id = $"demo-log-{logs.Count + 1:00000}", Date = date, PieceName = "Metronome", Value = 90 + rng.Next(5) * 10,
                    DurationSec = (5 + rng.Next(10)) * 60, Logged = time, Source = "metronome",
                });
        }

        var presets = new List<MetronomePreset>
        {
            new()
            {
                Id = "demo-preset-odd", Name = "Odd meters", PlayThrough = true, Loop = true, Created = On(0),
                Sections = [S("5/4", 110, beats: 5, bars: 4), S("7/8", 110, beats: 7, unit: 8, bars: 4), S("9/8", 110, beats: 9, unit: 8, bars: 4)],
            },
            new()
            {
                Id = "demo-preset-shuffle", Name = "Triplet shuffle", Created = On(0),
                Sections = [S("Shuffle", 84, sub: 3)],
            },
        };

        var practicePlans = new List<PracticePlan>
        {
            new()
            {
                Id = "demo-plan-warmup", Name = "Daily warm-up", Favourite = true, Created = On(0), LastRun = today.AddDays(-1).AddHours(18),
                Steps =
                [
                    new() { Id = "w1", PieceId = chromatic.Id, Minutes = 5, Trainer = true, TrainerStart = 120, TrainerEnd = 150, TrainerSteps = 6 },
                    new() { Id = "w2", PieceId = alternate.Id, Minutes = 4, Rounds = 2, RestMin = 1 },
                    new() { Id = "w3", Kind = PlanStepKind.Rest, Minutes = 2 },
                    new() { Id = "w4", PieceId = legato.Id, Minutes = 5, Trainer = true, TrainerStart = 110, TrainerEnd = 135, TrainerMode = RampMode.EveryNBars, TrainerEveryBars = 8, TrainerSteps = 5 },
                ],
            },
            new()
            {
                Id = "demo-plan-songs", Name = "Repertoire", Created = On(30), LastRun = today.AddDays(-3).AddHours(19),
                Steps =
                [
                    new() { Id = "s1", PieceId = spain.Id, SectionId = "head", Minutes = 10, StartTempo = 120 },
                    new() { Id = "s2", PieceId = spain.Id, SectionId = "solo", Minutes = 10, Trainer = true, TrainerStart = 110, TrainerEnd = 135 },
                    new() { Id = "s3", Kind = PlanStepKind.Rest, Minutes = 3 },
                    new() { Id = "s4", PieceId = dover.Id, Minutes = 15 },
                    new() { Id = "s5", PieceId = odd.Id, Mode = StepMode.Timer, Minutes = 5 },
                ],
            },
        };

        var settings = new AppSettings { WeeklyGoalMin = 240 };
        return new Set([.. plans.Select(p => p.Piece)], logs, presets, practicePlans, settings);
    }

    static string Slug(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
