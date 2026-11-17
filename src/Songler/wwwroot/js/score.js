// Guitar Pro / MusicXML support via alphaTab (MPL-2.0, vendored in lib/alphatab).
// - parse(): extract the tempo map, time signatures, section markers and repeat-expanded play order
// - mount()/dispose(): render a track and follow the metronome (window 'songler-beat' events)
import { putFile, getFile } from './db.js';

const LIB = 'lib/alphatab/';
let loading = null;
let pending = null; // { bytes, name } of the last parsed file, saved on confirm

function loadAlphaTab() {
    if (window.alphaTab) return Promise.resolve(window.alphaTab);
    if (loading) return loading;
    loading = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = LIB + 'alphaTab.min.js';
        s.onload = () => resolve(window.alphaTab);
        s.onerror = () => { loading = null; reject(new Error('Could not load the score library.')); };
        document.head.appendChild(s);
    });
    return loading;
}

function settings(at, extra) {
    const st = new at.Settings();
    st.core.fontDirectory = new URL(LIB + 'font/', document.baseURI).href;
    st.core.useWorkers = false;
    st.core.engine = 'svg';
    st.player.enablePlayer = false;
    if (extra) st.fillFromJson(extra);
    return st;
}

/** Bars in the order they are played, expanding repeats and alternate endings. */
function playOrder(mbs) {
    const order = [];
    const finished = new Set();
    let i = 0, start = 0, pass = 1;
    while (i < mbs.length && order.length < 20000) {
        const mb = mbs[i];
        if (mb.isRepeatStart && i !== start) { start = i; pass = 1; }
        if (mb.alternateEndings && !(mb.alternateEndings & (1 << (pass - 1)))) { i++; continue; }
        order.push(i);
        if (mb.repeatCount > 0 && !finished.has(i)) {
            if (pass < mb.repeatCount) { pass++; i = start; continue; }
            finished.add(i); pass = 1; start = i + 1;
        }
        i++;
    }
    return order;
}

export async function parse(bytes, name) {
    const at = await loadAlphaTab();
    const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    let score;
    try { score = at.importer.ScoreLoader.loadScoreFromBytes(data, settings(at)); }
    catch (e) { throw new Error('This file could not be read as a Guitar Pro or MusicXML score.'); }
    pending = { bytes: data, name };

    // tempo at the start of each master bar (automations mid-bar take effect from the next bar)
    let tempo = 120;
    const bars = score.masterBars.map(mb => {
        const autos = mb.tempoAutomations || [];
        const atStart = autos.find(a => a.ratioPosition <= 0.01);
        if (atStart) tempo = atStart.value;
        const startTempo = tempo;
        if (autos.length) tempo = autos[autos.length - 1].value;
        return {
            n: mb.timeSignatureNumerator, d: mb.timeSignatureDenominator, t: Math.round(startTempo * 10) / 10,
            marker: mb.section ? (mb.section.text || '').trim() || (mb.section.marker || '').trim() : null,
        };
    });
    return JSON.stringify({
        title: score.title || '', artist: score.artist || '',
        tracks: score.tracks.map(t => ({ index: t.index, name: t.name || `Track ${t.index + 1}` })),
        bars, order: playOrder(score.masterBars),
    });
}

export async function savePending(id) {
    if (!pending) throw new Error('Nothing to save');
    await putFile(id, pending.name, pending.bytes);
    pending = null;
}

// ------------------------------------------------------------------ rendering + sync
const views = new Map(); // host id -> view

export async function mount(hostId, fileId, opts) {
    dispose(hostId);
    const host = document.getElementById(hostId);
    if (!host) return false;
    const at = await loadAlphaTab();
    const bytes = await getFile(fileId);
    if (!bytes) { host.innerHTML = '<div class="tab-missing">Score file not found. Re-import it from the item editor.</div>'; return false; }

    host.innerHTML = '';
    const scroller = document.createElement('div'); scroller.className = 'tab-scroll';
    const wrap = document.createElement('div'); wrap.className = 'tab-wrap';
    const surface = document.createElement('div'); surface.className = 'tab-surface';
    const barHi = document.createElement('div'); barHi.className = 'tab-bar-hi';
    const cursor = document.createElement('div'); cursor.className = 'tab-cursor';
    wrap.append(surface, barHi, cursor); scroller.append(wrap); host.append(scroller);

    const view = { host, scroller, surface, barHi, cursor, api: null, score: null, opts: {}, ready: false, last: null, at };
    views.set(hostId, view);
    view.api = new at.AlphaTabApi(surface, settings(at, { display: { layoutMode: 'page', scale: 0.9 } }));
    view.score = at.importer.ScoreLoader.loadScoreFromBytes(bytes, view.api.settings);
    view.api.renderFinished.on(() => {
        view.ready = true;
        if (view.pendingScroll) { const f = view.pendingScroll; view.pendingScroll = null; f(); }
        if (view.last) place(view, view.last, false);
    });
    view.onBeat = e => onBeat(view, e.detail);
    window.addEventListener('songler-beat', view.onBeat);
    view.onStop = () => { view.barHi.style.opacity = '0'; view.cursor.style.opacity = '0'; };
    window.addEventListener('songler-stop', view.onStop);
    update(hostId, opts);
    return true;
}

