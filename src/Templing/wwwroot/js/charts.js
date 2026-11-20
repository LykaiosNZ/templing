// Charts via Apache ECharts (Apache-2.0, vendored in lib/echarts), loaded on first use.
// C# sends data plus pre-formatted text (tooltips, axis labels) so number and duration formats stay in one place (Stats.cs).
// Marks use the theme's CSS tokens, read when drawing (C# redraws on theme change). The HTML tooltip uses the tokens
// directly, so it follows the app font and theme.

const LIB = 'lib/echarts/echarts.min.js';
let loading = null;
const charts = new WeakMap(); // element → { chart, observer, relayout? }

function loadECharts() {
    if (window.echarts) return Promise.resolve(window.echarts);
    if (loading) return loading;
    loading = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = LIB;
        s.onload = () => resolve(window.echarts);
        s.onerror = () => { loading = null; reject(new Error('Could not load the chart library.')); };
        document.head.appendChild(s);
    });
    return loading;
}

function tokens(el) {
    const cs = getComputedStyle(el);
    const v = name => cs.getPropertyValue(name).trim();
    return {
        font: cs.fontFamily, series: v('--series'), accent: v('--accent'), good: v('--good'), card: v('--card'),
        muted: v('--muted'), grid: v('--grid'), axis: v('--axis'), ring: v('--ring'),
        hm: [0, 1, 2, 3, 4].map(i => v(`--hm-${i}`)),
    };
}

async function chartFor(el) {
    const echarts = await loadECharts();
    let entry = charts.get(el);
    if (!entry) {
        const chart = echarts.init(el, null, { renderer: 'svg' });
        entry = { chart, relayout: null };
        entry.observer = new ResizeObserver(() => { entry.relayout?.(); chart.resize(); });
        entry.observer.observe(el);
        charts.set(el, entry);
    }
    entry.relayout = null;
    return entry;
}

const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

// Tooltip box: bold value, muted label underneath, optional note.
const tipHtml = (title, sub, note) =>
    `<b>${esc(title)}</b>` +
    (sub ? `<br><span style="color:var(--muted)">${esc(sub)}</span>` : '') +
    (note ? `<br><span style="color:var(--ink-2);white-space:normal;display:inline-block;max-width:240px">${esc(note)}</span>` : '');

function tooltip(t) {
    return {
        backgroundColor: 'var(--card)',
        borderColor: 'var(--axis)',
        borderWidth: 1,
        padding: [6, 10],
        confine: true,
        textStyle: { color: 'var(--ink)', fontFamily: t.font, fontSize: 13 },
        extraCssText: 'border-radius:8px; box-shadow:0 6px 20px rgba(0,0,0,.18);',
    };
}

function valueAxis(t, spec) {
    return {
        type: 'value',
        min: spec.yMin ?? 0, max: spec.yMax ?? null, interval: spec.yStep ?? null,
        splitLine: { lineStyle: { color: t.grid } },
        axisLabel: { color: t.muted, fontSize: 12, formatter: v => spec.yLabels?.[v] ?? String(v) },
    };
}

function goalLine(t, goal) {
    return goal == null ? undefined : {
        symbol: 'none', silent: true, label: { show: false },
        lineStyle: { color: t.good, type: 'dashed', width: 1.5 },
        data: [{ yAxis: goal }],
    };
}

/**
 * Column chart.
 * spec: { labels[], values[], tips[] (tooltip value), subs[]? (tooltip label, default labels), yMax, yStep, yLabels,
 *         goal?, highlightLast?, labelEvery? }
 */
export async function bar(el, spec) {
    const { chart } = await chartFor(el);
    const t = tokens(el);
    const last = spec.values.length - 1;
    chart.setOption({
        animationDuration: 300,
        textStyle: { fontFamily: t.font },
        grid: { left: 4, right: 8, top: 12, bottom: 4, containLabel: true },
        xAxis: {
            type: 'category', data: spec.labels,
            axisTick: { show: false },
            axisLine: { lineStyle: { color: t.axis } },
            // 'auto' spaces labels by their width; labelEvery > 1 forces a fixed step
            axisLabel: { color: t.muted, fontSize: 12, interval: (spec.labelEvery ?? 1) > 1 ? spec.labelEvery - 1 : 'auto' },
        },
        yAxis: valueAxis(t, spec),
        tooltip: {
            ...tooltip(t),
            trigger: 'axis',
            axisPointer: { type: 'shadow', shadowStyle: { color: t.ring } },
            formatter: ps => {
                const i = ps[0].dataIndex;
                return tipHtml(spec.tips[i], (spec.subs ?? spec.labels)[i]);
            },
        },
        series: [{
            type: 'bar',
            data: spec.values.map((v, i) => spec.highlightLast && i === last ? { value: v, itemStyle: { color: t.accent } } : v),
            itemStyle: { color: t.series, borderRadius: [3, 3, 0, 0] },
            barMaxWidth: 48,
            barCategoryGap: '30%',
            emphasis: { disabled: true },
            markLine: goalLine(t, spec.goal),
        }],
    }, true);
}

/**
 * Tempo over time: a line through each day's top tempo, whiskers down to the day's lowest tempo, dots coloured by feel.
 * spec: { points: [{ date: 'yyyy-MM-dd', value, min?, color?, tip, sub, note? }], yMin, yMax, yStep, yLabels, goal? }
 */
