// Beat display on the Metronome page (#metro-beats): a strip and/or a ring of segments, one per click (or per beat),
// with a wider gap between beats. Segments fill as the bar plays and reset when the next bar starts.
//
// metronome.js drives it: click(e) as each click is heard, frame(state) on every animation frame while running (and
// once on pause / stop), shift(d) when a pause ends. The host's data-* attributes, rendered by Metronome.razor, carry
// the user's Settings > Metronome choices and the meter to show while stopped; a change to them redraws.
//
// host data: style strip|ring|both · fill click|sweep · newbar clear|fade|empty · pulse, mark, subsShow, thin, paused 0|1
//            beats, subs: the loaded section's meter (used until a click says otherwise)

const NS = 'http://www.w3.org/2000/svg';
const ROUND = 2.4; // corner rounding: every segment is stroked with its own colour this wide
const WATCHED = ['data-style', 'data-fill', 'data-newbar', 'data-pulse', 'data-mark', 'data-subs-show', 'data-thin', 'data-beats', 'data-subs', 'data-paused'];

let view = null;      // what's drawn: { host, observer, sig, segs, ... }
let last = null;      // the click sounding now: { time, dur, beat, sub, subs, beats, counting }
let state = { phase: 'stopped' }; // from the engine: { phase, now, paused, restStart, restEnd }
let bars = 0;         // work bars started since the run (or the round) began, for "empty out" and "fade"
let sonarAt = null;   // when the last bar started, for the pulse

const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');

// ------------------------------------------------------------------ engine API
export function click(e) {
    if (!last || last.counting !== !!e.counting) bars = 0;
    if (e.beat === 0 && e.sub === 0) {
        if (!e.counting) bars++;
        sonarAt = e.time;
    }
    last = { time: e.time, dur: e.dur, beat: e.beat, sub: e.sub, subs: e.subs, beats: e.beats, counting: !!e.counting };
}

export function frame(s) {
    state = s;
    if (s.phase === 'rest' || s.phase === 'stopped') { last = null; bars = 0; sonarAt = null; }
    draw();
}

// a restart or a section jump: nothing is sounding until the next click
export function restart() { last = null; bars = 0; sonarAt = null; draw(); }

// a pause of d seconds ended: what was sounding carries on from where it stopped
export function shift(d) {
    if (last) last.time += d;
    if (sonarAt != null) sonarAt += d;
}

// ------------------------------------------------------------------ drawing
const svgEl = (tag, attrs, parent) => {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
};
const rad = d => d * Math.PI / 180;
const clamp01 = x => Math.max(0, Math.min(1, x));
const setD = (e, d) => { if (e && e._d !== d) { e._d = d; e.setAttribute('d', d); } };
const setOp = (e, o) => { if (!e) return; const v = o.toFixed(3); if (e._o !== v) { e._o = v; e.setAttribute('opacity', v); } };
const setText = (e, t) => { if (e && e.textContent !== t) e.textContent = t; };
// m:ss / h:mm:ss, as Stats.Clock
const clockText = s => {
    s = Math.max(0, Math.ceil(s - 1e-6));
    const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, ss = String(s % 60).padStart(2, '0');
    return h ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
};

// annular sector from angle a0 to a1 (degrees, 0 = 3 o'clock), inset by half the rounding stroke
function sector(a0, a1, R, r) {
    const ri = r + ROUND / 2, Ro = R - ROUND / 2, inset = (ROUND / 2) / ((R + r) / 2) * 180 / Math.PI;
    a0 += inset; a1 -= inset;
    if (a1 - a0 < 0.05) return '';
    const p = (rr, a) => (rr * Math.cos(rad(a))).toFixed(2) + ' ' + (rr * Math.sin(rad(a))).toFixed(2);
    const large = a1 - a0 > 180 ? 1 : 0;
    return `M${p(Ro, a0)}A${Ro} ${Ro} 0 ${large} 1 ${p(Ro, a1)}L${p(ri, a1)}A${ri} ${ri} 0 ${large} 0 ${p(ri, a0)}Z`;
}

