// The metronome engine, run on a fake audio clock: what it plays, when, and what it reports to .NET.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { installFakeAudio } from './fakeaudio.js';

let m, audio, events;

// what MetronomeService.PushConfig sends, at 120 bpm in 4/4 with everything off
function config(o = {}) {
    return {
        steps: [{ bpm: 120, beatsPerBar: 4, beatUnit: 4, subdivision: 1, bars: 0 }],
        playThrough: false, loop: false, step: 0, factor: 1,
        volume: 0.8, sound: 'test', accentFirst: true, silent: false, restFirstSec: 0, restOnly: false,
        ...o,
        countIn: { enabled: false, bars: 1, ...o.countIn },
        training: { enabled: false, startFactor: 1, endFactor: 1, mode: 'time', durationSec: 60, everyBars: 4, levels: [1, 1], stopAtEnd: false, ...o.training },
        interval: { enabled: false, workSec: 60, restSec: 0, rounds: 1, cues: false, ...o.interval },
    };
}

async function configure(o) {
    m.configure(JSON.stringify(config(o)));
    await m.loadSound('test'); // the click samples, so clicks aren't the stand-in tick
}

// moves the audio clock and the page's timers on together, 5 ms at a time
function advance(sec) {
    for (let ms = 0; ms < Math.round(sec * 1000); ms += 5) {
        audio.time = Math.round((audio.time + 0.005) * 1000) / 1000;
        vi.advanceTimersByTime(5);
    }
}

const times = (sounds = audio.sounds) => sounds.map(s => s.time);
const kinds = (sounds = audio.sounds) => sounds.map(s => s.kind);
const gaps = (sounds = audio.sounds) => times(sounds).slice(1).map((t, i) => t - sounds[i].time);
const last = type => events.filter(e => e.event === type).at(-1);

function expectClose(actual, expected, digits = 6) {
    expect(actual).toHaveLength(expected.length);
    actual.forEach((a, i) => expect(a, `#${i}`).toBeCloseTo(expected[i], digits));
}

beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'requestAnimationFrame', 'cancelAnimationFrame', 'performance', 'Date'] });
    vi.resetModules();
    audio = installFakeAudio();
    m = await import('../../src/Templing/wwwroot/js/metronome.js');
    events = [];
    m.init({ invokeMethodAsync: (name, json) => { events.push({ name, ...JSON.parse(json) }); return Promise.resolve(); } });
});

afterEach(() => {
    m.stop();
    vi.useRealTimers();
});

