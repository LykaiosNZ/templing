// Templing metronome engine: Web Audio look-ahead scheduler.
// Tempo is expressed as a *factor* applied to each step's reference BPM, so a whole
// tempo map (sections with different tempos/meters) speeds up/slows down together.
//
// config = {
//   steps: [{ bpm, beatsPerBar, beatUnit, subdivision, bars }],
//   playThrough: bool, loop: bool,  // play the steps in order (bars>0) and loop at the end
//   step: index,                      // current step
//   factor: number,                   // manual tempo factor (when training disabled)
//   countIn: { enabled, bars },
//   volume, sound: 'click'|'beep'|'wood', accentFirst,
//   training: { enabled, startFactor, endFactor, mode: 'time'|'bars', durationSec, everyBars, levels, stopAtEnd },
//     levels: the tempo factors to play in turn, start first and end last (whole bpm / %, worked out in C#);
//     one jump every everyBars bars, or spread evenly over durationSec
//   interval: { enabled, workSec, restSec, rounds, cues }
// }

const LOOKAHEAD_MS = 25;
const SCHEDULE_AHEAD = 0.12;

let ctx = null;
let master = null;
let dotnet = null;
let cfg = null;
let timer = null;
let raf = null;
const queue = []; // visual events {time, kind, ...}

// runtime state
let st = null;

function ensureCtx() {
    if (!ctx) {
        ctx = new (window.AudioContext || window.webkitAudioContext)({ latencyHint: 'interactive' });
        master = ctx.createGain();
        master.connect(ctx.destination);
    }
    if (ctx.state === 'suspended') ctx.resume();
    master.gain.value = cfg ? cfg.volume : 0.8;
}

export function init(ref) { dotnet = ref; }

export function configure(json) {
    const next = JSON.parse(json);
    const prev = cfg;
    cfg = next;
    if (master) master.gain.value = cfg.volume;
    if (!st) return;
    // live updates while running
    const stepBefore = st.step;
    const tempoBefore = prev && prev.steps[st.step] ? prev.steps[st.step].bpm * st.factor : null;
    if (prev && prev.step !== cfg.step && st.phase !== 'rest') jumpToStep(cfg.step);
    if (!cfg.training.enabled) st.factor = cfg.factor;
    else if (!prev || !prev.training.enabled) { st.trainingStartActive = st.active; st.trainingBars = 0; st.factor = cfg.training.startFactor; }
    if (st.step >= cfg.steps.length) jumpToStep(0);
    if (st.running && st.step === stepBefore && tempoBefore) retime(tempoBefore, stepCfg().bpm * st.factor);
}

// A new tempo while playing takes effect now, not from the next beat: the beat that's due is moved to where it falls
// at the new tempo, keeping how far through the beat we are. (The next beat was timed at the old tempo, so a change
// used to wait for it: laggy and snapped to the beat, and at slow tempos you could hear it.)
function retime(before, after) {
    if (!(before > 0) || !(after > 0) || Math.abs(before - after) < 1e-9) return;
    if (st.phase !== 'work' && st.phase !== 'countin') return;
    if (st.lastPulse == null || st.lastPulse >= st.nextTime) return;
    const now = ctx.currentTime;
    const from = Math.max(now, st.lastPulse); // the last scheduled click may still be ahead: it can't move
    const gone = (from - st.lastPulse) / (st.nextTime - st.lastPulse); // how far through the gap we are
    const gap = (st.nextTime - st.lastPulse) * before / after;
    st.nextTime = Math.max(now + 0.005, from + (1 - Math.min(1, gone)) * gap);
    // the display shows the tempo sounding now, which is this one from here on (not the bar's, until its next bar line)
    if (st.heardBar) st.heardBar.factor = st.factor;
    for (const e of queue) if (e.kind === 'bar') e.factor = st.factor;
    emit('state');
}

// Per-bar meter. Sections imported from a score carry scoreBars: [[masterBarIndex, numerator, denominator], ...].
function meterAt(s, bar) {
    const sb = s.scoreBars;
    if (sb && sb.length) { const b = sb[bar % sb.length]; return { n: b[1], d: b[2], i: b[0] }; }
    return { n: s.beatsPerBar, d: s.beatUnit || 4, i: -1 };
}

