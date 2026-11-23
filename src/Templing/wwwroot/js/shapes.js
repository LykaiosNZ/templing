// Shared SVG shapes for the practice displays, so the Metronome's beat display (beatdisplay.js) and the Timer's ring
// (countdown.js) are drawn by the same code and look the same. Change a shape here, not in one display.
//
// Every shape is a filled path stroked in its own colour ROUND wide, which rounds its corners slightly; the path is
// inset by half that stroke so the shape keeps its size.

export const NS = 'http://www.w3.org/2000/svg';
export const ROUND = 2.4;

export const svgEl = (tag, attrs, parent) => {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
};

// attributes for a shape drawn with the rounding stroke
export const shape = cls => ({ class: cls, 'stroke-width': ROUND, 'stroke-linejoin': 'round' });

export const setD = (e, d) => { if (e && e._d !== d) { e._d = d; e.setAttribute('d', d); } };

const rad = d => d * Math.PI / 180;

// annular sector from angle a0 to a1 (degrees, 0 = 3 o'clock), outer radius R, inner r
export function sector(a0, a1, R, r) {
    const ri = r + ROUND / 2, Ro = R - ROUND / 2, inset = (ROUND / 2) / ((R + r) / 2) * 180 / Math.PI;
    a0 += inset; a1 -= inset;
    if (a1 - a0 < 0.05) return '';
    const p = (rr, a) => (rr * Math.cos(rad(a))).toFixed(2) + ' ' + (rr * Math.sin(rad(a))).toFixed(2);
    const large = a1 - a0 > 180 ? 1 : 0;
    return `M${p(Ro, a0)}A${Ro} ${Ro} 0 ${large} 1 ${p(Ro, a1)}L${p(ri, a1)}A${ri} ${ri} 0 ${large} 0 ${p(ri, a0)}Z`;
}

// a whole ring, with no seam (a sector can't close on itself: its rounded ends left a notch at 12 o'clock).
// The inner circle runs the other way round, so it's a hole.
export function annulus(R, r) {
    const Ro = R - ROUND / 2, ri = r + ROUND / 2;
    return `M${Ro} 0A${Ro} ${Ro} 0 1 1 ${-Ro} 0A${Ro} ${Ro} 0 1 1 ${Ro} 0Z`
        + `M${ri} 0A${ri} ${ri} 0 1 0 ${-ri} 0A${ri} ${ri} 0 1 0 ${ri} 0Z`;
}

// rounded bar from x0 to x1, top y, height h
export function bar(x0, x1, y, h) {
    const w = x1 - x0 - ROUND;
    if (w < 0.3) return '';
    const x = x0 + ROUND / 2, top = y + ROUND / 2, hh = h - ROUND, r = Math.min(3, w / 2, hh / 2), f = n => n.toFixed(2);
    return `M${f(x + r)} ${f(top)}H${f(x + w - r)}Q${f(x + w)} ${f(top)} ${f(x + w)} ${f(top + r)}V${f(top + hh - r)}`
        + `Q${f(x + w)} ${f(top + hh)} ${f(x + w - r)} ${f(top + hh)}H${f(x + r)}Q${f(x)} ${f(top + hh)} ${f(x)} ${f(top + hh - r)}`
        + `V${f(top + r)}Q${f(x)} ${f(top)} ${f(x + r)} ${f(top)}Z`;
}

// The ring geometry both displays use (viewBox -100..100): R outer, r inner
export const RING = { R: 96, r: 77 };

// A countdown ring in `svg`: a track and a fill that runs clockwise from 12 o'clock. set(f) shows fraction f (0..1).
// Colours come from CSS: .cd-track and .cd-fill, which read --cd-on (and --cd-track).
export function countdownRing(svg, R = RING.R, r = RING.r) {
    svgEl('circle', { class: 'cd-track', r: (R + r) / 2, 'stroke-width': R - r }, svg);
    const fill = svgEl('path', shape('cd-fill'), svg);
    return { set: f => setD(fill, f > .999 ? annulus(R, r) : f <= 0 ? '' : sector(-90, -90 + 360 * f, R, r)) };
}

// The same for a strip (viewBox width W, height H): fills from the left
export function countdownBar(svg, W, H) {
    svgEl('path', { ...shape('cd-track-bar'), d: bar(0, W, 0, H) }, svg);
    const fill = svgEl('path', shape('cd-fill'), svg);
    return { set: f => setD(fill, f <= 0 ? '' : bar(0, W * Math.min(1, f), 0, H)) };
}