describe('clicks', () => {
    it('plays quarter notes at the tempo, accenting beat 1', async () => {
        await configure();
        m.start();
        advance(2.1);
        expectClose(times(), [0.08, 0.58, 1.08, 1.58, 2.08]); // the first click is a moment after start
        expect(kinds()).toEqual(['accent', 'beat', 'beat', 'beat', 'accent']);
    });

    it('plays subdivisions between the beats, quieter', async () => {
        await configure({ steps: [{ bpm: 60, beatsPerBar: 2, beatUnit: 4, subdivision: 3, bars: 0 }] });
        m.start();
        advance(2);
        expect(kinds()).toEqual(['accent', 'sub', 'sub', 'beat', 'sub', 'sub', 'accent']);
        gaps().forEach(g => expect(g).toBeCloseTo(1 / 3));
    });

    it('counts tempo in quarter notes: 6/8 clicks eighths', async () => {
        await configure({ steps: [{ bpm: 120, beatsPerBar: 6, beatUnit: 8, subdivision: 1, bars: 0 }] });
        m.start();
        advance(1.6);
        expect(kinds()).toEqual(['accent', 'beat', 'beat', 'beat', 'beat', 'beat', 'accent']);
        gaps().forEach(g => expect(g).toBeCloseTo(0.25));
    });

    it('plays at the tempo factor', async () => {
        await configure({ factor: 0.5 });
        m.start();
        advance(2.1);
        expectClose(times(), [0.08, 1.08, 2.08]);
    });

    it('without accentFirst, beat 1 is a plain beat', async () => {
        await configure({ accentFirst: false });
        m.start();
        advance(2.1);
        expect(kinds()).not.toContain('accent');
    });

    it('follows a score part bar by bar', async () => {
        // [masterBar, numerator, denominator]: a 2/4 bar, then 3/8
        await configure({ steps: [{ bpm: 120, beatsPerBar: 4, beatUnit: 4, subdivision: 1, bars: 2, scoreBars: [[0, 2, 4], [1, 3, 8]] }] });
        m.start();
        advance(1.9);
        expect(kinds()).toEqual(['accent', 'beat', 'accent', 'beat', 'beat', 'accent']);
        expectClose(gaps(), [0.5, 0.5, 0.25, 0.25, 0.25]);
    });

    it('plays a stand-in tick until the click sound has loaded', async () => {
        globalThis.fetch = () => Promise.resolve({ ok: false, status: 404, url: 'x' });
        const warn = vi.spyOn(console, 'warn').mockImplementation(() => { });
        m.configure(JSON.stringify(config({ sound: 'missing' })));
        await m.loadSound('missing');
        m.start();
        advance(0.6);
        expect(kinds()).toEqual(['tick', 'tick']);
        expect(warn).toHaveBeenCalled();
    });

    it('silent runs make no sound and skip the count-in, but still count practice time', async () => {
        await configure({ silent: true, countIn: { enabled: true, bars: 1 } });
        m.start();
        expect(last('started').phase).toBe('work');
        advance(2);
        expect(audio.sounds).toEqual([]);
        m.stop();
        expect(last('stopped').active).toBeCloseTo(1.92);
    });
});

describe('count-in', () => {
    it('counts a bar in before the work starts, and doesn\'t count it as practice', async () => {
        await configure({ countIn: { enabled: true, bars: 1 } });
        m.start();
        expect(last('started').phase).toBe('countin');
        advance(12.1);
        expectClose(times().slice(0, 6), [0.08, 0.58, 1.08, 1.58, 2.08, 2.58]);
        expect(kinds().slice(0, 5)).toEqual(['accent', 'beat', 'beat', 'beat', 'accent']);
        m.stop();
        const stopped = last('stopped');
        expect(stopped.running).toBe(false);
        expect(stopped.active).toBeCloseTo(12.1 - 2.08); // from the first work beat
    });

    it('restarting a section counts in again and keeps the time already practised', async () => {
        await configure({ countIn: { enabled: true, bars: 1 } });
        m.start();
        advance(5);
        m.restartSection();
        expect(last('state').phase).toBe('countin');
        advance(2);
        m.stop();
        expect(last('stopped').active).toBeCloseTo(5 - 2.08);
    });
});