// Tempo is quarter-note based (as in Guitar Pro): an x/8 bar clicks eighths, x/2 clicks halves.
function beatSeconds(bpm, d) { return 60 / Math.max(10, bpm) * 4 / Math.max(1, d); }

function stepCfg() { return cfg.steps[Math.min(st.step, cfg.steps.length - 1)]; }

function jumpToStep(i) {
    st.step = Math.max(0, Math.min(i, cfg.steps.length - 1));
    st.bar = 0; st.beat = 0; st.sub = 0; st.meter = null;
    st.heardBeat = null; // show the new section straight away, not the old one's bar until its next beat
}

// ------------------------------------------------------------------ sound
function click(time, kind) {
    // kind: 'accent' | 'beat' | 'sub' | 'count' | 'cue-hi' | 'cue-lo' | 'cue-end'
    if (cfg.silent && !kind.startsWith('cue')) return;
    const sound = cfg.sound;
    const freqs = {
        click: { accent: 2000, beat: 1400, sub: 1000, count: 1700 },
        beep: { accent: 1320, beat: 880, sub: 660, count: 1100 },
        wood: { accent: 1100, beat: 800, sub: 600, count: 950 },
    }[sound] || { accent: 2000, beat: 1400, sub: 1000, count: 1700 };
    const g = ctx.createGain();
    g.connect(master);
    if (kind.startsWith('cue')) {
        const o = ctx.createOscillator();
        o.type = 'sine';
        o.frequency.value = kind === 'cue-hi' ? 1046 : kind === 'cue-end' ? 523 : 784;
        const dur = kind === 'cue-end' ? 0.6 : 0.18;
        g.gain.setValueAtTime(0.0001, time);
        g.gain.exponentialRampToValueAtTime(0.6, time + 0.01);
        g.gain.exponentialRampToValueAtTime(0.0001, time + dur);
        o.connect(g); o.start(time); o.stop(time + dur + 0.02);
        return;
    }
    const level = kind === 'accent' ? 1 : kind === 'beat' ? 0.7 : kind === 'count' ? 0.8 : 0.35;
    const o = ctx.createOscillator();
    let dur;
    if (sound === 'beep') {
        o.type = 'sine'; dur = 0.07;
    } else if (sound === 'wood') {
        o.type = 'triangle'; dur = 0.05;
        o.frequency.setValueAtTime(freqs[kind] * 1.5, time);
        o.frequency.exponentialRampToValueAtTime(freqs[kind], time + 0.02);
    } else {
        o.type = 'square'; dur = 0.025;
    }
    if (sound !== 'wood') o.frequency.value = freqs[kind];
    let node = o;
    if (sound === 'click') {
        const f = ctx.createBiquadFilter();
        f.type = 'bandpass'; f.frequency.value = freqs[kind]; f.Q.value = 3;
        o.connect(f); node = f;
    }
    g.gain.setValueAtTime(0.0001, time);
    g.gain.exponentialRampToValueAtTime(level, time + 0.002);
    g.gain.exponentialRampToValueAtTime(0.0001, time + dur);
    node.connect(g);
    o.start(time); o.stop(time + dur + 0.02);
}

// ------------------------------------------------------------------ transport
export function start() {
    if (!cfg) return;
    ensureCtx();
    stopInternal(false);
    const now = ctx.currentTime + 0.08;
    st = {
        running: true,
        step: Math.min(cfg.step, cfg.steps.length - 1),
        bar: 0, beat: 0, sub: 0,
        nextTime: now,
        factor: cfg.training.enabled ? cfg.training.startFactor : cfg.factor,
        phase: 'work',
        phaseEnd: Infinity,
        round: 1,
        countInLeft: 0,
        active: 0,             // seconds of active playing (work phase, not count-in)
        lastActiveStamp: null,
        trainingStartActive: 0,
        trainingBars: 0,
        finished: false,
        cuesDone: {},
    };
    if (cfg.restFirstSec > 0) {
        // timer extras: a rest before working ("+5 min after a rest"), or a rest-only countdown
        st.phase = 'rest';
        st.restStart = now;
        st.restEnd = now + cfg.restFirstSec;
        st.cuesDone = {};
    } else beginWork(now, true);
    timer = setInterval(scheduler, LOOKAHEAD_MS);
    raf = requestAnimationFrame(drawLoop);
    emit('started');
}

