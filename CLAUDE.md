# Templing: notes for agents

Practice tracker for any instrument: Blazor WebAssembly (.NET 11) + MudBlazor 9, Web Audio metronome in JS, all data in the browser (IndexedDB). No server, no accounts.

**UI conventions live in [DESIGN.md](DESIGN.md)** (copy, formats, controls, icons, shared components). Read it before UI work, and add to it when a new UI decision is made.

## Workflow

- **Never commit to `main` directly.** Work on a branch (`git switch -c <short-topic>` from an up-to-date `main`), push it and open a pull request. `main` only changes by merging PRs.
- A PR for an issue says `Closes #N` in its description, so merging closes the issue.

## Run / build

```bash
dotnet build src/Templing/Templing.csproj
dotnet run --project src/Templing/Templing.csproj --launch-profile http   # http://localhost:5075
```

- The `http` profile listens on all interfaces (`http://…:5075`), but other devices need HTTPS: the .NET WebAssembly runtime needs `crypto.subtle`, which browsers only expose to secure pages (HTTPS or localhost). To test on another device, run the **`lan`** profile (`--launch-profile lan`) and open `https://<this machine's IP>:7212`, accepting the certificate warning (the dev certificate is for localhost). Keep HTTPS out of the `http` profile: with an HTTPS address configured, the dev server redirects every page load to it, localhost included. Each address has its own IndexedDB data, so it starts empty (use backup/restore to copy data across).
- **Deploy:** publishing a GitHub release (tag `v1.2.3`) builds and publishes to GitHub Pages (`.github/workflows/pages.yml`, https://lykaiosnz.github.io/templing/); pushes to `main` and pull requests only build (`build.yml`). The release tag is the deployed version (`-p:Version`), so `<Version>` in `Templing.csproj` is only the default for local builds and manual runs: raise it after a release to the next version. Redeploy a release by running the Pages workflow by hand on its tag. The `github-pages` environment must allow tags matching `v*` as well as `main`, or release deploys are refused. The app is served from `/templing/`, so the workflow rewrites `<base href>` and copies `index.html` to `404.html` (Pages has no SPA fallback). Keep URLs relative (`library`, not `/library`), or they escape the subpath.
- **Demo build:** `-p:Demo=true` (`dotnet run … --property:Demo=true`, or the `templing-demo` preview config) defines `DEMO_MODE` (`Directory.Build.props`, so the tests get it too). Demo code is inside `#if DEMO_MODE` so the normal build doesn't carry it: `Services/DemoData.cs`, the seeding in `DataStore` (it calls `useMemory()` in `db.js` first, which loads `js/memorydb.js`, so every store lives in memory and IndexedDB is never opened), the app bar's "Demo" chip and the Settings note. `memorydb.js` is only published in the demo build (`Templing.csproj`). `AppInfo.Demo` / `Store.Demo` are constants for code that needs to ask. It's a build, not a switch: nothing in the app turns it on or off.
  - **`#if` in Razor markup isn't a directive:** it's compiled as text and shows on the page. Put demo-only markup in an `@code` block as a `RenderFragment` inside `#if DEMO_MODE` (`DemoChip` in MainLayout), and render that. `@if (AppInfo.Demo)` isn't enough either: the content is still compiled in.
  - **Tests:** the demo tests (`DemoDataTests`) only exist in the demo build, and the other store tests expect an empty store, so the demo build runs just them: `dotnet test --project tests/Templing.Tests -p:Demo=true -- --filter-class Templing.Tests.DemoDataTests` (CI runs both). `pages.yml` publishes it to `/templing/demo/`, on the **same origin** as the app, so it must never touch IndexedDB; the workflow marks its `<html data-demo>` so the boot script skips reading the real settings for the loading screen's colours, and the top-level `404.html` sends deep links under `/templing/demo/` to the demo's start. The sample is fixed (seeded random, ids like `demo-spain`) with dates relative to today, so `tools/demo/walkthrough.mjs` (Puppeteer, for demo videos) can rely on names and ids: change one and update the script. New screens or features worth showing: give `DemoData` something for them and add a scene. New persisted data needs nothing extra here, unless the demo should start with some.
- **Update notice:** publishing writes `version.json` (the informational version, version + commit) beside the app (`WriteVersionFile` in `Templing.csproj`). `Services/UpdateCheck.cs` fetches it uncached 10 s after start and every 15 min, and offers a reload once it differs from the running build. The dev server has no `version.json`, so it never shows; to try it, publish, serve `wwwroot` statically and edit the file.
- **Analytics:** Umami (`index.html`), on unless the user turns it off (`AppSettings.Analytics`, Settings > Data). The tracker isn't a static `<script>`: the boot script loads it via `templingAnalytics(on)` once it has read the saved setting, and `Analytics.Apply` keeps it in step (when off, `templingUmami` cancels every send). Page views are limited to `lykaiosnz.github.io` by `data-domains`, so dev servers and the preview never report. Blazor navigation is `pushState`, which Umami follows. `templingUmami` (its `data-before-send`) folds ids in routes into `:id` / `:section` and drops the query, so each screen is one page and no ids leave the device: a new route with an id needs adding to its pattern. Custom events go through `Services/Analytics.cs` (→ `templingTrack` in `index.html`): `practice-logged`, `piece-added` (`source`), `plan-started`, `plan-finished` (from `DataStore` / `PlanRunner` events, so only new rows count, not edits, imports or restores), `device-paired` and `backup-sent` (from `Devices`) and `error` (`type`, from `ErrorReporter`, a logger for Blazor's Critical unhandled exceptions). Never send names, notes, tempos, ids or exception messages. The demo build sends no events (`#if DEMO_MODE`).
- **Device link** (`wwwroot/js/link.js`, `Services/Devices.cs`, Settings > Devices): two devices pair by QR code, then send a backup to each other directly over a WebRTC data channel. Keys: the pairing link carries a random secret (`…/pair#<secret>`). The boot script in `index.html` takes it out of the address before the analytics tracker loads (`templingTakePairSecret`, read by the `/pair` page), and Umami is told to drop fragments and referrer queries, but a browser's history may still keep the link, so the secret only *authenticates* the pairing: once proven, each device adds 32 fresh random bytes over the encrypted channel, **committed before revealed** (the device showing the code sends a hash of its nonce, the other sends its nonce, then the first is revealed and checked: `pairExchange`), so no one can try nonces until the codes match, and the pair's key is HMAC(secret, both nonces), kept as a **non-extractable CryptoKey** in the `devices` store. Both screens show a six-digit code from the key and that connection's fingerprints, and the device showing the QR code confirms ("Pair with …?"), which catches anyone who used the code first. Room ids are HMACs of the key (a new room every 10 minutes), and once the data channel opens each side proves it holds the key and saw the same two certificate fingerprints, so a relay that swapped one is caught. A backup is only taken in after the receiving user accepts it (or after Get), one offer or request at a time; `Backup.Gunzip` caps what a backup may unpack to. A `Peer` keeps messages that arrive while its `onmessage` is null and hands them to the next handler, so the gaps between pairing steps (which await crypto) never drop one. Transfer replies (`accept` / `received` / `declined`) only answer a send in progress; a declined Get is its own `request-declined`. Custom colours must be `#rrggbb` (`CustomColours.IsColour`), checked on load, on restore and when writing the colour style sheet, since a backup can come from someone else. Devices only connect while Settings is open on both. The relay URL is the `RelayUrl` build property (`AppInfo.RelayUrl`, the deployed relay by default); `.claude/launch.json` has `relay` (`wrangler dev` on 8787) and `templing-local-relay` (the app pointed at it). To try two devices in the preview, open `localhost:5075` and `127.0.0.1:5075` in two tabs: different origins, so different IndexedDB. Not in the demo build.
- **Threat model** for the device link (and security work generally): keep it proportionate. Templing is a practice tracker whose data stays on the user's devices, and pairing is the user's own two devices within a 10-minute window. Paired devices are trusted (they're the user's), and attacks that need control of the relay *plus* the pairing link aren't worth designing for. Do fix what's plausible: anything a backup from elsewhere can do to the app (the colour style sheet), the secret leaking (analytics, the address bar), data taken in without consent, and ordinary reliability bugs. Brief reviewers with this, so they don't chase the rest.
- **Relay** (`relay/`): the one server Templing uses, only for devices to find each other. It must never carry app data: every message is checked against the fixed shapes in `relay/src/schema.js` (no free-text fields) and re-serialised, there's no TURN, and data goes over the direct WebRTC data channel. A new handshake field needs a shape there and a test, never a pass-through. Its allowed origins are `ALLOWED_ORIGINS` in `relay/wrangler.jsonc` (localhost is always allowed).
- The dev server does **not** hot-reload: restart it after C# / Razor / JS / CSS changes (static files are served from a build manifest, so even CSS edits need a build + restart) (`.claude/launch.json` has the `templing` preview config).
- **Tests** (both run in CI, `build.yml`): C# in `tests/Templing.Tests` (xUnit v3 on Microsoft.Testing.Platform, opted into in `global.json`), JS in `tests/js` (Vitest + jsdom, importing the app's modules from `wwwroot/js` as they are):

  ```bash
  dotnet test --project tests/Templing.Tests
  cd tests/js && npm install && npm test
  ```

  - `FakeJs` stands in for the browser on the C# side: `import("./js/db.js")` is an in-memory IndexedDB, so `DataStore`, `Backup` and `MetronomeService` run as they are. Seed it with `FakeJs.StoreAsync(db => db.Seed(...))`.
  - `tests/js/fakeaudio.js` runs `metronome.js` on a hand-moved audio clock with Vitest's fake timers, recording each click's time and kind; `db.test.js` uses `fake-indexeddb`. jsdom has no layout or `isContentEditable`, so leave drawing details to the browser.
  - `BackupTests.Every_list_in_the_store_is_backed_up` fails when a `DataStore` list has no `BackupFile` field of the same name (the checklist below).
  - Unit tests don't replace a look in the browser for UI changes. The preview pane (`.claude/launch.json`) is its own browser with its own IndexedDB, not the developer's, so its data is scratch: change settings, add pieces, run and log sessions, delete things as the check needs, and don't bother restoring them afterwards.

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
| `wwwroot/js/beatdisplay.js` | The Metronome page's beat display (strip / ring), fed each heard click by `metronome.js`; settings come from `#metro-beats` data attributes |
| `wwwroot/js/shapes.js`, `wwwroot/js/countdown.js` | Shared SVG shapes (segments, countdown ring / bar) for the beat display and the Timer's ring, which `countdown.js` draws on each engine frame. Reuse these rather than drawing a near copy (DESIGN.md, Shared components) |
| `Services/DemoData.cs`, `tools/demo` | The demo build's sample data (`-p:Demo=true`), and the Puppeteer walkthrough that records demo videos |
| `Services/ClickSounds.cs` + `wwwroot/sounds` | Click sounds: hi/lo WAV pairs (CC0, `Credits.txt`), the catalog and display names, `Default`. Audio cues stay synthesised in `metronome.js` |
| `wwwroot/js/db.js` | IndexedDB wrapper (JSON stores + binary `files` store) |
| `tests/Templing.Tests`, `tests/js` | C# (xUnit) and JS (Vitest) unit tests; see Run / build |
| `wwwroot/js/link.js`, `Services/Devices.cs`, `Components/DevicesSection.razor`, `PairDialog.razor`, `wwwroot/js/qr.js` | The device link: pairing, the WebRTC connection and proof, sending backups; Settings > Devices; QR codes via qrcode-generator (MIT, vendored in `wwwroot/lib/qrcode`) |
| `relay/` | The relay: a Cloudflare Worker + Durable Object that lets two devices swap a WebRTC handshake (`relay/README.md`). Its own Node project (Wrangler needs Node 22.12+), tested in CI, deployed by `relay.yml` when it changes on `main` |
| `Components/Charts/*` + `wwwroot/js/charts.js` | Charts via Apache ECharts (Apache-2.0): a trimmed bundle in `wwwroot/lib/echarts`, built by `tools/echarts`; C# formats all text, JS draws |