// rounded bar from x0 to x1, inset the same way
function bar(x0, x1, y, h) {
    const w = x1 - x0 - ROUND;
    if (w < 0.3) return '';
    const x = x0 + ROUND / 2, top = y + ROUND / 2, hh = h - ROUND, r = Math.min(3, w / 2, hh / 2), f = n => n.toFixed(2);
    return `M${f(x + r)} ${f(top)}H${f(x + w - r)}Q${f(x + w)} ${f(top)} ${f(x + w)} ${f(top + r)}V${f(top + hh - r)}`
        + `Q${f(x + w)} ${f(top + hh)} ${f(x + w - r)} ${f(top + hh)}H${f(x + r)}Q${f(x)} ${f(top + hh)} ${f(x)} ${f(top + hh - r)}`
        + `V${f(top + r)}Q${f(x)} ${f(top)} ${f(x + r)} ${f(top)}Z`;
}

function ensure() {
    const host = document.getElementById('metro-beats');
    if (!host) { if (view) view.observer.disconnect(); view = null; return null; }
    if (!view || view.host !== host) {
        if (view) view.observer.disconnect();
        view = { host };
        view.observer = new MutationObserver(() => draw());
        view.observer.observe(host, { attributes: true, attributeFilter: WATCHED });
    }
    const d = host.dataset;
    const beats = last ? last.beats : Math.max(1, +d.beats || 4);
    const subs = last ? last.subs : Math.max(1, +d.subs || 1);
    const sig = [d.style, d.mark, d.subsShow, d.thin, beats, subs].join('|');
    if (view.sig !== sig || !host.firstChild) { view.sig = sig; build(view, beats, subs); }
    return view;
}