function beginWork(t, first) {
    st.phase = cfg.countIn.enabled && cfg.countIn.bars > 0 && !cfg.silent ? 'countin' : 'work';
    st.bar = 0; st.beat = 0; st.sub = 0; st.meter = null;
    if (!first && !cfg.playThrough) { /* keep current step */ }
    st.countInLeft = st.phase === 'countin' ? cfg.countIn.bars * stepCfg().beatsPerBar : 0;
    st.nextTime = t;
    st.lastPulse = null; // nothing to retime from until the first click
    st.phaseEnd = Infinity; // set when count-in completes
    if (st.phase === 'work') startWorkClock(t);
}

function startWorkClock(t) {
    st.workStart = t;
    st.worked = true;
    st.phaseEnd = cfg.interval.enabled ? t + cfg.interval.workSec : Infinity;
    st.cuesDone = {};
}

function stopInternal(notify) {
    if (timer) clearInterval(timer);
    timer = null;
    if (raf) cancelAnimationFrame(raf);
    raf = null;
    queue.length = 0;
    if (st) { accumulateActive(); st.running = false; }
    clearVisual();
    window.dispatchEvent(new CustomEvent('templing-stop'));
    if (notify) emit('stopped');
}

export function stop() { stopInternal(true); }

export function restartSection() {
    if (!st || !st.running) { start(); return; }
    const t = ctx.currentTime + 0.05;
    queue.length = 0;
    st.bar = 0; st.beat = 0; st.sub = 0; st.meter = null; st.heardBeat = null;
    if (st.phase === 'rest') return;
    st.nextTime = t; st.lastPulse = null;
    if (cfg.countIn.enabled && cfg.countIn.bars > 0 && !cfg.silent) {
        accumulateActive();
        st.phase = 'countin';
        st.countInLeft = cfg.countIn.bars * stepCfg().beatsPerBar;
        st.pausedPhaseRemain = st.phaseEnd === Infinity ? Infinity : st.phaseEnd - ctx.currentTime;
    }
    emit('state');
}

export function setStep(i) {
    if (!cfg) return;
    cfg.step = i;
    if (st && st.running && st.phase !== 'rest') { jumpToStep(i); st.nextTime = Math.max(st.nextTime, ctx.currentTime + 0.03); st.lastPulse = null; queue.length = 0; }
    emit('state');
}

export function isRunning() { return !!(st && st.running); }

// Banks the active time up to `at`: now, or when work ends. The end of work is scheduled up to SCHEDULE_AHEAD early,
// and banking it then, counted to "now", lost that much every round (2 x 5:00 logged as 9:59).
// st.activeUntil is how far ahead that banked: activeNow() doesn't count it until it's reached, nor does a stop before it.
function accumulateActive(at) {
    if (!st || !ctx) return;
    const now = ctx.currentTime;
    if (st.lastActiveStamp != null) {
        const end = at ?? now;
        st.active += Math.max(0, end - st.lastActiveStamp);
        // round off float error, so a 15 s block adds 15, not 14.999999999998 (the clock shows whole seconds, rounded down)
        st.active = Math.round(st.active * 1e6) / 1e6;
        st.activeUntil = end;
        st.lastActiveStamp = null;
    } else if (at === undefined && st.activeUntil > now) {
        // stopped before an end that was already banked: take back the part that never played
        st.active = Math.max(0, st.active - (st.activeUntil - now));
        st.activeUntil = now;
    }
}

