namespace Templing.Tests;

public class SectionTests
{
    [Theory]
    [InlineData(1, 4, "1/4")]
    [InlineData(2, 4, "1/8")]
    [InlineData(3, 4, "1/8t")]
    [InlineData(4, 4, "1/16")]
    [InlineData(6, 4, "1/16t")]
    [InlineData(2, 8, "1/16")]
    [InlineData(3, 8, "1/16t")]
    [InlineData(5, 4, "5 per beat")]
    [InlineData(0, 4, "1/4")]
    public void Division_reads_as_a_note_value(int subdivision, int beatUnit, string expected) =>
        Assert.Equal(expected, Section.DivisionName(subdivision, beatUnit));

    [Fact]
    public void Divisions_offered_stop_at_32nds_and_16th_triplets()
    {
        Assert.Equal(["1/4", "1/8", "1/8t", "1/16", "1/16t", "1/32"], Section.Divisions(4).Select(d => d.Label));
        Assert.Equal(["1/8", "1/16", "1/16t", "1/32"], Section.Divisions(8).Select(d => d.Label));
    }

    [Fact]
    public void Divisions_keep_an_old_value_outside_the_range() =>
        Assert.Contains(Section.Divisions(8, current: 8), d => d.Value == 8);

    [Fact]
    public void ClampDivision_keeps_an_offered_division_else_the_nearest_slower()
    {
        var s = new Section { BeatUnit = 4, Subdivision = 3 };
        s.ClampDivision();
        Assert.Equal(3, s.Subdivision);

        s = new Section { BeatUnit = 8, Subdivision = 8 }; // 1/64: not offered in x/8
        s.ClampDivision();
        Assert.Equal(4, s.Subdivision);

        s = new Section { BeatUnit = 16, Subdivision = 1 }; // the slowest is always kept
        s.ClampDivision();
        Assert.Equal(1, s.Subdivision);
    }

    [Fact]
    public void A_score_linked_section_takes_its_length_and_meter_from_the_score()
    {
        var s = new Section { Bars = 4, BeatsPerBar = 4, BeatUnit = 4, ScoreBars = [[0, 4, 4], [1, 4, 4], [2, 7, 8]] };
        Assert.True(s.FromScore);
        Assert.Equal(3, s.Length);
        Assert.True(s.MixedMeter);
        Assert.Equal("4/4 (mixed)", s.Meter);
        Assert.Equal(4, new Section { Bars = 4 }.Length);
    }
}

public class TrainingSettingsTests
{
    [Fact]
    public void Levels_spread_the_remainder_so_jumps_differ_by_at_most_one() =>
        Assert.Equal([128, 134, 139, 145, 150], new TrainingSettings { Start = 128, End = 150, Steps = 4 }.Levels());

    [Fact]
    public void Levels_can_ramp_down() =>
        Assert.Equal([120, 110, 100], new TrainingSettings { Start = 120, End = 100, Steps = 2 }.Levels());

    [Fact]
    public void Steps_are_capped_at_one_per_whole_bpm()
    {
        var t = new TrainingSettings { Start = 100, End = 103, Steps = 10 };
        Assert.Equal(3, t.StepCount);
        Assert.Equal([100, 101, 102, 103], t.Levels());
    }

    [Fact]
    public void Old_settings_without_steps_keep_what_the_trainer_used_to_do()
    {
        Assert.Equal(40, new TrainingSettings { Start = 80, End = 120, Mode = RampMode.OverTime }.StepCount);
        Assert.Equal(14, new TrainingSettings { Start = 80, End = 120, Mode = RampMode.EveryNBars, Increment = 3 }.StepCount);
    }

    [Fact]
    public void The_same_start_and_end_is_one_step_that_holds() =>
        Assert.Equal([100, 100], new TrainingSettings { Start = 100, End = 100 }.Levels());
}

public class SettingsAndPlanTests
{
    [Theory]
    [InlineData(1.5, 1)]
    [InlineData(-0.2, 0)]
    [InlineData(0.3, 0.3)]
    public void Volume_is_clamped_to_0_1(double set, double expected) =>
        Assert.Equal(expected, new MetronomeSettings { Volume = set }.Volume);

    [Fact]
    public void Plan_step_time_includes_rests_between_rounds()
    {
        var step = new PlanStep { Minutes = 5, Rounds = 3, RestMin = 1 };
        Assert.Equal(17, step.TotalMinutes);
        Assert.Equal(15, step.WorkMinutes);
        Assert.Equal("3 × 5 min, 1 min rests", step.IntervalText);
        var rest = new PlanStep { Kind = PlanStepKind.Rest, Minutes = 2, Rounds = 4 };
        Assert.Equal(2, rest.TotalMinutes);
        Assert.Equal("2 min rest", rest.IntervalText);
        Assert.Equal(19, new PracticePlan { Steps = [step, rest] }.TotalMinutes);
    }

    [Fact]
    public void Plan_step_trainer_runs_over_the_whole_work_time()
    {
        var t = new PlanStep { Minutes = 4, Rounds = 2, Trainer = true, TrainerStart = 90, TrainerEnd = 110 }.TrainerSettings();
        Assert.True(t.Enabled);
        Assert.Equal(8, t.DurationMin);
        Assert.False(t.StopAtEnd);
    }

    [Fact]
    public void Plan_step_tempo_text()
    {
        Assert.Equal("start 90 bpm", new PlanStep { StartTempo = 90 }.TempoText(TempoUnit.Bpm));
        Assert.Equal("trainer 80 → 100%, every 4 bars",
            new PlanStep { Trainer = true, TrainerStart = 80, TrainerEnd = 100, TrainerMode = RampMode.EveryNBars }.TempoText(TempoUnit.Percent));
        Assert.Equal("", new PlanStep { Mode = StepMode.Timer, StartTempo = 90 }.TempoText(TempoUnit.Bpm));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(6, null)]
    [InlineData(3, "Just right")]
    public void Effort_levels(int? value, string? label) => Assert.Equal(label, Effort.Get(value)?.Label);
}
