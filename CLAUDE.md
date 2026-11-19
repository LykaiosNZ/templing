# Songler: notes for agents

Practice tracker for guitar: Blazor WebAssembly (.NET 11) + MudBlazor 9, Web Audio metronome in JS, all data in the browser (IndexedDB). No server, no accounts.

**UI conventions live in [DESIGN.md](DESIGN.md)** (copy, formats, controls, icons, shared components). Read it before UI work, and add to it when a new UI decision is made.

## Run / build

```bash
dotnet build src/Songler/Songler.csproj
dotnet run --project src/Songler/Songler.csproj --launch-profile http   # http://localhost:5075
```

- The `http` profile listens on all interfaces, on `http://…:5075` and `https://…:7212`. From another device, use **`https://<this machine's IP>:7212`** and accept the certificate warning (the dev certificate is for localhost). Plain http only works on localhost: the .NET WebAssembly runtime needs `crypto.subtle`, which browsers only expose to secure pages. Each address is its own origin with its own IndexedDB data, so it starts empty (use backup/restore to copy data across).
- The dev server does **not** hot-reload: restart it after C# / Razor / JS / CSS changes (static files are served from a build manifest, so even CSS edits need a build + restart) (`.claude/launch.json` has the `songler` preview config).
- There is no test project. Verify changes in the browser, and use clearly named throwaway fixtures (e.g. ids prefixed `zztest-`). Remove them afterwards and never touch the user's real data in the browser.

## Layout

