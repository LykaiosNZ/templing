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
| Duration inputs | h:mm:ss text field | `<DurationInput>` (never number fields or `MudTimePicker`) |
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
- Charts are **Apache ECharts** (vendored in `wwwroot/lib/echarts`, lazy-loaded) drawn by `wwwroot/js/charts.js`, through components on `ChartBase`. MudChart was tried and dropped: no reference lines, per-bar colours, whiskers or calendar extras. LiveCharts2 too (Skia canvas ignores CSS fonts, needs native WASM linking).
- All text (ticks, tooltips) is formatted in C# with `Stats.*` and sent as strings, so charts read the same as the rest of the app. Don't format numbers or durations in JS.
- Colours come from the theme's CSS tokens (`--series`, `--accent`, `--good`, `--hm-*`…), read when drawing; the spec includes the theme so a theme switch redraws. Tooltips are ECharts' HTML tooltips styled with the tokens: bold value, muted label below ("2 hr 35 min" / "Week of 9 Nov"), optional note.
- Goals are a dashed green line, with a small key above the chart ("Weekly goal 2 hr 30 min"). The current week's bar is accent. Tempo dots are coloured by feel, with a faint whisker down to the day's lowest tempo.
- Practice calendar: Monday-first columns, square cells sized to the width, untimed practice days on the first step, days before the selected period faded, today outlined in accent, Less → More key below.

## Layout

- **Page head:** `h1` on the left (optional overline above, optional info line below); the page's primary action on the right.
- **Toolbars** (search + filters above a list): search box first, then filters, selects at the end. Everything in a toolbar is `Variant.Outlined`, `Margin.Dense`.
- **Cards** (`section.card`) with a `card-title`. A card's on/off state shows as a dot after the title (`seg-dot`), not as text.
- Must work at phone width (≥ 360 px): wide tables switch to compact rows (`hide-phone` / `show-phone-block`).

## Settings

- Settings pages are a single column of sections (`settings-list`): a heading per section, then one row per setting (`setting-row`) with its name on the left and the control right-aligned. Rows are separated by hairlines; no cards.
- A row's description (`setting-desc`) is only for information the name can't carry ("Score files aren't included."), never for instructions.
- Controls in a row have no visible label of their own (the row names them), but still get an accessible name (`aria-label`, `DurationInput AriaLabel`).

## Controls

- **Prefer existing MudBlazor components** over hand-built ones: `MudProgressLinear` for bars, `MudTooltip` / a component's own tooltip options for tooltips, and so on. Configure them through their parameters and options first; only build something custom when MudBlazor has nothing that fits, and say why in a comment. Check the API at mudblazor.com (it renders client-side, so open it in a browser rather than fetching it). Charts are the exception (ECharts, see Charts).

- **Buttons:** at most one filled primary button per view (or per card when that card is the active one). Secondary actions are outlined; tertiary ones are text buttons.
- **Every icon-only button has a tooltip** (`MudTooltip`) naming what it does ("Move up", "Edit entry", "More"), plus an accessible name (`aria-label`, or `AriaLabel` on `MudMenu`) that can carry more context ("Move Verse up"). Tooltips never include keyboard shortcuts.
- **Destructive actions** (delete) go in a ⋮ menu, not next to everyday buttons, and always confirm. In an edit dialog, Delete is a red text button on the left of the actions, and confirms too.
- **No helper text under fields** in rows of fields: it pushes the field out of line. Examples go in the placeholder ("120, 65–145 or 85%"); a short note goes in the label ("Bars (loop)").
- **Menu items that open a dialog** end with "…" and start it via `AfterMenu(...)`, not by awaiting it in `OnClick` (MudMenu stays open until the handler returns).
- **Menus:** set the name with `AriaLabel="…"` on `MudMenu`. Plain `aria-label` lands on a wrapper and the button ends up unnamed.
- **On/off options:** icon toggles (`opt-toggle`, `aria-pressed`, tooltip "Name: on/off") or dots. Not "On"/"Off" text, and no duplicate switches for the same setting.
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
- **Reorderable lists drag** by a grip handle (`DragIndicator`, `data-drag-handle` / `data-drag-row`, see `js/drag.js`). The list reorders live while dragging and the dragged row is outlined in accent (no browser ghost); Esc puts it back, and keep Move up / Move down buttons for keyboard and touch. Never attach a Blazor handler to `dragover` (it fires continuously and re-renders on every call); use `@ondragover:preventDefault` alone and update state on `dragenter` / `drop`.
- **Dialogs whose content grows** (editors with add buttons: piece, plan, sections, score import) open at the top (`Position = DialogPosition.TopCenter`), so adding rows grows them downwards and the add buttons stay under the pointer. Short dialogs stay centred.
- **Long editable lists collapse:** each row is a one-line summary that expands to its fields (plan steps). New rows open expanded; the summary uses the same component as the read-only view (`PlanStepSummary`).
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
| `IntervalsEditor` | Work / rest / rounds + presets (Metronome intervals, Timer countdown) |
| `DurationInput` | Any duration field |
| `PlanStepSummary` | One-line plan step (Plans list, plan editor rows) |
| `LogTable` | Practice log lists (History, Piece detail) |
| `GoalBar` | Goal progress bar (`MudProgressLinear`: accent, green when reached) |
| `StatTile` | Headline numbers (pass `Seconds` for durations so the unit words render small) |
| `FeelHint`, `EffortSlider` | Feel / effort display and input |
| `Charts/*` | ECharts components on `ChartBase`: `BarChart` (`Minutes`, `Goal` + `GoalLabel`, `HighlightLast`, `TipLabels`), `TempoChart` (whiskers, feel dots, goal), `Heatmap` (practice calendar: `Practiced`, `From`); `HBars` ranked lists on `MudProgressLinear` (`Limit` + "Show all", `Goal` for goal colouring) |
