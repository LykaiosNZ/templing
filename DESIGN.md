# Songler design guide

The house rules for how screens look and read. Follow them when building or changing UI, and add a rule here when a new decision is made, so screens don't drift apart. If a rule here and the code disagree, the code is wrong (or the rule needs updating: say so).

## Copy

- **No instruction text.** The UI speaks for itself. No "press X to…", "toggle with the … icon", keyboard hints, or sentences that explain what a screen is for. Tooltips name a control; they don't teach.
- **Information is fine:** summaries, totals, counts ("4 steps · 30 min · Not run yet").
- **Empty states:** a heading and the primary action, no explanatory sentence ("No plans yet" + "Create a plan").
- **Filtered to nothing:** "No matching entries" + a "Clear filters" button. Keep "No … yet" for when there's no data at all.
- **Pluralise** counts ("1 step", "2 steps").
- Sentence case everywhere ("Log practice", not "Log Practice").

## Numbers, times and dates

| What | Format | Helper |
| --- | --- | --- |
| Durations in text | `45 s`, `10 min`, `1 h`, `1 h 5 min` | `Stats.Duration(sec)` |
| Running clocks / countdowns | `m:ss`, `h:mm:ss` | `Stats.Clock(sec)` |
| Duration inputs | h:mm:ss text field | `<DurationInput>` (never number fields or `MudTimePicker`) |
| Chart axis ticks | compact (`100m`) is fine; tooltips use the full format | |
| Dates | `Sat 14 Nov`; add the year (`14 Nov 2025`) only outside the current year | |
| Relative dates | `today`, `yesterday`, `3 days ago`, `2w ago`, then a date | `Stats.Ago` |
| Tempo | `120 bpm`, `90%`, ranges `65–145` | |

Never hand-roll a duration format in a page. If `Stats.Duration` doesn't fit, change it there.

## Layout

- **Page head:** `h1` on the left (optional overline above, optional info line below); the page's primary action on the right.
- **Toolbars** (search + filters above a list): search box first, then filters, selects at the end. Everything in a toolbar is `Variant.Outlined`, `Margin.Dense`.
- **Cards** (`section.card`) with a `card-title`. A card's on/off state shows as a dot after the title (`seg-dot`), not as text.
- Must work at phone width (≥ 360 px): wide tables switch to compact rows (`hide-phone` / `show-phone-block`).

## Controls

- **Buttons:** at most one filled primary button per view (or per card when that card is the active one). Secondary actions are outlined; tertiary ones are text buttons.
- **Destructive actions** (delete) go in a ⋮ menu, not next to everyday buttons, and always confirm.
- **Menus:** set the name with `AriaLabel="…"` on `MudMenu`. Plain `aria-label` lands on a wrapper and the button ends up unnamed.
- **On/off options:** icon toggles (`opt-toggle`, `aria-pressed`, tooltip "Name: on/off") or dots. Not "On"/"Off" text, and no duplicate switches for the same setting.
- **Selects:**
  - In toolbars and compact editor rows (plan steps, sections): `Variant.Outlined`, `Margin.Dense`, `Dense="true"`, fixed width via `toolbar-select` (ellipsis on overflow).
  - In forms (dialogs, settings panels): the default variant, matching the text fields beside them.
  - **Filter selects always show a value**: an explicit "All items" / "All parts" / "All time" option rather than an empty, clearable select. Empty outlined selects render their label as a placeholder, which looks different from their filled neighbours.
  - Option order: most relevant first (e.g. items by most recently logged), not alphabetical, unless the list is a fixed set.
- **Rows that open something:** the whole row is clickable with a hover highlight. A pencil appears on hover and stays reachable by keyboard. Links inside the row still work.

## Icons

- MudBlazor Material **Rounded** set by default; Pictogrammers MDI (`Mdi.*`, see CLAUDE.md) for anything it lacks.
- Fixed meanings: metronome `Mdi.Metronome` · timer `HourglassBottom` · rest `SelfImprovement` · speed trainer `TrendingUp` · intervals `Timelapse` · count-in `HourglassTop` · log `AddTask` · warning `WarningAmber`.

## Colour

- Use the CSS tokens (`--ink`, `--ink-2`, `--muted`, `--accent`, `--accent-soft`, `--ring`, `--card-2`, MudBlazor palette vars). No literal colours in pages.
- Accent (orange) means primary action, current or active state. Warning (amber) means something needs attention, e.g. a step whose item was deleted. Blue is reserved for rest phases and the practice heatmap.

## Shared components

Reuse these instead of rebuilding them. When two screens show the same thing, they share one component.

| Component | Used for |
| --- | --- |
| `IntervalsEditor` | Work / rest / rounds + presets (Metronome intervals, Timer countdown) |
| `DurationInput` | Any duration field |
| `LogTable` | Practice log lists (History, Item detail) |
| `StatTile` | Headline numbers |
| `FeelHint`, `EffortSlider` | Feel / effort display and input |
| `Charts/*` | Tempo line, bar chart, heatmap, ranked bars |