function trainingFactorAtBar() {
    const tr = cfg.training;
    if (!tr.enabled) return cfg.factor;
    const levels = tr.levels || [tr.startFactor, tr.endFactor];
    const n = Math.max(1, levels.length - 1);
    let done; // jumps taken so far
    if (tr.mode === 'bars') {
        done = Math.floor(st.trainingBars / Math.max(1, tr.everyBars));
    } else {
        const elapsed = activeNow() - st.trainingStartActive;
        done = Math.floor(Math.min(1, elapsed / Math.max(1, tr.durationSec)) * n + 1e-9);
    }
    return levels[Math.min(done, n)];
}

// The next training tempo change: when ({ bars, beats } or { sec }) and the level it lands on ({ level }, an index
// into cfg.training.levels). Tempo only changes on a bar line, so in 'time' mode this is the first bar line at which
// the clock has passed the next level's time. When levels are shorter than a bar, that skips some (+1 due mid-bar
// becomes +3 at the bar line), so the level is worked out here rather than assumed to be the next one.
// Counted from what's sounding (st.heardBeat / st.heardBar), not from the scheduler, which runs up to a beat ahead:
// counted from the scheduler, beat 3 of 4 already read "1 beat", and so did beat 4.
function trainingNextStep() {
    const tr = cfg.training;
    const levels = tr.levels || [tr.startFactor, tr.endFactor];
    const n = Math.max(1, levels.length - 1);
    const levelOf = f => { let i = 0; levels.forEach((l, j) => { if (Math.abs(l - f) < Math.abs(levels[i] - f)) i = j; }); return i; };
    const now = ctx.currentTime;
    const ahead = st.lastBarTime != null && st.lastBarTime > now; // the scheduler has decided the next bar line already
    const heardFactor = ahead && st.heardBar ? st.heardBar.factor : st.factor;
    const cur = levelOf(heardFactor); // the level sounding now
    const s = stepCfg();
    const meter = st.meter || meterAt(s, st.bar);
    const hb = st.heardBeat;
    const beatsLeft = hb ? Math.max(1, hb.beats - hb.beat) : meter.n; // to the end of the bar that's sounding
    if (tr.mode === 'bars') {
        const every = Math.max(1, tr.everyBars);
        const done = ahead && st.heardBar ? st.heardBar.trainingBars : st.trainingBars;
        return { bars: every - (done % every), beats: beatsLeft, level: Math.min(n, cur + 1) };
    }
    if (ahead && levelOf(st.factor) !== cur) return { beats: beatsLeft, sec: st.lastBarTime - now, level: levelOf(st.factor) };
    // walk the bar lines the scheduler hasn't reached, at the current tempo, until one lands on a new level
    const dur = Math.max(1, tr.durationSec);
    const levelAt = e => Math.min(n, Math.floor(Math.min(1, e / dur) * n + 1e-9));
    const elapsed = activeNow() - st.trainingStartActive;
    const subs = Math.max(1, s.subdivision);
    const beatSec = beatSeconds(s.bpm * st.factor, meter.d);
    const atBarLine = st.beat === 0 && st.sub === 0; // waiting on a bar line it hasn't decided yet
    let t = atBarLine ? st.nextTime : st.nextTime + (meter.n - st.beat) * beatSec - st.sub * beatSec / subs;
    let bar = atBarLine ? st.bar : st.bar + 1;
    for (let k = 0; k < 10000; k++) {
        const level = levelAt(elapsed + Math.max(0, t - now));
        if (level !== levelOf(st.factor)) {
            const sec = Math.max(0, t - now);
            // the first of these is the end of the sounding bar unless the scheduler is a bar line ahead
            return k === 0 && !ahead ? { beats: beatsLeft, sec, level } : { sec, level };
        }
        const m = meterAt(s, bar++);
        t += m.n * beatSeconds(s.bpm * st.factor, m.d);
    }
    return null;
}

function trainingDone() {
    const tr = cfg.training;
    if (!tr.enabled) return false;
    if (tr.mode === 'bars') return Math.abs(st.factor - tr.endFactor) < 1e-6;
    return activeNow() - st.trainingStartActive >= tr.durationSec;
}

