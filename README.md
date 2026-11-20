# Songler

A practice tracker for instruments: a training metronome, a library of songs/exercises/riffs with tempo goals, and fitness-tracker-style stats. It runs entirely in the browser (Blazor WebAssembly + MudBlazor), with data kept in IndexedDB.

## Run

```bash
dotnet run --project src/Songler/Songler.csproj --launch-profile http
```

Then open http://localhost:5075. You need the .NET 11 SDK.

## Features

- **Dashboard**: a launch pad. Star pieces and plans to keep them under **Favourites**, each one click from the metronome or a plan run; **Recent** lists what you practised last; and this week's goal and your streak sit at the top. Fuller statistics are on Stats.
- **Library**: songs, exercises and riffs. Each piece measures progress in **BPM** or **% of song tempo**, and has a goal and a tempo map of **sections**. Each section has its own tempo, time signature, division (what clicks: 1/4, 1/8, 1/8t … 1/32) and bar count.
- **Metronome**: a Web Audio look-ahead scheduler, so timing is sample-accurate. You can open it on its own (free mode), from a piece, or as a saved **custom metronome** with its own tempo changes. Practice stays on its page: leaving the Metronome or Timer while it runs asks first, and stops it.
  - **Speed trainer**: goes from a start tempo to an end tempo in a set number of whole-bpm steps (e.g. +6, +5, +6), spread over N minutes of playing or one every N bars. Tempo changes land on bar lines. It can hold at the end tempo or stop.
  - **Interval trainer**: work/rest/rounds, with presets you can edit in Settings. The click stops during rests, and audio cues sound before each change.
  - **Count-in**: optional, any number of bars (1 by default), and it plays at every restart and every interval round.
  - **Play-through**: steps through the sections in order using their bar counts, with optional looping.
- **Practice timer** (`/timer`, `g` then `t`): the interval trainer with no click, for playing along to Guitar Pro or a backing track. Intervals are optional (off = stopwatch). Audio cues and a colour change mark each work/rest switch. At the end you can log the time, add another block (e.g. +5 min after a 1 min rest), or start a rest countdown between pieces that isn't counted as practice.
- **Plans** (`/plans`, `g` then `p`): ordered practice routines. Each step is a piece (metronome or click-free timer, with work minutes, rounds and rests) or a rest. Metronome steps can set a start tempo, or a speed trainer for that step only (e.g. 65 → 145 over the step's 10 minutes). Your own trainer settings are left alone. Running a plan shows a bar across the top of the app with the current and next step. Each step opens with its intervals set, and when it finishes you're asked to log it before moving on. A setting logs steps automatically instead. You can skip, go back, finish a step early or end the plan.
- **Time tracking**: only *active* time counts, meaning time when the click plays in a work phase. Rests, count-ins and pauses are excluded, so a 5-1-5 interval session logs 10 minutes. **Log session** (`L`) saves the active time and the tempo range you played. You can also log entries manually (`N`) or edit them.
- **Stats**: practice calendar heatmap, minutes per week against a weekly goal, streaks, practice by weekday, time per piece and type, biggest tempo gains, and goal progress. Each piece has a tempo-over-time chart, with whiskers for ranged sessions like `65 - 145`.
- **Guitar Pro import + tab view**: *Import from Guitar Pro* (Library) or *Import from Guitar Pro* (piece editor) reads .gp/.gpx/.gp5/.gp4/.gp3 and MusicXML via [alphaTab](https://alphatab.net) (MPL-2.0, vendored in `wwwroot/lib/alphatab`). Section markers and tempo changes become song parts; repeats are expanded and time-signature changes are kept bar by bar, so the metronome follows the real meter. The score file is kept in IndexedDB and rendered on the item and metronome pages, with the current bar and beat highlighted in sync with the click. Tempo is quarter-note based, as in Guitar Pro (x/8 bars click eighths).
- **Backup**: JSON backup/restore (pieces, logs, plans, custom metronomes) and CSV export of the practice log, in Settings & data.

## Keyboard shortcuts

Press `?` in the app to see the full list.

| Key | Action |
| --- | --- |
| `Space` | Start / stop the metronome or timer (on their pages) |
| `R` | Restart section (with count-in) |
| `↑` / `↓` (`Shift` for ±5) | Change tempo |
| `←` / `→` | Previous / next section |
| `T` | Tap tempo |
| `C` / `X` / `I` | Toggle count-in / speed trainer / intervals |
| `L` | Stop and log the session |
| `N` | Log practice manually |
| `/` | Search the library |
| `g` then `d` `l` `m` `h` `s` `,` | Go to a page |

Shortcuts are ignored while you type in a field.

## Layout

- `src/Songler/Models`: data model (pieces, sections, logs, presets, settings)
- `src/Songler/Services`: `DataStore` (IndexedDB cache), `MetronomeService`, `Shortcuts`, `Stats`, `LogCsv`, `ScoreImport`, `PlanRunner`
- `src/Songler/wwwroot/js`: `metronome.js` (audio engine), `db.js` (IndexedDB), `keys.js` (global shortcuts)
- `src/Songler/Components/Charts` + `wwwroot/js/charts.js`: charts on [Apache ECharts](https://echarts.apache.org) (Apache-2.0; a trimmed bundle in `wwwroot/lib/echarts`, built by `tools/echarts`): tempo line, bars, practice calendar; ranked bars on MudBlazor progress bars