export function update(hostId, opts) {
    const v = views.get(hostId);
    if (!v) return;
    const prev = v.opts;
    v.opts = opts;
    const track = Math.min(opts.track || 0, v.score.tracks.length - 1);
    const changed = prev.track !== track || prev.stave !== opts.stave || prev.start !== opts.start || prev.count !== opts.count || prev.dark !== opts.dark;
    if (!changed) return;
    const s = v.api.settings;
    s.display.staveProfile = opts.stave === 'score' ? v.at.StaveProfile.ScoreTab : v.at.StaveProfile.Tab;
    s.display.startBar = (opts.start ?? 0) + 1;
    s.display.barCount = opts.count > 0 ? opts.count : -1;
    applyColors(v, s, !!opts.dark);
    v.api.updateSettings();
    v.ready = false;
    v.api.renderScore(v.score, [track]);
}

function applyColors(v, s, dark) {
    const C = v.at.model.Color;
    const ink = dark ? C.fromJson('#e9e8e4') : C.fromJson('#1b1b1a');
    const soft = dark ? C.fromJson('#8f8d87') : C.fromJson('#77756f');
    const res = s.display.resources;
    for (const k of ['mainGlyphColor', 'secondaryGlyphColor', 'scoreInfoColor', 'barNumberColor', 'barSeparatorColor']) if (k in res) res[k] = k === 'secondaryGlyphColor' || k === 'barNumberColor' ? soft : ink;
    if ('staffLineColor' in res) res.staffLineColor = dark ? C.fromJson('#5a5955') : C.fromJson('#a9a79f');
}

function onBeat(v, d) {
    if (d.scoreBar == null || d.scoreBar < 0) return;
    v.last = d;
    if (v.ready) place(v, d, true);
}

function place(v, d, scroll) {
    const lookup = v.api.boundsLookup || (v.api.renderer && v.api.renderer.boundsLookup);
    if (!lookup) return;
    const mbb = lookup.findMasterBarByIndex(d.scoreBar);
    if (!mbb) { v.barHi.style.opacity = '0'; v.cursor.style.opacity = '0'; return; }
    const r = mbb.realBounds;
    const sb = v.barHi.style;
    sb.opacity = '1'; sb.left = r.x + 'px'; sb.top = r.y + 'px'; sb.width = r.w + 'px'; sb.height = r.h + 'px';
    v.barHi.classList.toggle('counting', !!d.counting);

    // beat cursor: find the rendered beat at this click's position within the bar
    let x = r.x + r.w * ((d.beat + 0.5) / Math.max(1, d.beats));
    try {
        const track = v.score.tracks[Math.min(v.opts.track || 0, v.score.tracks.length - 1)];
        const bar = track.staves[0].bars[d.scoreBar];
        const ticksPerClick = 3840 / Math.max(1, d.unit || 4);
        const target = bar.masterBar.start + d.beat * ticksPerClick;
        let best = null;
        for (const voice of bar.voices) for (const b of voice.beats) {
            if (b.isEmpty && voice.index > 0) continue;
            const start = bar.masterBar.start + b.playbackStart;
            if (start <= target + 1 && (!best || start > best.start)) best = { b, start };
        }
        if (best) {
            const bb = lookup.findBeat(best.b);
            if (bb) x = bb.visualBounds.x + bb.visualBounds.w / 2;
        }
    } catch { /* keep the proportional estimate */ }
    const sc = v.cursor.style;
    sc.opacity = d.counting ? '0' : '1'; sc.left = (x - 1) + 'px'; sc.top = r.y + 'px'; sc.height = r.h + 'px';

    if (scroll) {
        const top = r.y, bottom = r.y + r.h, view = v.scroller;
        if (top < view.scrollTop + 8 || bottom > view.scrollTop + view.clientHeight - 8)
            view.scrollTo({ top: Math.max(0, top - 24), behavior: 'smooth' });
    }
}

export function dispose(hostId) {
    const v = views.get(hostId);
    if (!v) return;
    window.removeEventListener('songler-beat', v.onBeat);
    window.removeEventListener('songler-stop', v.onStop);
    try { v.api.destroy(); } catch { }
    v.host.innerHTML = '';
    views.delete(hostId);
}

export function tracks(hostId) {
    const v = views.get(hostId);
    if (!v) return '[]';
    return JSON.stringify(v.score.tracks.map(t => ({ index: t.index, name: t.name || `Track ${t.index + 1}` })));
}

/** Scroll the tab so a master bar is near the top (used when a part is selected). Waits for layout if needed. */
export function scrollToBar(hostId, barIndex) {
    const v = views.get(hostId);
    if (!v) return;
    const go = () => {
        const lookup = v.api.boundsLookup;
        const mbb = lookup && lookup.findMasterBarByIndex(barIndex);
        if (mbb) v.scroller.scrollTo({ top: Math.max(0, mbb.realBounds.y - 24), behavior: 'smooth' });
    };
    if (v.ready) go(); else v.pendingScroll = go;
}
