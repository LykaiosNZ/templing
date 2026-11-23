// The shared SVG shapes, and the Timer's ring (countdown.js) that draws with them.
import { describe, it, expect, beforeEach } from 'vitest';
import { sector, annulus, bar, setD, countdownRing, countdownBar, svgEl, RING, ROUND } from '../../src/Templing/wwwroot/js/shapes.js';
import { frame } from '../../src/Templing/wwwroot/js/countdown.js';

// the numbers in a path, to check its geometry
const nums = d => d.match(/-?\d+(\.\d+)?/g).map(Number);

describe('sector', () => {
    it('is inset by half the rounding stroke, so it keeps its size', () => {
        const d = sector(0, 90, 100, 50);
        const [x0, y0] = nums(d.slice(1));
        expect(Math.hypot(x0, y0)).toBeCloseTo(100 - ROUND / 2, 1);
        expect(d).toMatch(/^M.*A98\.8 98\.8 0 0 1 .*L.*A51\.2 51\.2 0 0 0 .*Z$/);
    });

    it('uses the large arc past half way round', () => {
        expect(sector(0, 270, 100, 50)).toContain('A98.8 98.8 0 1 1');
        expect(sector(0, 170, 100, 50)).toContain('A98.8 98.8 0 0 1');
    });

    it('is nothing when too thin to draw', () => {
        expect(sector(10, 10.5, 100, 50)).toBe('');
    });
});

describe('annulus', () => {
    it('is two whole circles, the inner one the other way round (a hole)', () => {
        const d = annulus(100, 50);
        expect(d).toBe('M98.8 0A98.8 98.8 0 1 1 -98.8 0A98.8 98.8 0 1 1 98.8 0Z'
            + 'M51.2 0A51.2 51.2 0 1 0 -51.2 0A51.2 51.2 0 1 0 51.2 0Z');
    });
});

describe('bar', () => {
    it('is a rounded rectangle inside x0..x1', () => {
        const d = bar(0, 100, 0, 10);
        const xs = nums(d).filter((_, i) => i % 2 === 0);
        expect(d.startsWith('M')).toBe(true);
        expect(d.endsWith('Z')).toBe(true);
        expect(Math.min(...nums(d))).toBeCloseTo(ROUND / 2, 2);
        expect(Math.max(...xs)).toBeLessThanOrEqual(100 - ROUND / 2 + 1e-9);
    });

    it('is nothing when too narrow', () => {
        expect(bar(0, 2.5, 0, 10)).toBe('');
    });
});

describe('setD', () => {
    it('only touches the DOM when the path changes', () => {
        const e = svgEl('path', {});
        let writes = 0;
        const set = e.setAttribute.bind(e);
        e.setAttribute = (k, v) => { writes++; set(k, v); };
        setD(e, 'M0 0Z'); setD(e, 'M0 0Z'); setD(e, 'M1 1Z');
        expect(writes).toBe(2);
        expect(e.getAttribute('d')).toBe('M1 1Z');
        setD(null, 'M0 0Z'); // no element: nothing to do
    });
});

describe('countdown ring and bar', () => {
    let svg;
    beforeEach(() => { svg = svgEl('svg', {}); });
    const fill = () => svg.querySelector('.cd-fill').getAttribute('d') ?? '';

    it('a full ring has no seam, an empty one draws nothing, part of one runs from 12 o\'clock', () => {
        const ring = countdownRing(svg);
        ring.set(1);
        expect(fill()).toBe(annulus(RING.R, RING.r));
        ring.set(0);
        expect(fill()).toBe('');
        ring.set(0.25);
        expect(fill()).toBe(sector(-90, 0, RING.R, RING.r));
    });

    it('a bar fills from the left', () => {
        const strip = countdownBar(svg, 200, 10);
        strip.set(0.5);
        expect(fill()).toBe(bar(0, 100, 0, 10));
        strip.set(2);
        expect(fill()).toBe(bar(0, 200, 0, 10));
        strip.set(0);
        expect(fill()).toBe('');
    });
});

describe('the Timer\'s ring', () => {
    let host;
    beforeEach(() => {
        document.body.innerHTML = '<div id="timer-ring" data-full="1"></div>';
        host = document.getElementById('timer-ring');
    });
    const fill = () => host.querySelector('.cd-fill').getAttribute('d') ?? '';

    it('is full before a timed block starts, if the page asks for that', () => {
        frame({ phase: 'stopped' });
        expect(fill()).toBe(annulus(RING.R, RING.r));
    });

    it('drains as the work block runs', () => {
        frame({ phase: 'work', now: 7.5, workStart: 0, phaseEnd: 10 });
        expect(fill()).toBe(sector(-90, 0, RING.R, RING.r)); // a quarter left
    });

    it('counts a rest from when it starts, not from before', () => {
        frame({ phase: 'rest', now: 0, restStart: 1, restEnd: 5 });
        expect(fill()).toBe(annulus(RING.R, RING.r));
    });

    it('is empty with nothing running when the page doesn\'t ask for full', () => {
        host.dataset.full = '0';
        frame({ phase: 'stopped' });
        expect(fill()).toBe('');
    });
});