export async function tempo(el, spec) {
    const { chart } = await chartFor(el);
    const t = tokens(el);
    const pts = spec.points;
    const many = pts.length > 40;
    chart.setOption({
        animationDuration: 300,
        textStyle: { fontFamily: t.font },
        grid: { left: 4, right: 12, top: 12, bottom: 4, containLabel: true },
        xAxis: {
            type: 'time',
            axisTick: { show: false },
            axisLine: { lineStyle: { color: t.axis } },
            splitLine: { show: false },
            axisLabel: { color: t.muted, fontSize: 12, hideOverlap: true, formatter: v => window.echarts.time.format(v, '{d} {MMM}', false) },
        },
        yAxis: valueAxis(t, spec),
        tooltip: {
            ...tooltip(t),
            trigger: 'axis',
            axisPointer: { type: 'line', lineStyle: { color: t.axis } },
            formatter: ps => {
                const p = ps.find(x => x.seriesType === 'line');
                if (!p) return '';
                const d = pts[p.dataIndex];
                return tipHtml(d.tip, d.sub, d.note);
            },
        },
        series: [
            {
                // range whiskers (lowest → top tempo of the day)
                type: 'custom',
                silent: true,
                data: pts.filter(p => p.min != null && p.min < p.value).map(p => [p.date, p.min, p.value]),
                encode: { x: 0, y: [1, 2] },
                renderItem: (params, api) => {
                    const a = api.coord([api.value(0), api.value(1)]);
                    const b = api.coord([api.value(0), api.value(2)]);
                    return { type: 'line', shape: { x1: a[0], y1: a[1], x2: b[0], y2: b[1] }, style: { stroke: t.series, lineWidth: 1.5, opacity: 0.3 } };
                },
                z: 1,
            },
            {
                type: 'line',
                data: pts.map(p => ({
                    value: [p.date, p.value],
                    itemStyle: p.color ? { color: p.color, borderColor: p.color } : undefined,
                    symbolSize: p.color ? 9 : many ? 5 : 7,
                })),
                lineStyle: { color: t.series, width: 2 },
                itemStyle: { color: t.series, borderColor: t.series },
                symbol: 'circle',
                showAllSymbol: true,
                emphasis: { scale: 1.4 },
                markLine: goalLine(t, spec.goal),
                z: 2,
            },
        ],
    }, true);
}

/**
 * Practice calendar: one column per week (Monday first), square cells sized to the width.
 * spec: { start: 'yyyy-MM-dd' (a Monday), end: 'yyyy-MM-dd' (today), weeks,
 *         hideMonth?: 'yyyy-M' (a month cut off at the start, its label would collide), days: [{ date, level (0–4), tip, sub, faded?, today? }] }
 */
export async function calendar(el, spec) {
    const entry = await chartFor(el);
    const t = tokens(el);
    const label = 34, top = 20, gap = 3;
    const cellFor = () => Math.max(8, Math.min(28, Math.floor((el.clientWidth - label - 4) / spec.weeks)));
    // once cells hit their cap the grid is narrower than the card: centre it, and line the legend up with its right edge
    const insetFor = cell => Math.max(0, Math.floor((el.clientWidth - label - 4 - cell * spec.weeks) / 2));

    const option = (cell, left) => ({
        animation: false,
        textStyle: { fontFamily: t.font },
        tooltip: {
            ...tooltip(t),
            trigger: 'item',
            formatter: p => { const d = spec.days[p.dataIndex]; return tipHtml(d.tip, d.sub); },
        },
        calendar: {
            range: [spec.start, spec.end],
            orient: 'horizontal',
            left, top, cellSize: [cell, cell],
            splitLine: { show: false },
            itemStyle: { color: 'transparent', borderWidth: 0 },
            dayLabel: { firstDay: 1, nameMap: ['', 'Mon', '', 'Wed', '', 'Fri', ''], color: t.muted, fontSize: 11, margin: 8 },
            monthLabel: { color: t.muted, fontSize: 11, margin: 6, align: 'left', formatter: p => `${p.yyyy}-${+p.M}` === spec.hideMonth ? '' : p.nameMap },
            yearLabel: { show: false },
        },
        series: [{
            type: 'heatmap',
            coordinateSystem: 'calendar',
            data: spec.days.map(d => ({
                value: [d.date, d.level],
                itemStyle: {
                    color: t.hm[d.level],
                    opacity: d.faded ? 0.35 : 1,
                    borderColor: d.today ? t.accent : t.card,
                    borderWidth: d.today ? 2 : gap / 2,
                    borderRadius: 3,
                },
            })),
            emphasis: { itemStyle: { borderColor: t.axis, borderWidth: 2 } },
        }],
    });

    const draw = () => {
        const cell = cellFor(), inset = insetFor(cell);
        el.style.height = `${top + cell * 7 + 4}px`;
        el.parentElement?.style.setProperty('--hm-inset', `${inset}px`);
        return { cell, inset };
    };
    let layout = draw();
    entry.chart.resize();
    entry.chart.setOption(option(layout.cell, label + layout.inset), true);
    entry.relayout = () => {
        const cell = cellFor();
        if (cell === layout.cell && insetFor(cell) === layout.inset) return;
        layout = draw();
        entry.chart.setOption(option(layout.cell, label + layout.inset), true);
    };
}

export function dispose(el) {
    const entry = charts.get(el);
    if (!entry) return;
    entry.observer.disconnect();
    entry.chart.dispose();
    charts.delete(el);
}
