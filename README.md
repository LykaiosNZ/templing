# Songler

A practice tracker for instruments: a training metronome, a library of songs/exercises/riffs with tempo goals, and fitness-tracker-style stats. It runs entirely in the browser (Blazor WebAssembly + MudBlazor), with data kept in IndexedDB.

## Run

```bash
dotnet run --project src/Songler/Songler.csproj --launch-profile http
```

Then open http://localhost:5075. You need the .NET 11 SDK.

## Features

- **Library**: songs, exercises and riffs. Each item measures progress in **BPM** or **% of song tempo**, and has a goal and a tempo map of **sections**. Each section has its own tempo, time signature, subdivision and bar count.
- **Metronome**: a Web Audio look-ahead scheduler, so timing is sample-accurate. You can open it on its own (free mode), from an item, or as a saved **custom metronome** with its own tempo changes. It keeps playing while you browse other pages, and the app bar shows a mini player.
  - **Speed trainer**: goes from a start tempo to an end tempo, either over N minutes of playing or in steps every N bars. Tempo changes land on bar lines. It can hold at the end tempo or stop.
  - **Interval trainer**: work/rest/rounds (presets include 5·1·5). The click stops during rests, and audio cues sound before each change.
  - **Count-in**: optional, 1 or 2 bars, and it plays at every restart and every interval round.
  - **Play-through**: steps through the sections in order using their bar counts, with optional looping.
- **Time tracking**: only *active* time counts, meaning time when the click plays in a work phase. Rests, count-ins and pauses are excluded, matching how the spreadsheet logged 5-1-5 sessions. **Log session** (`L`) saves the active time and the tempo range you played. You can also log entries manually (`N`) or edit them.
- **Stats**: practice calendar heatmap, minutes per week against a weekly goal, streaks, practice by weekday, time per item and type, biggest tempo gains, and goal progress. Each item has a tempo-over-time chart, with whiskers for ranged sessions like `65 - 145`.
- **Guitar Pro import + tab view**: *Import Guitar Pro* (Library) or *Import parts from Guitar Pro* (item editor) reads .gp/.gpx/.gp5/.gp4/.gp3 and MusicXML via [alphaTab](https://alphatab.net) (MPL-2.0, vendored in `wwwroot/lib/alphatab`). Section markers and tempo changes become song parts; repeats are expanded and time-signature changes are kept bar by bar, so the metronome follows the real meter. The score file is kept in IndexedDB and rendered on the item and metronome pages, with the current bar and beat highlighted in sync with the click. Tempo is quarter-note based, as in Guitar Pro (x/8 bars click eighths).
- **Import/export**: imports the Google Sheets "Pieces" and "Practice Log" CSVs (Settings & data). Re-importing skips duplicates. JSON backup/restore and CSV export are also available.

## Keyboard shortcuts

Press `?` in the app to see the full list.

| Key | Action |
| --- | --- |
| `Space` | Start / stop the metronome (works on every page) |
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

- `src/Songler/Models`: data model (items, sections, logs, presets, settings)
- `src/Songler/Services`: `DataStore` (IndexedDB cache), `MetronomeService`, `Shortcuts`, `Stats`, `CsvImport`
- `src/Songler/wwwroot/js`: `metronome.js` (audio engine), `db.js` (IndexedDB), `keys.js` (global shortcuts)
- `src/Songler/Components/Charts`: hand-rolled SVG charts (tempo line, bars, heatmap, ranked bars)