function activeNow() {
    if (!st) return 0;
    const now = ctx.currentTime;
    if (st.lastActiveStamp != null) return st.active + Math.max(0, now - st.lastActiveStamp);
    return st.active - Math.max(0, (st.activeUntil ?? now) - now); // banked up to an end that's still ahead
}

// ------------------------------------------------------------------ scheduler
function scheduler() {
    if (!st || !st.running) return;
    const horizon = ctx.currentTime + SCHEDULE_AHEAD;

    // rest phase: wait, play cues, then start next round
    if (st.phase === 'rest') {
        if (cfg.interval.cues) {
            for (const n of [3, 2, 1]) {
                const t = st.restEnd - n;
                if (!st.cuesDone['r' + n] && t <= horizon && t >= st.restStart) { click(Math.max(t, ctx.currentTime), 'cue-lo'); st.cuesDone['r' + n] = true; }
            }
        }
        if (st.restEnd <= horizon) {
            if (cfg.restOnly) { finish(st.restEnd, 'rest'); return; }
            if (st.worked) st.round++;
            if (cfg.interval.cues) click(st.restEnd, 'cue-hi');
            beginWork(st.restEnd + (cfg.interval.cues ? 0.35 : 0), false);
            queue.push({ time: st.restEnd, kind: 'phase' });
        }
        return;
    }

    while (st.nextTime < horizon && st.running && st.phase !== 'rest') {
        const s = stepCfg();
        const bpm = Math.max(10, s.bpm * st.factor);
        const subs = Math.max(1, s.subdivision);
        const pulse = 60 / bpm / subs;
        const t = st.nextTime;

        if (st.phase === 'countin') {
            // count-in uses the meter of the bar we're about to play
            const m0 = meterAt(s, st.bar);
            const beatIdx = (cfg.countIn.bars * m0.n - st.countInLeft) % m0.n;
            click(t, beatIdx === 0 ? 'accent' : 'count');
            queue.push({ time: t, kind: 'count', beat: beatIdx, beats: m0.n, unit: m0.d, scoreBar: m0.i, left: Math.ceil(st.countInLeft / m0.n), num: st.countInLeft });
            st.countInLeft--;
            st.lastPulse = t;
            st.nextTime += beatSeconds(bpm, m0.d);
            if (st.countInLeft <= 0) {
                st.phase = 'work';
                if (st.pausedPhaseRemain !== undefined) {
                    // resuming after a restart: keep the remaining work time
                    st.phaseEnd = st.pausedPhaseRemain === Infinity ? Infinity : st.nextTime + st.pausedPhaseRemain;
                    st.pausedPhaseRemain = undefined;
                } else startWorkClock(st.nextTime);
                st.bar = 0; st.beat = 0; st.sub = 0; st.meter = null;
                queue.push({ time: st.nextTime, kind: 'workstart' });
            }
            continue;
        }

        // --- work phase ---
        if (t >= st.phaseEnd) { enterRestOrFinish(st.phaseEnd); return; }
        if (st.lastActiveStamp == null) st.lastActiveStamp = Math.max(ctx.currentTime, t);

        // bar boundary: training tempo updates are quantized to the bar
        if (st.beat === 0 && st.sub === 0) {
            st.factor = cfg.training.enabled ? trainingFactorAtBar() : cfg.factor;
            st.meter = meterAt(s, st.bar);
            st.lastBarTime = t;
            queue.push({ time: t, kind: 'bar', step: st.step, bar: st.bar, factor: st.factor, trainingBars: st.trainingBars });
        }
        const meter = st.meter || meterAt(s, st.bar);

        const accent = st.beat === 0 && st.sub === 0 && cfg.accentFirst;
        const kind = st.sub > 0 ? 'sub' : accent ? 'accent' : 'beat';
        click(t, kind);
        if (st.sub === 0) queue.push({ time: t, kind: 'beat', beat: st.beat, beats: meter.n, unit: meter.d, scoreBar: meter.i, step: st.step, bar: st.bar });

        // advance
        st.lastPulse = t;
        st.nextTime += beatSeconds(s.bpm * st.factor, meter.d) / subs;
        st.sub++;
        if (st.sub >= subs) {
            st.sub = 0; st.beat++;
            if (st.beat >= meter.n) {
                st.beat = 0; st.bar++;
                if (cfg.training.enabled) {
                    st.trainingBars++;
                    if (cfg.training.stopAtEnd && trainingDone()) { finish(st.nextTime, 'training'); return; }
                }
                const len = s.scoreBars && s.scoreBars.length ? s.scoreBars.length : s.bars;
                if (cfg.playThrough && len > 0 && st.bar >= len) {
                    st.bar = 0;
                    if (st.step + 1 < cfg.steps.length) st.step++;
                    else if (cfg.loop) st.step = 0;
                    else { finish(st.nextTime, 'end'); return; }
                    cfg.step = st.step;
                }
            }
        }
    }

    // The 3-2-1 pips and the end of work are timed in seconds, not beats, so check them on every tick.
    // (Checked only when a beat was scheduled, a pip between beats came due after its time and played
    // late by a different amount each, so the pips sounded uneven at slow tempos.)
    if (st.running && st.phase === 'work' && cfg.interval.enabled && st.phaseEnd !== Infinity) {
        if (cfg.interval.cues) {
            for (const n of [3, 2, 1]) {
                const ct = st.phaseEnd - n;
                if (!st.cuesDone['w' + n] && ct <= horizon && ct >= st.workStart) { click(Math.max(ct, ctx.currentTime), 'cue-lo'); st.cuesDone['w' + n] = true; }
            }
        }
        // every beat before the end is scheduled: end work on time rather than when the next beat comes due
        if (st.phaseEnd <= horizon && st.nextTime >= st.phaseEnd) enterRestOrFinish(st.phaseEnd);
    }
}

