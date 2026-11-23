// The Timer page's ring (#timer-ring): the block's remaining time, drawn with the same countdown ring as the
// Metronome's rest (shapes.js), so the two look the same. It drains clockwise from 12 o'clock as the block runs.
//
// metronome.js drives it: frame(state) on every animation frame while running, and once on pause / stop.
// host data: full 1|0, what to show with no countdown running (a full ring before a timed block, else empty)

import { svgEl, countdownRing } from './shapes.js';

let view = null;  // { host, observer, ring }
let state = { phase: 'stopped' };

export function frame(s) { state = s; draw(); }

function draw() {
    const host = document.getElementById('timer-ring');
    if (!host) { if (view) view.observer.disconnect(); view = null; return; }
    if (!view || view.host !== host || !host.firstChild) {
        if (view) view.observer.disconnect();
        const svg = svgEl('svg', { viewBox: '-100 -100 200 200', 'aria-hidden': 'true' });
        host.replaceChildren(svg);
        view = { host, ring: countdownRing(svg), observer: new MutationObserver(() => draw()) };
        view.observer.observe(host, { attributes: true, attributeFilter: ['data-full'] });
    }
    view.ring.set(fraction(state, host.dataset.full === '1'));
}

// the fraction of the current block left, or `full` with no countdown running
function fraction(s, full) {
    const left = (start, end) => end > start ? Math.max(0, Math.min(1, (end - Math.max(s.now, start)) / (end - start))) : 0;
    if (s.phase === 'rest' && s.restEnd != null) return left(s.restStart ?? s.now, s.restEnd);
    if (s.phase === 'work' && s.phaseEnd != null && s.workStart != null) return left(s.workStart, s.phaseEnd);
    return full ? 1 : 0;
}
