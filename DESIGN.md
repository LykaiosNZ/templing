# Songler design guide

The house rules for how screens look and read. Follow them when building or changing UI, and add a rule here when a new decision is made, so screens don't drift apart. If a rule here and the code disagree, the code is wrong (or the rule needs updating: say so).

## Copy

- **No instruction text.** The UI speaks for itself. No "press X to…", "toggle with the … icon", keyboard hints, or sentences that explain what a screen is for. Tooltips name a control; they don't teach.
- **Information is fine:** summaries, totals, counts ("4 steps · 30 min · Not run yet").
- **Empty states:** a heading and the primary action, no explanatory sentence ("No plans yet" + "Create a plan").
- **Filtered to nothing:** "No matching entries" + a "Clear filters" button. Keep "No … yet" for when there's no data at all.
- **Pluralise** counts ("1 step", "2 steps").
- **"Section", never "part".** A song is just another item type; don't word anything differently for songs. Item-level text says "item", not "song".
- Sentence case everywhere ("Log practice", not "Log Practice").

## Numbers, times and dates

| What | Format | Helper |
| --- | --- | --- |
| Durations in text | `45 sec`, `10 min`, `1 hr`, `1 hr 5 min`: always `hr`, `min`, `sec` | `Stats.Duration(sec)` |
| Running clocks / countdowns | `m:ss`, `h:mm:ss` | `Stats.Clock(sec)` |
| Duration inputs | h:mm:ss text field | `<DurationInput>` (never number fields or `MudTimePicker`) |
| Chart axis ticks | `100 min`, or whole hours (`2 hr`) once the scale is large | `BarChart Minutes` |
| Dates | `Sat 14 Nov`; add the year (`14 Nov 2025`) only outside the current year | |
| Relative dates | `today`, `yesterday`, `3 days ago`, `2w ago`, then a date | `Stats.Ago` |
| Tempo | `120 bpm`, `90%`, ranges `65–145` | |

Never hand-roll a duration format in a page. If `Stats.Duration` doesn't fit, change it there.

## Charts

- A chart's title says what's measured ("Days practised by weekday", "Weekly practice time"), not just how it's split ("By day of week").
- Everything on a page with a period picker follows that period. The practice calendar keeps a fixed 12 months so it doesn't change size; days outside the period are faded.
- Cards side by side in a grid end level at the bottom (`equal-cards` on the `MudGrid`).
- Long ranked lists show their top 8 with "Show all".

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
  - **Filter selects always show a value**: an explicit "All items" / "All parts" / "All time" option rather than an empty, clearable select. Empty outlined selects render their label as a placeholder, which looks different from their filled neighbours.
  - **Period pickers are selects** (label "Period", options "Last 30 days" … "All time"), not toggle groups. Segmented toggles are for switching a panel or a mode, not for ranges.
  - Option order: most relevant first (e.g. items by most recently logged), not alphabetical, unless the list is a fixed set.
- **Hide controls that don't apply; don't disable them.** A greyed-out field ("Rest" with 1 round) makes you work out why. Show it once it applies. Disable only when the control's presence matters (e.g. Move up on the first row), and then make the reason obvious.
- **A disabled primary button says why** next to it, phrased as what to do ("Select an item for step 2", "Add a name"), so nothing is blocked silently.
- **Placeholders, not defaults:** example names ("My routine") are placeholders. Don't pre-fill a value the user has to delete.
- **Reorderable lists drag** by a grip handle (`DragIndicator`, `data-drag-handle` / `data-drag-row`, see `js/drag.js`), and keep Move up / Move down buttons for keyboard and touch.
- **Long editable lists collapse:** each row is a one-line summary that expands to its fields (plan steps). New rows open expanded; the summary uses the same component as the read-only view (`PlanStepSummary`).
- **Rows that open something:** the whole row is clickable with a hover highlight. A pencil appears on hover and stays reachable by keyboard. Links inside the row still work.

## Icons

- MudBlazor Material **Rounded** set by default; Pictogrammers MDI (`Mdi.*`, see CLAUDE.md) for anything it lacks.
- Fixed meanings: metronome `Mdi.Metronome` · timer `HourglassBottom` · rest `SelfImprovement` · speed trainer `TrendingUp` · intervals `Timelapse` · count-in `HourglassTop` · log `AddTask` · warning `WarningAmber`.

## Colour

- Use the CSS tokens (`--ink`, `--ink-2`, `--muted`, `--accent`, `--accent-soft`, `--ring`, `--card-2`, MudBlazor palette vars). No literal colours in pages.
- Accent (orange) means primary action, current or active state. Warning (amber) means something needs attention, e.g. a step whose item was deleted. Blue is for data (chart bars, ranked bars, the practice heatmap) and for rest phases.

## Shared components

Reuse these instead of rebuilding them. When two screens show the same thing, they share one component.

| Component | Used for |
| --- | --- |
| `ScoreImportDialog` (`ScoreImportDialog.Title`) | Guitar Pro / MusicXML import; every entry point uses `Title` for its label |
| `IntervalsEditor` | Work / rest / rounds + presets (Metronome intervals, Timer countdown) |
| `DurationInput` | Any duration field |
| `PlanStepSummary` | One-line plan step (Plans list, plan editor rows) |
| `LogTable` | Practice log lists (History, Item detail) |
| `StatTile` | Headline numbers (pass `Seconds` for durations so the unit words render small) |
| `FeelHint`, `EffortSlider` | Feel / effort display and input |
| `Charts/*` | Tempo line, bar chart, heatmap, ranked bars (`HBars Limit` for long lists with "Show all"; `BarChart Minutes` for minute values: ticks in min, whole hr once the scale is large) |