## Persisted data: checklist (don't skip)

Every new piece of user data has to be wired through **storage, backup/restore and delete-all**, or it's silently lost when the user moves devices.

**Adding a new entity (a new IndexedDB store):**
1. `wwwroot/js/db.js`: add the store name to `STORES` **and bump `DB_VERSION`** (the upgrade handler creates missing stores).
2. `DataStore.cs`: add the list, load it in `LoadAsync`, add save/delete methods that call `Changed`, include it in `ImportAsync` and in `ClearAllAsync` (both the store-name array and the in-memory `.Clear()`).
3. `Backup.cs`: add a **nullable field with a default** to `BackupFile` (old backups must still load), fill it in `Export`, restore it in `RestoreAsync`, add it to the summary text, **bump the format** (below).
4. `SettingsPage.razor`: add it to the "Delete all data" confirmation wording. `RestoreDialog.razor`: mention it in the "Songs & data only" description if it's data, or in the settings line if it's a preference.
5. If other entities reference it by id (e.g. plan steps → pieces), decide what happens on delete (see `DataStore.DeletePieceAsync`) and handle missing references in the UI.

**Adding a field to an existing persisted type:**
- Give it a sensible default in the model, because old IndexedDB rows and old backups won't have it. Nullable is fine.
- Nothing else is needed for backup (whole objects are serialised), **unless** the field references something that isn't backed up (see score files below).
- If it's a user preference on `AppSettings`: settings are backed up as one object, and restoring them is the user's choice in `RestoreDialog` ("Songs & data only" or "Everything, including settings"). If the new setting changes what that choice means, update the dialog's description. Restoring settings reloads the app, because services hold references into the settings object.