function enterRestOrFinish(t) {
    accumulateActive(t);
    const iv = cfg.interval;
    if (iv.rounds > 0 && st.round >= iv.rounds) { finish(t, 'interval'); return; }
    if (iv.restSec <= 0) { st.round++; beginWork(t, false); return; }
    st.phase = 'rest';
    st.restStart = t;
    st.restEnd = t + iv.restSec;
    st.cuesDone = {};
    if (iv.cues) click(t, 'cue-end');
    queue.push({ time: t, kind: 'phase' });
}

function finish(t, reason) {
    if (cfg.interval.cues || reason !== 'end') click(Math.max(t, ctx.currentTime), reason === 'rest' ? 'cue-hi' : 'cue-end');
    accumulateActive(Math.max(t, ctx.currentTime));
    st.finished = reason;
    st.running = false;
    if (timer) clearInterval(timer);
    timer = null;
    setTimeout(() => { stopInternal(false); emit('finished', reason); }, Math.max(0, (t - ctx.currentTime) * 1000) + 50);
}

// ------------------------------------------------------------------ visuals + .NET events
let lastEmit = 0;
function drawLoop() {
    if (!ctx) return;
    const now = ctx.currentTime;
    let changed = false;
    while (queue.length && queue[0].time <= now) {
        const e = queue.shift();
        if (e.kind === 'beat' || e.kind === 'count') {
            flashBeat(e.beat, e.kind === 'count');
            window.dispatchEvent(new CustomEvent('templing-beat', { detail: {
                scoreBar: e.scoreBar, beat: e.beat, beats: e.beats, unit: e.unit, counting: e.kind === 'count', step: e.step, bar: e.bar } }));
            changed = true;
        }
        // what's sounding now (the scheduler runs ahead of it): the trainer countdown counts from here
        if (e.kind === 'beat') st.heardBeat = { beat: e.beat, beats: e.beats, unit: e.unit, step: e.step, bar: e.bar };
        if (e.kind === 'count') st.heardBeat = null; // a count-in (or a restart's) starts again from bar 1
        if (e.kind === 'bar') st.heardBar = { factor: e.factor, trainingBars: e.trainingBars };
        if (e.kind === 'phase' && st.phase === 'rest') clearVisual();
        if (e.kind === 'bar' || e.kind === 'phase' || e.kind === 'workstart' || e.kind === 'count') changed = true;
        if (e.kind === 'count') st.countDisplay = e.num; else if (e.kind !== 'beat') st.countDisplay = null;
    }
    const ms = performance.now();
    if (changed || ms - lastEmit > 500) { emit('state'); lastEmit = ms; }
    if (st && (st.running || queue.length)) raf = requestAnimationFrame(drawLoop);
}