describe('intervals', () => {
    it('plays rounds of work with rests between, then finishes', async () => {
        await configure({ interval: { enabled: true, workSec: 2, restSec: 1, rounds: 2 } });
        m.start();
        advance(6);
        // round 1: 0.08–2.08, rest to 3.08, round 2: 3.08–5.08, then the end tone (it plays even with cues off)
        expectClose(times(), [0.08, 0.58, 1.08, 1.58, 3.08, 3.58, 4.08, 4.58, 5.08]);
        expect(kinds().at(-1)).toBe('cue-end');
        const finished = last('finished');
        expect(finished.reason).toBe('interval');
        expect(finished.running).toBe(false); // the engine reports running=false with (and before) finished
        expect(finished.round).toBe(2);
        expect(finished.active).toBeCloseTo(4);
        expect(m.isRunning()).toBe(false);
    });

    it('cues 3-2-1 before each change, a low tone at the end of work and a high one to start', async () => {
        await configure({ interval: { enabled: true, workSec: 5, restSec: 4, rounds: 2, cues: true } });
        m.start();
        advance(15);
        const cues = audio.sounds.filter(s => s.kind.startsWith('cue'));
        expect(kinds(cues)).toEqual([
            'cue-lo', 'cue-lo', 'cue-lo', 'cue-end',   // work ends at 5.08
            'cue-lo', 'cue-lo', 'cue-lo', 'cue-hi',    // rest ends at 9.08
            'cue-lo', 'cue-lo', 'cue-lo', 'cue-end',   // work ends at 9.43 + 5
        ]);
        expectClose(times(cues), [2.08, 3.08, 4.08, 5.08, 6.08, 7.08, 8.08, 9.08, 11.43, 12.43, 13.43, 14.43]);
        // work starts a moment after the start cue
        expect(audio.sounds.find(s => s.time > 9.08 && !s.kind.startsWith('cue')).time).toBeCloseTo(9.43);
    });

    it('a rest can be skipped', async () => {
        await configure({ interval: { enabled: true, workSec: 2, restSec: 60, rounds: 2 } });
        m.start();
        advance(3);
        expect(last('state').phase).toBe('rest');
        m.skipRest();
        advance(0.1);
        expect(audio.sounds.at(-1)).toEqual({ time: 3.05, kind: 'accent' });
    });

    it('a rest-only countdown pips and finishes', async () => {
        await configure({ restFirstSec: 3, restOnly: true, interval: { cues: true } });
        m.start();
        expect(last('started').phase).toBe('rest');
        advance(4);
        expect(kinds()).toEqual(['cue-lo', 'cue-lo', 'cue-lo', 'cue-hi']);
        expectClose(times(), [0.08, 1.08, 2.08, 3.08]);
        expect(last('finished').reason).toBe('rest');
        expect(last('finished').active).toBe(0);
    });
});

describe('pause', () => {
    it('freezes the run and its practice time, and carries on from the same beat', async () => {
        await configure();
        m.start();
        advance(1);
        m.pause();
        expect(last('state').paused).toBe(true);
        const before = audio.sounds.length;
        advance(5);
        expect(audio.sounds.length).toBe(before);
        m.resume();
        advance(1);
        // 0.08, 0.58, 1.08 (beats 1-3) before; beat 4 plays straight away on resume
        expect(audio.sounds.slice(before)).toEqual([{ time: 6.05, kind: 'beat' }, { time: 6.55, kind: 'accent' }, { time: 7.05, kind: 'beat' }]);
        m.stop();
        expect(last('stopped').active).toBeCloseTo((1 - 0.08) + (7 - 6.05));
    });

    it('holds an interval countdown while paused', async () => {
        await configure({ interval: { enabled: true, workSec: 10, restSec: 0, rounds: 1 } });
        m.start();
        advance(4);
        m.pause();
        const remaining = last('state').remaining;
        expect(remaining).toBeCloseTo(10 - (4 - 0.08));
        advance(30);
        m.resume();
        // the same, give or take the short lead before the first click after a resume
        expect(last('state').remaining - remaining).toBeGreaterThanOrEqual(0);
        expect(last('state').remaining - remaining).toBeLessThanOrEqual(0.05 + 1e-9);
        advance(remaining + 0.2);
        expect(last('finished').reason).toBe('interval');
        expect(last('finished').active).toBeCloseTo(10);
    });
});

describe('sections', () => {
    const two = [{ bpm: 120, beatsPerBar: 2, beatUnit: 4, subdivision: 1, bars: 1 }, { bpm: 60, beatsPerBar: 2, beatUnit: 4, subdivision: 1, bars: 1 }];

    it('plays through the sections in order and finishes at the end', async () => {
        await configure({ steps: two, playThrough: true });
        m.start();
        advance(4);
        expectClose(times(), [0.08, 0.58, 1.08, 2.08]);
        expect(kinds()).toEqual(['accent', 'beat', 'accent', 'beat']);
        expect(last('finished').reason).toBe('end');
    });

    it('loops back to the first section', async () => {
        await configure({ steps: two, playThrough: true, loop: true });
        m.start();
        advance(3.7);
        expectClose(times(), [0.08, 0.58, 1.08, 2.08, 3.08, 3.58]);
        expect(m.isRunning()).toBe(true);
    });

    it('jumps to another section from the next click', async () => {
        await configure({ steps: two });
        m.start();
        advance(0.7);
        m.setStep(1);
        advance(2);
        // the next click was due at 1.08: it's the new section's first, at its tempo from there
        const after = audio.sounds.filter(s => s.time > 0.7);
        expectClose(times(after), [1.08, 2.08]);
        expect(after[0].kind).toBe('accent');
        expect(last('state').step).toBe(1);
    });
});