**Backups must keep restoring** (every format since `Backup.OldestSupported`, which never goes up). A backup carries `Format`, the file's layout, and `AppVersion`, the build that wrote it; only `Format` decides compatibility. Bump `Backup.Format` when a backup would otherwise be read wrongly, by this code or an older build: a new list, a field renamed, moved or removed, a value whose meaning changes, a new enum value. A new nullable field with a default doesn't need it. A bump needs three things, or `BackupTests` fails:
- a migration in `Backup.Migrations` from the previous format, working on the raw JSON (the models only know the current shape);
- a sample `tests/Templing.Tests/Backups/format-N.json`: export the `Filled()` data in `BackupTests` with the new code. Never edit the old samples, which stand for backups people have;
- a line in the format-history comment on `Backup.Format`.

Renaming or removing a field on a persisted type also fails `BackupTests` (every field in the samples has to be read), so add a migration rather than just changing the model.

**Deliberately not in the backup:**
- Score files (Guitar Pro) are binary blobs in the `files` store, too large for a JSON backup. Pieces keep `ScoreFileId`, and the tab view shows a "re-import" message if the file is missing. If you change this, update the Settings > Backup text.
- The `session` store holds transient state that should survive a refresh but not a device move, e.g. the running plan (`PlanRunner`, key `planrun`). It's cleared by "Delete all data" and never backed up.
- The `devices` store (`link.js`, raw rows via `db.js` `getRows`/`putRow`, not JSON): this device's id and name, and paired devices with their keys. A key belongs to the two devices that made it, so it's never backed up; "Delete all data" clears it and `Devices.ResetAsync` starts the link again as a new device. It isn't a `DataStore` list, which keeps `BackupTests` happy.
- `localStorage` isn't used for app data. Don't start.