| Path | What |
| --- | --- |
| `Models/Models.cs` | All persisted types: `Piece`/`Section`, `PracticeLog`, `MetronomePreset`, `PracticePlan`/`PlanStep`, `AppSettings` |
| `Services/DataStore.cs` | In-memory cache over IndexedDB: load once, write-through, `Changed` event |
| `Services/Backup.cs` | JSON backup export/restore (**single source of truth for what's backed up**) |
| `Services/MetronomeService.cs` | App-wide metronome/timer state, session tracking, interval/trainer overrides |
| `Services/PlanRunner.cs` | Runs practice plans step by step |
| `Services/ScoreImport.cs` + `wwwroot/js/score.js` | Guitar Pro/MusicXML via alphaTab (vendored in `wwwroot/lib/alphatab`, MPL-2.0) |
| `Services/Stats.cs`, `Services/LogCsv.cs` | Stats maths, tempo parsing + CSV log export |
| `Services/Hotkeys.cs`, `Services/Shortcuts.cs`, `wwwroot/js/keys.js` | Keyboard shortcuts: the one catalog of keys, the handler registry, the key listener |
| `wwwroot/js/metronome.js` | Audio engine (look-ahead scheduler, intervals, trainer, per-bar meters) |
| `wwwroot/js/db.js` | IndexedDB wrapper (JSON stores + binary `files` store) |
| `Components/Charts/*` + `wwwroot/js/charts.js` | Charts via Apache ECharts (vendored in `wwwroot/lib/echarts`, Apache-2.0); C# formats all text, JS draws |

## Persisted data: checklist (don't skip)

Every new piece of user data has to be wired through **storage, backup/restore and delete-all**, or it's silently lost when the user moves devices.

**Adding a new entity (a new IndexedDB store):**
1. `wwwroot/js/db.js`: add the store name to `STORES` **and bump `DB_VERSION`** (the upgrade handler creates missing stores).
2. `DataStore.cs`: add the list, load it in `LoadAsync`, add save/delete methods that call `Changed`, include it in `ImportAsync` and in `ClearAllAsync` (both the store-name array and the in-memory `.Clear()`).
3. `Backup.cs`: add a **nullable field with a default** to `BackupFile` (old backups must still load), fill it in `Export`, restore it in `RestoreAsync`, add it to the summary text, **bump `Backup.Version`** and extend the format-history comment.
4. `SettingsPage.razor`: add it to the "Delete all data" confirmation wording. `RestoreDialog.razor`: mention it in the "Songs & data only" description if it's data, or in the settings line if it's a preference.
5. If other entities reference it by id (e.g. plan steps → pieces), decide what happens on delete (see `DataStore.DeletePieceAsync`) and handle missing references in the UI.

**Adding a field to an existing persisted type:**
- Give it a sensible default in the model, because old IndexedDB rows and old backups won't have it. Nullable is fine.
- Nothing else is needed for backup (whole objects are serialised), **unless** the field references something that isn't backed up (see score files below).
- If it's a user preference on `AppSettings`: settings are backed up as one object, and restoring them is the user's choice in `RestoreDialog` ("Songs & data only" or "Everything, including settings"). If the new setting changes what that choice means, update the dialog's description. Restoring settings reloads the app, because services hold references into the settings object.

**Deliberately not in the backup:**
- Score files (Guitar Pro) are binary blobs in the `files` store, too large for a JSON backup. Pieces keep `ScoreFileId`, and the tab view shows a "re-import" message if the file is missing. If you change this, update the Settings > Backup text.
- The `session` store holds transient state that should survive a refresh but not a device move, e.g. the running plan (`PlanRunner`, key `planrun`). It's cleared by "Delete all data" and never backed up.
- `localStorage` isn't used for app data. Don't start.

Serialisation uses `DataStore.Json` (PascalCase property names, string enums via attributes). Keep using it for anything written to IndexedDB or backups.

## Gotchas learned the hard way

- **Prefer MudBlazor components** (progress bars, tooltips, dialogs…) over hand-built ones; see DESIGN.md. Charts are the exception: they're ECharts, and new chart kinds go in `charts.js` + a `ChartBase` component. The docs at mudblazor.com are a client-side app, so read them in a browser tab (plain fetches return an empty shell).

- **Naming:** a thing in the library is a **piece** everywhere: UI text, code (`Piece`, `PieceId`, `PieceDialog`, `PieceDetail`), routes (`/metronome/piece/{id}`), CSS (`.piece-card`) and the IndexedDB store (`pieces`). Never "item" (MudBlazor's `MudSelectItem` etc. aside).

- **Render fragments capture lazily:** inside `@for` loops, child content (e.g. `<MudChip>@x</MudChip>`) is evaluated later. Copy values to locals in the loop body (`var label = Labels[i];`) instead of indexing parameters or loop variables inside the fragment.
- **JS interop with arrays:** `InvokeVoidAsync("fn", array)` spreads a `string[]` as `params`. Pass `(object)array`.
- **MudBlazor 9** renamed things (e.g. `AutoGrow` → `Sizing`, tabs `PanelClass` → `TabPanelsClass`). The analyzer warnings (MUD0002) tell you the replacement, so treat them as errors.
- **Icons:** MudBlazor's Material set (`Icons.Material.Rounded.*`) by default. For icons it lacks (e.g. metronome), use Pictogrammers MDI: add the name to `tools/icons/icons.txt`, run `npm install && npm run generate` in `tools/icons`, then use `Mdi.Name`. Only listed icons ship. The output, `Components/Mdi.g.cs`, is generated, so don't hand-edit it.
- **Keyboard shortcuts are defined only in `Hotkeys.cs`** (group, description, every combo). `Shortcuts.Register` takes a `Hotkey`, never a key string, and the shortcuts dialog is generated from `Hotkeys.All`, so a new key is listed automatically. A combo claimed twice throws at startup.
- **Durations in the UI use `<DurationInput>`** (h:mm:ss text field, value in seconds; also accepts m:ss or bare minutes, ↑/↓ steps). Don't use number fields for minutes, and not `MudTimePicker` either: it's time-of-day, has no seconds and caps at 23:59. Models keep minutes or seconds as they are, so convert at the call site.
- **Tempo is quarter-note based** (as in Guitar Pro): an x/8 bar clicks eighths. Score-linked sections carry `ScoreBars` ([masterBar, num, den] per bar); don't edit their meter/bars by hand.
- **Practice time = active time only:** only work-phase time while running counts. Count-ins, rests, pauses and rest-only countdowns don't.
- **Metronome overrides** (`MetronomeService`):
  - `_override` is one-off (timer extend / rest countdown) and is cleared when a run ends.
  - `_step` / `_stepTraining` are pinned by a plan step and cleared by `PlanRunner.EndAsync`.
  - UI should read `Metro.Training` / `Metro.EffectiveIntervals`, not `Settings.*`, while a plan runs.
- **Engine events:** the engine reports `running=false` just before `finished`. Only `event == "stopped"` means a manual stop.
- **Files are CRLF on Windows checkouts.** Multi-line string replacements in scripts should normalise line endings.
