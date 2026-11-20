# Songler design guide

The house rules for how screens look and read. Follow them when building or changing UI, and add a rule here when a new decision is made, so screens don't drift apart. If a rule here and the code disagree, the code is wrong (or the rule needs updating: say so).

## Copy

- **No instruction text.** The UI speaks for itself. No "press X to…", "toggle with the … icon", keyboard hints, or sentences that explain what a screen is for. Tooltips name a control; they don't teach.
- **Information is fine:** summaries, totals, counts ("4 steps · 30 min · Not run yet").
- **Empty states:** a heading and the primary action, no explanatory sentence ("No plans yet" + "Create a plan").
- **Filtered to nothing:** "No matching entries" + a "Clear filters" button. Keep "No … yet" for when there's no data at all.
- **Pluralise** counts ("1 step", "2 steps").
- **"Piece", never "item".** A thing in the library is a *piece* in every bit of UI text ("Add piece", "All pieces", "Select a piece for step 2"). The code, routes and stored data say piece too (`Piece`, `PieceId`).
- **"Section", never "part".** A song is just another type of piece; don't word anything differently for songs.
- Sentence case everywhere ("Log practice", not "Log Practice").

## Numbers, times and dates

| What | Format | Helper |
| --- | --- | --- |
| Durations in text | `45 sec`, `10 min`, `1 hr`, `1 hr 5 min`: always `hr`, `min`, `sec` | `Stats.Duration(sec)` |
| Running clocks / countdowns | `m:ss`, `h:mm:ss` | `Stats.Clock(sec)` |
| Duration inputs | h:mm:ss as one field of three numbers with fixed colons | `<DurationInput>` (never number fields or `MudTimePicker`) |
| Chart axis ticks and tooltips | durations in round steps ("30 min", "1 hr 30 min"), same format as text | `BarChart Minutes` (C# formats every tick and tooltip and sends the text to charts.js) |
| Dates | `Sat 14 Nov`; add the year (`14 Nov 2025`) only outside the current year | |
| Relative dates | `today`, `yesterday`, `3 days ago`, `2w ago`, then a date | `Stats.Ago` |
| Tempo | `120 bpm`, `90%`, ranges `65–145` | |

Never hand-roll a duration format in a page. If `Stats.Duration` doesn't fit, change it there.

## Charts

- A chart's title says what's measured ("Days practised by weekday", "Weekly practice time"), not just how it's split ("By day of week").
- Everything on a page with a period picker follows that period. The practice calendar is the exception: it always shows the last 12 months so it doesn't change size.
- Cards side by side in a grid end level at the bottom (`equal-cards` on the `MudGrid`).
- Long ranked lists show their top 8 with "Show all".
- Charts are **Apache ECharts** (a trimmed bundle in `wwwroot/lib/echarts` built by `tools/echarts`, lazy-loaded) drawn by `wwwroot/js/charts.js`, through components on `ChartBase`. MudChart was tried and dropped: no reference lines, per-bar colours, whiskers or calendar extras. LiveCharts2 too (Skia canvas ignores CSS fonts, needs native WASM linking).
- All text (ticks, tooltips) is formatted in C# with `Stats.*` and sent as strings, so charts read the same as the rest of the app. Don't format numbers or durations in JS.
- Colours come from the theme's CSS tokens (`--series`, `--accent`, `--good`, `--hm-*`…), read when drawing; the spec includes the theme so a theme switch redraws. Tooltips are ECharts' HTML tooltips styled with the tokens: bold value, muted label below ("2 hr 35 min" / "Week of 9 Nov"), optional note.
- Goals are a dashed green line, with a small key above the chart ("Weekly goal 2 hr 30 min"). The current week's bar is accent. Tempo dots are coloured by feel, with a faint whisker down to the day's lowest tempo.
- Practice calendar: Monday-first columns, square cells sized to the width, untimed practice days on the first step, days before the selected period faded, today outlined in accent, Less → More key below.

## Layout

- **Sidebar:** Dashboard and Library at the top, then **Practice** (Metronome, Timer, Plans) and **Progress** (History, Stats) under small uppercase headings (`nav-heading stat-label`), then a divider and Settings & data. New screens go in the group that matches what they're for.
- **Page head:** `h1` on the left (optional overline above, optional info line below); the page's primary action on the right.
- **Toolbars** (search + filters above a list): search box first, then filters, selects at the end. Everything in a toolbar is `Variant.Outlined`, `Margin.Dense`.
- **Dashboard** is for starting practice, not for statistics. The header keeps this week's goal and the streak. Below, full-width rows of the same small cards (a piece: last practice line, goal bar, Metronome + Timer, star; a plan: steps · length, Run plan, star), up to 4 per row: **Jump back in** (latest practice per piece, plans by last run), **Favourites** (one dashed placeholder card while nothing is starred), **Closest to goal** (practised in the last 30 days, not there yet), **Not practised lately** (a goal to reach, untouched for 2+ weeks). A piece shows in one row only (Favourites first), empty rows hide, and on phones each row scrolls sideways. Charts and totals live on Stats; log entries on History.
- **Favourites:** a star (`FavouriteToggle`, `Piece.Favourite` / `PracticePlan.Favourite`), filled accent while starred, tooltip "Add to / Remove from favourites". On Library cards, beside a piece's title, on plan cards, and on the Dashboard's favourite cards to un-star.
- **Library view:** Cards or List, a two-icon toggle (`view-toggle`) in the toolbar, remembered in `AppSettings.LibraryList`. List rows (`piece-row`, columns `pr-cols` under a header): type icon, name, tempo as "150 / 180 bpm" (unit once), progress bar with % (or the `GoalChip` once reached), last practised, practice time; the row opens the piece and ends with the same actions as a card. On phones: name and tempo, actions underneath.
- **Cards** (`section.card`) with a `card-title`.
- **Practice stays on its page.** The Metronome and Timer share one engine, so only one runs at a time, and it runs only while you're on its page. There's no mini player. Leaving the page while it runs asks first ("Stop the metronome?", `PracticeGuard`); moving within the page (another piece) and plan steps don't. The practice shortcuts live on those pages too (`PracticeKeys`).
- **Settings a plan step sets** (speed trainer, intervals) show the step's values in the rail, locked: fields, the section's header switch and its strip toggle are disabled, and both say "Set by your plan for this step." The user's own settings are never changed by a plan and come back when it ends. Options the plan doesn't set (Audio cues) stay editable.
- **Trainer wording:** the number of tempo jumps is **Increments** (never "steps", which in plans means plan steps), and the ramp is **Over time** or **Every N bars**, in the rail and in plans alike.
- **A plan step's trainer** has the rail's options: From, To, Increments, and Over time (the step's work time) or Every N bars (`PlanStep.TrainerSettings()`). Switched on, it starts from the piece's Start at → goal (unless From/To were already changed); its summary reads "trainer 65 → 145 bpm" (", every 4 bars" when stepping by bars).
- **Plan steps:** leaving a step that's running or has practice time asks first: Log it (always through the log dialog), Discard or Keep going. Backing out of the log dialog stays on the step. Auto-log only applies to steps that reach their end; Finish step always asks.
- **Options rail** (`OptionsRail`, Metronome and Timer): the page's settings sit in a 320 px rail on the right, and the page itself is one centred column (`practice-col`).
  - The rail is for configuration only: no summaries, totals or explanations.
  - The source select (and Save or ⋮) goes at the top, then `RailSection`s.
  - A feature you switch on and off (Speed trainer, Intervals, Count-in, Play through) is a section whose whole header is its on/off switch (`role="switch"`): the icon sits in a circle lit while on, the same look as the strip buttons, and the title dims while off. Its fields show only while it's on. Switches inside a section are that feature's own options (Stop at end, Audio cues), with labels smaller than the section title. Other sections (Time signature, Sound, Countdown) are always open. Order: source, Time signature (free metronome), then the features, then Sound. Sections don't collapse.
  - Hiding the rail leaves a 56 px strip of quick controls rather than a copy of the sections: `RailToggle`s for the features, plus `RailVolume` (click turns sound on or off, hover shows the slider).
  - Below `lg` the same sections open as a bottom sheet from an Options button in the page head.
  - The rail starts open; hiding it lasts for the session.
  - Everything in the rail must fit its 320 px without a sideways scrollbar. Keep rows to two fields (`interval-grid`: Work · Rounds, then Rest).
  - Put `<OptionsRail>` last in the page markup. MudBlazor gives every `div` after a right-hand drawer a right margin the width of the drawer, so anything placed after it (e.g. the tab view) gets squeezed.
  - Tooltips on controls at the rail's right edge (Save, ⋮, strip buttons, Hide options) use `Placement.Left`. Below or above, they hit the window edge, wrap and jitter.
- Must work at phone width (≥ 360 px): wide tables switch to compact rows (`hide-phone` / `show-phone-block`).

## Settings

- Settings pages are a single column of sections (`settings-list`): a heading per section, then one row per setting (`setting-row`) with its name on the left and the control right-aligned. Rows are separated by hairlines; no cards.
- A row's description (`setting-desc`) is only for information the name can't carry ("Score files aren't included."), never for instructions.
- Controls in a row have no visible label of their own (the row names them), but still get an accessible name (`aria-label`, `DurationInput AriaLabel`).

## Controls

- **Prefer existing MudBlazor components** over hand-built ones: `MudProgressLinear` for bars, `MudTooltip` / a component's own tooltip options for tooltips, and so on. Configure them through their parameters and options first; only build something custom when MudBlazor has nothing that fits, and say why in a comment. Check the API at mudblazor.com (it renders client-side, so open it in a browser rather than fetching it). Charts are the exception (ECharts, see Charts).

- **Buttons:** at most one filled primary button per view (or per card when that card is the active one). Secondary actions are outlined; tertiary ones are text buttons.
- **Every icon-only button has a tooltip** (`MudTooltip`) naming what it does ("Move up", "Edit entry", "More"), plus an accessible name (`aria-label`, or `AriaLabel` on `MudMenu`) that can carry more context ("Move Verse up"). Tooltips never include keyboard shortcuts.
- **Confirmations** use `ConfirmDialog` (`DeleteAsync` for deletes, `ShowAsync` otherwise), never MudBlazor's message box. Destructive answers are red and the safe answer has focus. Delete wording: "“Name” will be deleted." + what else goes or stays + "This can't be undone." After a delete, a snackbar says "Deleted “Name”".
- **Destructive actions** (delete) go in a ⋮ menu, not next to everyday buttons, and always confirm. In an edit dialog, Delete is a red text button on the left of the actions, and confirms too.
- **No helper text under fields** in rows of fields: it pushes the field out of line. Examples go in the placeholder ("120, 65–145 or 85%"); a short note goes in the label ("Bars (loop)").
- **Menu items that open a dialog** end with "…" and start it via `AfterMenu(...)`, not by awaiting it in `OnClick` (MudMenu stays open until the handler returns).
- **Menus:** set the name with `AriaLabel="…"` on `MudMenu`. Plain `aria-label` lands on a wrapper and the button ends up unnamed.
- **On/off options:** icon toggles (`opt-toggle`, `aria-pressed`, tooltip "Name: on/off"), lit while on, or a rail section's header. Not "On"/"Off" text, and not checkboxes: an on/off field in a form is a `MudSwitch` with its label on the left (`rail-line-switch` in the rail). The rail's strip toggles are the hidden-rail stand-ins for the section switches, not extra copies on the page.
- **Sound on/off** is shown as on/off (lit = sound on), not as a mute button that lights up when muted. It's `Metro.Muted`, not saved; moving the volume slider turns the sound back on.
- **Selects:**
  - In toolbars and compact editor rows (plan steps, sections): `Variant.Outlined`, `Margin.Dense`, `Dense="true"`, fixed width via `toolbar-select` (ellipsis on overflow).
  - In forms (dialogs, settings panels): the default variant, matching the text fields beside them.
  - **Selects whose empty value means something** ("Free practice", "Full run-through") show it as an explicit first option, not as an empty field with helper text.
  - **Filter selects always show a value**: an explicit "All pieces" / "All sections" / "All time" option rather than an empty, clearable select. Empty outlined selects render their label as a placeholder, which looks different from their filled neighbours.
  - **Source pickers are selects** (what the Metronome or Timer has loaded: label "Metronome" / "Timer", first option "Free metronome" / "Free timer"), not "Load…" menus. Group headings inside a select are disabled items with `Class="select-group"`.
  - **Period pickers are selects** (label "Period", options "Last 30 days" … "All time"), not toggle groups. Segmented toggles are for switching a panel or a mode, not for ranges.
  - Option order: most relevant first (e.g. pieces by most recently logged), not alphabetical, unless the list is a fixed set.
- **Hide controls that don't apply; don't disable them.** A greyed-out field ("Rest" with 1 round) makes you work out why. Show it once it applies. Disable only when the control's presence matters (e.g. Move up on the first row), and then make the reason obvious.
- **A disabled primary button says why** next to it, phrased as what to do ("Select a piece for step 2", "Add a name"), so nothing is blocked silently.
- **Empty fields look alike:** a label resting inside an empty field is styled like a placeholder (text colour at 42%), so an unselected select and a placeholder read the same. Handled globally in `songler.css`.
- **Placeholders, not defaults:** example names ("My routine") are placeholders. Don't pre-fill a value the user has to delete. A useful suggestion (e.g. "128 bpm · 4/4" for a new custom metronome) goes in the placeholder too.
- **Asking for a name** (save as, rename): `PromptDialog.ShowAsync`. The field is focused with its text selected, Enter saves, and Save says why it's disabled ("Add a name", "You already have “…”" via `taken`).
- **Page actions beyond the main one go in a ⋮ menu** (e.g. a custom metronome: Save changes, Edit sections…, Rename…, Delete…). A page with a single action may show it as an icon button with a tooltip (Save as custom metronome).
- **Reorderable lists drag** by a grip handle (`DragIndicator`, `data-drag-handle` / `data-drag-row`, see `js/drag.js`), using the shared `DragReorder<T>` helper and the `edit-row` / `er-*` row classes (plan steps, sections). The list reorders live while dragging and the dragged row is outlined in accent (no browser ghost); Esc puts it back, and keep Move up / Move down buttons for keyboard and touch. Never attach a Blazor handler to `dragover` (it fires continuously and re-renders on every call); use `@ondragover:preventDefault` alone and update state on `dragenter` / `drop`.
- **Dialogs whose content grows** (editors with add buttons: piece, plan, sections, score import) open at the top (`Position = DialogPosition.TopCenter`), so adding rows grows them downwards and the add buttons stay under the pointer. Short dialogs stay centred.
- **Long editable lists collapse:** each row is a one-line summary that expands to its fields (plan steps, sections). New rows open expanded (a lone row opens straight away); summaries are their own components (`PlanStepSummary`, `SectionSummary`).
- **Sections:** length is a Loop toggle (Repeat icon, `opt-toggle`) plus Bars (1+) when not looping; stored as 0 bars = loop. Score-linked sections show their meter and bars as text ("4/4 · 32 bars · from the score") instead of disabled fields.
- **Rows that open something:** the whole row is clickable with a hover highlight. A pencil appears on hover and stays reachable by keyboard. Links inside the row still work.

## Icons

- MudBlazor Material **Rounded** set by default; Pictogrammers MDI (`Mdi.*`, see CLAUDE.md) for anything it lacks.
- Fixed meanings: metronome `Mdi.Metronome` · timer `HourglassBottom` · rest `SelfImprovement` · speed trainer `TrendingUp` · intervals `Timelapse` · count-in `HourglassTop` · log `AddTask` · warning `WarningAmber`.

## Colour

- Use the CSS tokens (`--ink`, `--ink-2`, `--muted`, `--accent`, `--accent-soft`, `--ring`, `--card-2`, MudBlazor palette vars). No literal colours in pages.
- Accent (orange) means primary action, current or active state. Warning (amber) means something needs attention, e.g. a step whose piece was deleted. Blue is for data (chart bars, ranked bars, the practice heatmap) and for rest phases.

## Shared components

Reuse these instead of rebuilding them. When two screens show the same thing, they share one component.

| Component | Used for |
| --- | --- |
| `ScoreImportDialog` (`ScoreImportDialog.Title`) | Guitar Pro / MusicXML import; every entry point uses `Title` for its label |
| `IntervalsEditor` | Work / rest / rounds, a "Load preset" select (an action: it always reads "Load preset"; choices from Settings > Interval presets, `IntervalPresetsEditor`) and Audio cues (Metronome intervals, Timer countdown) |
| `OptionsRail`, `RailSection`, `RailToggle`, `RailVolume` | The Metronome / Timer options rail, its sections, and the hidden rail's strip controls |
| `VolumeControl` | Sound on/off + volume slider (rail Sound section, strip pop-up) |
| `PracticeGuard`, `PracticeKeys` | On the Metronome and Timer pages: ask before leaving while running; the practice shortcuts |
| `RoundsInput` | Interval rounds: a Repeat toggle ("until stopped", stored as 0) else a count from 1; turning Repeat off restores the last count. In the interval editor the row is Work · Rounds, with Rest shown only when there's more than one round |
| `BootScreen` (+ `wwwroot/index.html`) | The loading screen: ♪ mark, name, tagline, a slim accent bar (download % from Blazor, then an indeterminate bar while the library loads) and "Everything stays on this device". The two halves share `.boot` styles in plain colours (the theme isn't applied yet); `html.boot-light` comes from the saved setting, read from IndexedDB by a script in index.html. Keep the two in step |
| `FavouriteToggle` | The star that adds a piece or plan to the Dashboard's Favourites |
| `SessionPanel` | Practised time on the Metronome and Timer, inside the stage. Hidden until there's time; one line while playing; once stopped, Log + Discard + page extras (Timer: +5 min, +10 min, Rest). Hidden while a plan runs. |
| `DurationInput` | Any duration field: hours, minutes and seconds as separate numbers with fixed colons (like `MeterInput`). ↑/↓ step the number you're in, ←/→ and Backspace move between them, ":" or a digit typed into a full number moves on |
| `DivisionIcon` (`NoteIcons`) | A division shown as notation wherever it's read (tempo line, section summaries, piece page): the note value's icon, a small 3 for triplets, and a tooltip / accessible name that spells it out ("Eighth-note triplets (1/8t)"). In the Division select it sits beside the text (`ShowLabel`, no tooltip). Notes come from MDI; the thirty-second is hand-made in `NoteIcons` |
| `MeterInput` | Any time signature: one outlined field "7 / 8", each number typed or stepped with ↑/↓, ←/→ to move between them (the bottom doubles/halves and snaps to a note value: 1, 2, 4, 8, 16, 32). Pair it with a "Division" select from `Section.Divisions(unit)` (note values, 1/4 … 1/32, triplets to 1/16t) |
| `PlanStepSummary` | One-line plan step (Plans list, plan editor rows) |
| `SectionsEditor`, `SectionSummary` | Tempo-map editor (piece editor, custom metronome sections): header with count, quick-add chips, collapsible rows |
| `LogTable` | Practice log lists (History, Piece detail) |
| `GoalBar` | Goal progress bar (`MudProgressLinear`: accent, green when reached) |
| `GoalChip` | "Goal reached" / "+15 over" (green), wherever a piece's goal shows: Library and Dashboard cards, the piece page's Progress tile (with **Raise goal…**). Progress percentages aren't capped, so past the goal reads 111% |
| `StatTile` | Headline numbers (pass `Seconds` for durations so the unit words render small) |
| `FeelHint`, `EffortSlider` | Feel / effort display and input |
| `Charts/*` | ECharts components on `ChartBase`: `BarChart` (`Minutes`, `Goal` + `GoalLabel`, `HighlightLast`, `TipLabels`), `TempoChart` (whiskers, feel dots, goal), `Heatmap` (practice calendar: `Practiced`, `From`); `HBars` ranked lists on `MudProgressLinear` (`Limit` + "Show all", `Goal` for goal colouring) |