Serialisation uses `DataStore.Json` (PascalCase property names, string enums via attributes). Keep using it for anything written to IndexedDB or backups.

## Gotchas learned the hard way

- **Prefer MudBlazor components** (progress bars, tooltips, dialogs…) over hand-built ones; see DESIGN.md. Charts are the exception: they're ECharts, and new chart kinds go in `charts.js` + a `ChartBase` component. The bundle only contains the series, components and features `charts.js` uses (`tools/echarts/entry.js`): if a chart needs another (a scatter series, a legend, dataZoom…), add it there and run `npm install && npm run build` in `tools/echarts`, then rebuild the app. A missing part doesn't throw; it logs "[ECharts] … is used but not imported" and quietly doesn't draw. The docs at mudblazor.com are a client-side app, so read them in a browser tab (plain fetches return an empty shell).

- **Naming:** a thing in the library is a **piece** everywhere: UI text, code (`Piece`, `PieceId`, `PieceDialog`, `PieceDetail`), routes (`/metronome/piece/{id}`), CSS (`.piece-card`) and the IndexedDB store (`pieces`). Never "item" (MudBlazor's `MudSelectItem` etc. aside).
- **The IndexedDB database is named `templing`** (`db.js`, and the boot script in `index.html`). Don't rename it again: a new name starts an empty database and orphans everyone's data.

- **Render fragments capture lazily:** inside `@for` loops, child content (e.g. `<MudChip>@x</MudChip>`) is evaluated later. Copy values to locals in the loop body (`var label = Labels[i];`) instead of indexing parameters or loop variables inside the fragment.
- **Every `.js` file is fingerprinted** (`name.<hash>.js`, `StaticWebAssetFingerprintPattern` in the csproj), and only that name is published, so a release never runs new C# against a cached old module (Pages caches files for 10 minutes). `import("./js/x.js")` and module imports go through the import map in `index.html` and need nothing. A plain `<script src>` must be `js/x#[.{fingerprint}].js` in `index.html`, or `import.meta.resolve(...)` when added from JS (see `charts.js`).
- **JS interop with arrays:** `InvokeVoidAsync("fn", array)` spreads a `string[]` as `params`. Pass `(object)array`.
- **Big data crosses interop as UTF-8 bytes, not strings:** a string argument or return value is JSON-encoded again by interop (every quote escaped), which made reading 20k logs take longer than IndexedDB itself. `byte[]` ⇄ `Uint8Array` is passed as it is (`getAll`, `putMany`, `download` in `db.js`), and System.Text.Json reads and writes UTF-8 directly.
- **Long lists are virtualised** (`<Virtualize>`): a Library card is about 20 MudBlazor components, and drawing 300 at once took 1.7 s. The card grid is virtualised a row at a time: `js/columns.js` reports how many columns fit (from the grid's `--card-min` / `--card-gap`), and `ItemSize` is a measured row height. Change the card's size and that estimate goes with it. Give every virtualised item an `@key` (the piece id): `Virtualize` otherwise reuses elements for whatever scrolls in, so progress bars animate from the previous piece's value.
- **Performance at scale:** screens must not scan `Store.Logs` once per piece. Use `Store.LogsFor(pieceId)` (grouped, oldest first) and `Store.GetPiece` (by id), and for a piece's overall stats `Store.StatsFor` / `LastRatedFor` / `PartsFor`, which are worked out once and shared by every screen. All are rebuilt after each change. Work out what only a drawn card shows (Library's feel rating, section bars) as it's drawn, not for every piece up front. To check a change with a lot of history, put a few hundred pieces and tens of thousands of logs straight into the preview's IndexedDB, and time a **published** build served as static files: the dev server always runs .NET with debugging on (`debugLevel -1`, even with `-c Release`), which is several times slower.
- **MudBlazor 9** renamed things (e.g. `AutoGrow` → `Sizing`, tabs `PanelClass` → `TabPanelsClass`). The analyzer warnings (MUD0002) tell you the replacement, so treat them as errors.
- **Icons:** MudBlazor's Material set (`Icons.Material.Rounded.*`) by default. For icons it lacks (e.g. metronome), use Pictogrammers MDI: add the name to `tools/icons/icons.txt`, run `npm install && npm run generate` in `tools/icons`, then use `Mdi.Name`. Only listed icons ship. The output, `Components/Mdi.g.cs`, is generated, so don't hand-edit it.
- **Keyboard shortcuts are defined only in `Hotkeys.cs`** (group, description, every combo). `Shortcuts.Register` takes a `Hotkey`, never a key string, and the shortcuts dialog is generated from `Hotkeys.All`, so a new key is listed automatically. A combo claimed twice throws at startup.
- **Durations in the UI use `<DurationInput>`** (one field of three numbers with fixed colons, h:mm:ss, value in seconds; ↑/↓ step the number you're in, ←/→ move between them). Don't use number fields for minutes, and not `MudTimePicker` either: it's time-of-day, has no seconds and caps at 23:59. Models keep minutes or seconds as they are, so convert at the call site.
- **Tempo is quarter-note based** (as in Guitar Pro): an x/8 bar clicks eighths. `Section.Subdivision` is clicks per beat; the UI shows it as a note value relative to the beat unit (`Section.Division`: 2 in 4/4 is "1/8", in 6/8 "1/16"). Score-linked sections carry `ScoreBars` ([masterBar, num, den] per bar); don't edit their meter/bars by hand.
- **Practice time = active time only:** only work-phase time while running counts. Count-ins, rests, pauses and rest-only countdowns don't.
- **Metronome overrides** (`MetronomeService`):
  - `_override` is one-off (timer extend / rest countdown) and is cleared when a run ends.
  - `_step` / `_stepTraining` are pinned by a plan step and cleared by `PlanRunner.EndAsync`.
  - `_pieceTraining` lays a loaded piece's Start at / goal over the user's trainer (Start/End only) until another source loads. It's never saved, so opening a piece doesn't rewrite the user's trainer; switching the trainer on/off goes through `SetTrainingEnabledAsync`, which also sets the user's own switch.
  - A piece opens at `OpeningTempo`: its Start at, else the low end of the last logged range, else the last tempo, else the section tempo. Never the last tempo by default.
  - UI should read `Metro.Training` / `Metro.EffectiveIntervals`, not `Settings.*`, while a plan runs.
- **Engine events:** the engine reports `running=false` just before `finished`. Only `event == "stopped"` means a manual stop.
- **Files are CRLF on Windows checkouts.** Multi-line string replacements in scripts should normalise line endings.
