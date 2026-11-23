<div align="center">

<img src="src/Templing/wwwroot/icon.svg" width="72" alt="">

# Templing

**A practice tracker for any instrument.**
A training metronome, a library of the pieces you're working on, and stats that show you getting faster.

![The Templing metronome playing the verse of a song, with the speed trainer, intervals and count-in options alongside](docs/images/metronome.png)

</div>

Templing runs entirely in your browser. There's no account and no server: your pieces, practice log and settings stay on your device, and you can back them up to a file whenever you like.

## What it does

### 🎵 A metronome built for practice

- **Sample-accurate timing** from a Web Audio look-ahead scheduler.
- **Speed trainer**: climb from a start tempo to a goal in whole-bpm steps, over a set number of minutes or every N bars. Tempo changes land on bar lines.
- **Interval trainer**: work / rest / rounds, with audio cues before each change. The click stops during rests.
- **Sections**: give a song its intro, verse and solo, each with its own tempo, time signature and division (1/4 through 1/32, triplets included), and **play through** them in order.
- **Count-in**, **tap tempo**, accent and visual beat flash.

### 📚 A library of pieces

Songs, exercises and riffs, each with a **goal tempo** (in bpm or as a % of the song's tempo) and an optional **start tempo**. Open a piece and the metronome is ready where you left off. When you pass the goal, Templing says so and offers to raise it.

**Guitar Pro and MusicXML import** (.gp, .gpx, .gp5, .gp4, .gp3, .musicxml, .mxl): section markers and tempo changes become sections, repeats are expanded, and changing time signatures are followed bar by bar. The tab is shown alongside the metronome, with the current bar and beat highlighted in time with the click.

### ⏱️ Timer and plans

- **Practice timer**: the interval trainer without a click, for playing along to a backing track or Guitar Pro. Add another block or start an uncounted rest when it ends.
- **Plans**: ordered routines of pieces and rests, e.g. a warm-up with a speed trainer from 65 to 145 over ten minutes, then two songs. A bar across the top shows the current and next step, and each step can be logged as you go.

### 📈 Progress you can see

- **Only active time counts**: count-ins, rests and pauses don't, so a 5-1-5 interval session logs 10 minutes.
- **Log a session** in one keypress, with the tempo range you played (e.g. `95–145`).
- **Stats**: a practice calendar, minutes per week against a weekly goal, streaks, time per piece, biggest tempo gains, and a tempo-over-time chart for every piece.
- **Dashboard**: jump back into what you played last, your favourites, the pieces closest to their goal, and the ones you haven't touched in a while.

### ⌨️ Keyboard first

Everything common has a shortcut. Press `?` in the app for the full list.

| Key | Action |
| --- | --- |
| `Space` | Start / stop |
| `↑` / `↓` (`Shift` for ±5) | Change tempo |
| `←` / `→` | Previous / next section |
| `R` | Restart the section (with count-in) |
| `T` | Tap tempo |
| `C` / `X` / `I` | Toggle count-in / speed trainer / intervals |
| `L` | Stop and log the session |
| `N` | Log practice manually |
| `/` | Search the library |
| `g` then `d` `l` `m` `t` `p` `h` `s` `,` | Go to a page |

## Your data

Everything is stored in your browser's IndexedDB, per browser and per address. Nothing is sent anywhere.

- **Backup / restore** a JSON file in *Settings & data* to move to another device or browser. You choose whether a restore brings your settings too.
- **CSV export** of the practice log, for spreadsheets.
- Imported score files aren't in the backup (they can be large); re-import them on the new device.

## Running it

You need the [.NET 11 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Templing/Templing.csproj --launch-profile http
```

Then open <http://localhost:5075>.

To try it on a phone or tablet on the same network, use the `lan` profile and open `https://<your computer's IP>:7212`, accepting the certificate warning. (The browser only allows the app's crypto APIs on HTTPS or localhost.)

```bash
dotnet run --project src/Templing/Templing.csproj --launch-profile lan
```

## Tests

Unit tests for the C# (xUnit) and the JavaScript (Vitest, which needs [Node.js](https://nodejs.org) 18 or later):

```bash
dotnet test --project tests/Templing.Tests
cd tests/js && npm install && npm test
```

## Built with

- [Blazor WebAssembly](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor) and [MudBlazor](https://mudblazor.com)
- [alphaTab](https://alphatab.net) for reading and rendering Guitar Pro / MusicXML
- [Apache ECharts](https://echarts.apache.org) for the charts
- Metronome click sounds recorded by Ludwig Peter Müller (CC0)
- The Web Audio API for the metronome engine

Contributors: [CLAUDE.md](CLAUDE.md) covers the code layout and the rules for persisted data, and [DESIGN.md](DESIGN.md) the UI conventions.

## License

[MIT](LICENSE). The vendored libraries keep their own licenses: alphaTab (MPL-2.0, [`src/Templing/wwwroot/lib/alphatab/LICENSE`](src/Templing/wwwroot/lib/alphatab/LICENSE)) and Apache ECharts (Apache-2.0, [`src/Templing/wwwroot/lib/echarts/LICENSE`](src/Templing/wwwroot/lib/echarts/LICENSE)).
