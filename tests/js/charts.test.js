// charts.js loading the ECharts library: GitHub Pages answers 503 now and then, and a chart that can't load must say so
// in its box rather than throw (the error reached Blazor and stopped the whole app).
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

// jsdom doesn't fetch scripts: answer each <script> added to <head> with the next outcome, as the browser would
let outcomes, tries;
function answerScripts() {
    const append = document.head.appendChild.bind(document.head);
    vi.spyOn(document.head, 'appendChild').mockImplementation(node => {
        append(node);
        if (node.tagName === 'SCRIPT') {
            tries++;
            const ok = outcomes.shift();
            queueMicrotask(() => {
                if (ok) { window.echarts = fakeECharts; node.onload(); } else node.onerror();
            });
        }
        return node;
    });
}

const fakeChart = { setOption: vi.fn(), resize() { }, on() { }, off() { }, dispose() { }, getZr: () => ({ on() { }, off() { } }) };
const fakeECharts = { init: vi.fn(() => fakeChart), graphic: { LinearGradient: function () { } } };

let charts;
beforeEach(async () => {
    vi.resetModules();
    vi.useFakeTimers();
    delete window.echarts;
    globalThis.ResizeObserver ??= class { observe() { } disconnect() { } };
    tries = 0;
    answerScripts();
    charts = await import('../../src/Templing/wwwroot/js/charts.js');
});
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

const spec = { labels: ['a', 'b'], values: [1, 2], tips: ['', ''], yMin: 0, yMax: 2, yStep: 1, yLabels: {} };

describe('charts.js loading the chart library', () => {
    it('tries again after a failed download, and draws', async () => {
        outcomes = [false, true];
        const el = document.createElement('div');
        const drawn = charts.bar(el, spec);
        await vi.runAllTimersAsync();
        await drawn;
        expect(tries).toBe(2);
        expect(fakeECharts.init).toHaveBeenCalledWith(el, null, { renderer: 'svg' });
        expect(el.textContent).not.toMatch(/couldn.t load/);
    });

    it("says so in the chart's box when every try fails, without throwing", async () => {
        outcomes = Array(charts.RETRY_MS.length + 1).fill(false);
        const el = document.createElement('div');
        const drawn = charts.bar(el, spec);
        await vi.runAllTimersAsync();
        await expect(drawn).resolves.toBeUndefined();
        expect(tries).toBe(charts.RETRY_MS.length + 1);
        expect(el.querySelector('.viz-empty').textContent).toMatch(/couldn.t load/);
    });

    it('loads again for a later chart, and clears the message when it draws there', async () => {
        outcomes = [...Array(charts.RETRY_MS.length + 1).fill(false), true];
        const el = document.createElement('div');
        let drawn = charts.bar(el, spec);
        await vi.runAllTimersAsync(); await drawn;
        drawn = charts.bar(el, spec);
        await vi.runAllTimersAsync(); await drawn;
        expect(el.querySelector('.viz-empty')).toBeNull();
        expect(fakeECharts.init).toHaveBeenCalled();
    });
});