describe('a tempo change while playing', () => {
    it('moves the next click to where it falls at the new tempo', async () => {
        audio.time = 0.02;
        await configure();
        m.start();          // clicks at 0.1, 0.6…
        advance(0.33);      // halfway to the second
        m.configure(JSON.stringify(config({ factor: 2 })));
        advance(1);
        // half a beat left at 240 bpm is 0.125 s
        expectClose(times().slice(0, 4), [0.1, 0.475, 0.725, 0.975]);
    });
});

describe('speed trainer', () => {
    const twoFour = [{ bpm: 120, beatsPerBar: 2, beatUnit: 4, subdivision: 1, bars: 0 }];

    it('steps through its levels every N bars, on the bar line', async () => {
        await configure({ steps: twoFour, training: { enabled: true, mode: 'bars', everyBars: 1, startFactor: 1, endFactor: 1.5, levels: [1, 1.25, 1.5] } });
        m.start();
        advance(3);
        // 120, then 150, then 180 bpm
        expectClose(gaps().slice(0, 7), [0.5, 0.5, 0.4, 0.4, 1 / 3, 1 / 3, 1 / 3]);
    });

    it('can stop when it reaches the end', async () => {
        await configure({ steps: twoFour, training: { enabled: true, mode: 'bars', everyBars: 1, startFactor: 1, endFactor: 1.5, levels: [1, 1.25, 1.5], stopAtEnd: true } });
        m.start();
        advance(5);
        expect(audio.sounds.filter(s => !s.kind.startsWith('cue'))).toHaveLength(6); // a bar at each level
        expect(last('finished').reason).toBe('training');
    });

    it('over time, changes tempo on a bar line once the time has passed', async () => {
        await configure({ training: { enabled: true, mode: 'time', durationSec: 4, startFactor: 1, endFactor: 2, levels: [1, 2] } });
        m.start();
        advance(10);
        const g = gaps();
        const change = g.findIndex(x => x < 0.4);
        expect(change).toBeGreaterThan(0);
        expect(g.slice(0, change).every(x => Math.abs(x - 0.5) < 1e-6)).toBe(true);
        expect(g.slice(change).every(x => Math.abs(x - 0.25) < 1e-6)).toBe(true);
        // the click that starts the faster spacing is a bar's first, after the trainer's time
        expect(audio.sounds[change].kind).toBe('accent');
        expect(audio.sounds[change].time).toBeGreaterThanOrEqual(4.08);
        expect(last('state').trainingProgress).toBe(1);
    });

    it('reports how far it is to the next level', async () => {
        await configure({ steps: twoFour, training: { enabled: true, mode: 'bars', everyBars: 4, startFactor: 1, endFactor: 1.5, levels: [1, 1.25, 1.5] } });
        m.start();
        advance(0.3);
        m.pause(); // reports the state
        expect(last('state').trainingNext).toMatchObject({ bars: 4, level: 1 });
        expect(last('state').trainingProgress).toBe(0);
    });
});

describe('events to .NET', () => {
    it('reports start and stop, and tells the page', async () => {
        await configure();
        const stops = vi.fn();
        window.addEventListener('templing-stop', stops);
        m.start();
        expect(last('started')).toMatchObject({ name: 'OnEngineEvent', running: true, phase: 'work', step: 0 });
        m.stop();
        expect(last('stopped')).toMatchObject({ running: false, phase: 'stopped' });
        expect(stops).toHaveBeenCalled();
        window.removeEventListener('templing-stop', stops);
    });

    it('does nothing before it has a config', () => {
        m.start();
        expect(events).toEqual([]);
        expect(audio.contexts).toBe(0);
    });
});