function build(v, beats, subs) {
    const host = v.host, d = host.dataset;
    const style = d.style || 'strip';
    const vs = d.subsShow === '1' ? subs : 1; // segments per beat
    const thin = d.thin === '1' && vs > 1;
    const mark = d.mark === '1';
    host.replaceChildren();
    Object.assign(v, { beats, subs, vs, segs: [], ring: null, strip: null, labels: [], center: null });

    const attrs = cls => ({ class: cls, 'stroke-width': ROUND, 'stroke-linejoin': 'round' });
    const layers = svg => ({
        track: svgEl('g', { class: 'bd-segs' }, svg), ghost: svgEl('g', { class: 'bd-segs bd-ghosts' }, svg),
        fill: svgEl('g', { class: 'bd-segs' }, svg), hit: svgEl('g', { class: 'bd-segs' }, svg),
    });

    // ring: viewBox -100..100, starting at 12 o'clock with half a beat gap either side
    if (style !== 'strip') {
        const wrap = document.createElement('div');
        wrap.className = 'bd-ring';
        host.appendChild(wrap);
        const svg = svgEl('svg', { viewBox: '-100 -100 200 200' }, wrap);
        const center = document.createElement('div');
        center.className = 'bd-center';
        wrap.appendChild(center);
        const R = 96, rBeat = 77, rSub = thin ? 82 : 77;
        let gS = 2.6, gB = 8;
        const gaps = beats * gB + beats * (vs - 1) * gS;
        if (gaps > 130) { gS *= 130 / gaps; gB *= 130 / gaps; }
        const segA = (360 - beats * gB - beats * (vs - 1) * gS) / (beats * vs);
        const L = layers(svg);
        let a = -90 + gB / 2;
        for (let b = 0; b < beats; b++) for (let s = 0; s < vs; s++) {
            const r = s === 0 ? rBeat : rSub;
            const one = mark && b === 0 ? ' one' : '';
            svgEl('path', { ...attrs('bd-track' + one), d: sector(a, a + segA, R, r) }, L.track);
            v.segs.push({ isBeat: s === 0, ring: {
                a0: a, a1: a + segA, r,
                ghost: svgEl('path', attrs('bd-ghost'), L.ghost),
                fill: svgEl('path', attrs('bd-fill'), L.fill),
                hit: svgEl('path', { ...attrs('bd-hit'), d: sector(a, a + segA, R, r), opacity: 0 }, L.hit),
            } });
            a += segA + (s === vs - 1 ? gB : gS);
        }
        const sonar = svgEl('g', { class: 'bd-segs' }, svg);
        const rest = svgEl('g', { class: 'bd-rest' }, svg);
        svgEl('circle', { class: 'bd-rest-track', r: (R + rBeat) / 2, 'stroke-width': R - rBeat }, rest);
        v.ring = {
            R, rBeat, ghosts: L.ghost,
            sonar: [0, 1].map(() => svgEl('circle', { class: 'bd-sonar', r: R, opacity: 0 }, sonar)),
            rest: svgEl('path', attrs('bd-rest-fill'), rest),
        };
        v.center = center;
    }

    // strip: viewBox 600 x 30; with the ring hidden, beat numbers underneath
    if (style !== 'ring') {
        const W = 600, H = 30;
        const wrap = document.createElement('div');
        wrap.className = 'bd-strip';
        host.appendChild(wrap);
        const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}` }, wrap);
        let sS = 4, sB = 14;
        const gaps = (beats - 1) * sB + beats * (vs - 1) * sS;
        if (gaps > W * .3) { sS *= W * .3 / gaps; sB *= W * .3 / gaps; }
        const segW = (W - gaps) / (beats * vs);
        const labels = style === 'strip' ? document.createElement('div') : null;
        if (labels) { labels.className = 'bd-labels'; wrap.appendChild(labels); }
        const L = layers(svg);
        let x = 0, i = 0;
        for (let b = 0; b < beats; b++) {
            const bx = x;
            for (let s = 0; s < vs; s++, i++) {
                const h = s === 0 || !thin ? H : H * .62, y = (H - h) / 2;
                const one = mark && b === 0 ? ' one' : '';
                svgEl('path', { ...attrs('bd-track' + one), d: bar(x, x + segW, y, h) }, L.track);
                if (!v.segs[i]) v.segs[i] = { isBeat: s === 0 };
                v.segs[i].strip = {
                    x0: x, x1: x + segW, y, h,
                    ghost: svgEl('path', attrs('bd-ghost'), L.ghost),
                    fill: svgEl('path', attrs('bd-fill'), L.fill),
                    hit: svgEl('path', { ...attrs('bd-hit'), d: bar(x, x + segW, y, h), opacity: 0 }, L.hit),
                };
                x += segW + (s === vs - 1 ? sB : sS);
            }
            if (labels) {
                const span = document.createElement('span');
                span.textContent = b + 1;
                span.style.left = (bx / W * 100) + '%';
                span.style.width = ((x - sB - bx) / W * 100) + '%';
                labels.appendChild(span);
                v.labels.push(span);
            }
        }
        const sonar = svgEl('g', { class: 'bd-segs' }, svg);
        const rest = svgEl('g', { class: 'bd-rest' }, svg);
        svgEl('path', { ...attrs('bd-track'), d: bar(0, W, 0, H) }, rest);
        v.strip = {
            W, H, ghosts: L.ghost,
            sonar: [0, 1].map(() => svgEl('rect', { class: 'bd-sonar', opacity: 0 }, sonar)),
            rest: svgEl('path', attrs('bd-rest-fill'), rest),
        };
    }
    v.cb = undefined;
}

function draw() {
    const v = ensure();
    if (!v) return;
    const host = v.host, d = host.dataset, s = state;
    const now = s.now ?? 0;
    const paused = d.paused === '1';

    // rest: one countdown in place of the segments
    if (s.phase === 'rest') {
        if (host.dataset.phase !== 'rest') host.dataset.phase = 'rest';
        const total = (s.restEnd ?? 0) - (s.restStart ?? 0);
        const left = Math.max(0, (s.restEnd ?? 0) - Math.max(now, s.restStart ?? now));
        const f = total > 0 ? clamp01(left / total) : 0;
        if (v.ring) setD(v.ring.rest, f > .999 ? sector(-90, 269.95, v.ring.R, v.ring.rBeat) : sector(-90, -90 + 360 * f, v.ring.R, v.ring.rBeat));
        if (v.strip) setD(v.strip.rest, bar(0, v.strip.W * f, 0, v.strip.H));
        setText(v.center, clockText(left));
        return;
    }

    const playing = s.phase !== 'stopped' && last;
    const phase = !playing ? 'stopped' : last.counting ? 'countin' : 'work';
    if (host.dataset.phase !== phase) host.dataset.phase = phase;

    // how far through the bar, in segments
    let level = 0, start = -1, frac = 0;
    if (playing) {
        const span = last.counting ? v.vs : v.vs / v.subs;
        start = last.counting ? last.beat * v.vs : (last.beat * v.subs + last.sub) * v.vs / v.subs;
        frac = last.dur > 0 ? clamp01((now - last.time) / last.dur) : 1;
        level = d.fill === 'sweep' ? start + span * frac : Math.ceil(start + span - 1e-6);
    }
    const empties = playing && phase === 'work' && d.newbar === 'empty' && bars % 2 === 0; // every other bar
    const ghost = playing && phase === 'work' && d.newbar === 'fade' && bars > 1 ? Math.max(0, 1 - level / v.vs) * .5 : 0;
    if (v.ring) setOp(v.ring.ghosts, ghost);
    if (v.strip) setOp(v.strip.ghosts, ghost);

    // the segment whose click just sounded lights up and fades over the click
    const motion = !reduceMotion.matches && !paused;
    const hitSeg = playing && Math.abs(start - Math.round(start)) < 1e-6 ? Math.round(start) : -1;

    v.segs.forEach((seg, i) => {
        const f = clamp01(level - i);
        const [a, b] = empties ? [f, 1] : [0, f];
        const hit = motion && i === hitSeg ? Math.pow(1 - frac, 2.2) * (seg.isBeat ? .85 : .55) : 0;
        if (seg.ring) {
            const g = seg.ring, span = g.a1 - g.a0;
            setD(g.fill, b > a ? sector(g.a0 + span * a, g.a0 + span * b, v.ring.R, g.r) : '');
            if (ghost > 0) setD(g.ghost, f < 1 ? sector(g.a0 + span * f, g.a1, v.ring.R, g.r) : '');
            setOp(g.hit, hit);
        }
        if (seg.strip) {
            const g = seg.strip, w = g.x1 - g.x0;
            setD(g.fill, b > a ? bar(g.x0 + w * a, g.x0 + w * b, g.y, g.h) : '');
            if (ghost > 0) setD(g.ghost, f < 1 ? bar(g.x0 + w * f, g.x1, g.y, g.h) : '');
            setOp(g.hit, hit);
        }
    });

    // the pulse: two echoes ripple out as a bar starts
    const t0 = playing && motion && d.pulse === '1' && sonarAt != null ? now - sonarAt : -1;
    for (let j = 0; j < 2; j++) {
        const t = t0 - j * .14, p = t / .7, on = t0 >= 0 && t >= 0 && p < 1;
        const op = on ? Math.pow(1 - p, 1.5) * (j ? .45 : .8) : 0, sw = (3.2 * (1 - p) + .6).toFixed(2);
        if (v.ring) {
            const c = v.ring.sonar[j];
            if (on) { c.setAttribute('r', (v.ring.R + 2 + p * 24).toFixed(2)); c.setAttribute('stroke-width', sw); }
            setOp(c, op);
        }
        if (v.strip) {
            const r = v.strip.sonar[j];
            if (on) {
                const g = 2 + p * 16;
                r.setAttribute('x', -g); r.setAttribute('y', -g);
                r.setAttribute('width', v.strip.W + 2 * g); r.setAttribute('height', v.strip.H + 2 * g);
                r.setAttribute('rx', (5 + g * .7).toFixed(1)); r.setAttribute('stroke-width', sw);
            }
            setOp(r, op);
        }
    }

    // ring centre: the beat (beats left while counting in); beat numbers under a lone strip
    setText(v.center, !playing ? '' : String(last.counting ? last.beats - last.beat : last.beat + 1));
    const cb = playing ? last.beat : -1;
    if (v.cb !== cb) {
        v.cb = cb;
        v.labels.forEach((e, i) => { e.className = i === cb ? 'on' : i < cb ? 'past' : ''; });
    }
}