function flashBeat(beat, counting) {
    const host = document.getElementById('metro-beats');
    if (host) {
        const dots = host.querySelectorAll('.beat-dot');
        dots.forEach((d, i) => {
            d.classList.toggle('on', i === beat);
            d.classList.toggle('count', counting && i === beat);
        });
    }
    const pulse = document.getElementById('metro-pulse');
    if (pulse && cfg.flash !== false) {
        pulse.classList.remove('flash', 'flash-accent');
        void pulse.offsetWidth;
        pulse.classList.add(beat === 0 ? 'flash-accent' : 'flash');
    }
}

function clearVisual() {
    const host = document.getElementById('metro-beats');
    if (host) host.querySelectorAll('.beat-dot').forEach(d => d.classList.remove('on', 'count'));
}

function snapshot() {
    if (!st) return { running: false, phase: 'stopped' };
    const now = ctx ? ctx.currentTime : 0;
    let remaining = null;
    // measured from the block's start if that's still ahead: starts are scheduled a moment into the future
    // (a short lead so the first click isn't late), and counting that lead showed a 2:00 rest as 2:01
    if (st.phase === 'rest') remaining = st.restEnd - Math.max(now, st.restStart ?? now);
    else if (st.phase === 'work' && st.phaseEnd !== Infinity) remaining = st.phaseEnd - Math.max(now, st.workStart ?? now);
    // the tempo sounding now: the scheduler sets the next bar's tempo up to a beat before it plays
    const factor = st.lastBarTime != null && st.lastBarTime > now && st.heardBar ? st.heardBar.factor : st.factor;
    let trainingProgress = null, trainingNext = null;
    if (cfg.training.enabled) {
        const tr = cfg.training;
        trainingProgress = tr.endFactor === tr.startFactor ? 1 : Math.max(0, Math.min(1, (factor - tr.startFactor) / (tr.endFactor - tr.startFactor)));
        if (st.running && st.phase === 'work' && Math.abs(factor - tr.endFactor) > 1e-6) trainingNext = trainingNextStep();
    }
    // where the bar and section shown come from: the beat sounding now, not the scheduler, which moves on to the next
    // bar as soon as it has queued the last beat of this one (so the bar number changed a beat early)
    const hb = st.phase === 'work' ? st.heardBeat : null;
    const step = hb ? Math.min(hb.step, cfg.steps.length - 1) : st.step;
    const bar = hb ? hb.bar : st.bar;
    const meter = hb ? { n: hb.beats, d: hb.unit } : (st.meter || meterAt(stepCfg(), st.bar));
    const sb = cfg.steps[Math.min(step, cfg.steps.length - 1)].scoreBars;
    return {
        running: st.running,
        phase: st.running ? st.phase : 'stopped',
        step: st.step, // where the scheduler is: what the app keeps as the current section (and sends back)
        heardStep: step, // the section sounding now, for display
        bar,
        factor,
        active: activeNow(),
        remaining,
        round: st.round,
        count: st.phase === 'countin' ? st.countDisplay : null,
        workElapsed: st.phase === 'work' && st.workStart != null ? Math.max(0, now - st.workStart) : null,
        beats: meter.n,
        unit: meter.d,
        barInPart: sb && sb.length ? bar % sb.length : bar,
        trainingProgress,
        trainingNext,
        finished: st.finished || null,
    };
}

function emit(type, reason) {
    if (!dotnet) return;
    const s = snapshot();
    s.event = type;
    if (reason) s.reason = reason;
    dotnet.invokeMethodAsync('OnEngineEvent', JSON.stringify(s)).catch(() => { });
}

// a single quick tick used for tap/test sound
export function previewClick() {
    if (!cfg) return;
    ensureCtx();
    click(ctx.currentTime + 0.01, 'accent');
}
