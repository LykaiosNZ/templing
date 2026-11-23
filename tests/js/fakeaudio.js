// A Web Audio stand-in for metronome.js: a clock the test moves by hand, and a record of every sound started on it.
//
//   const audio = installFakeAudio();   // before importing metronome.js
//   audio.time                          // AudioContext.currentTime, in seconds
//   audio.sounds                        // [{ time, kind }] in the order they were scheduled
//
// kind is what metronome.js asked for, read back from what it built: 'accent' (the hi sample), 'beat' (the
// lo sample at 0.8: beats and the count-in), 'sub' (the lo sample at 0.4), or a cue: 'cue-hi', 'cue-lo', 'cue-end' (sine tones).

const param = (value = 0) => ({ value, setValueAtTime() { }, exponentialRampToValueAtTime() { }, linearRampToValueAtTime() { } });
const node = extra => ({ connect(target) { this.out = target; }, disconnect() { }, ...extra });

const CUES = { 1046: 'cue-hi', 784: 'cue-lo', 523: 'cue-end' };

export function installFakeAudio() {
    const audio = { time: 0, sounds: [], contexts: 0 };

    class FakeAudioContext {
        constructor() { audio.contexts++; this.state = 'running'; this.destination = node(); }
        get currentTime() { return audio.time; }
        resume() { return Promise.resolve(); }
        createGain() { return node({ gain: param(1) }); }
        createDynamicsCompressor() {
            return node({ threshold: param(), knee: param(), ratio: param(), attack: param(), release: param() });
        }
        createOscillator() {
            return node({
                type: 'sine', frequency: param(440),
                start(time) {
                    // sine tones are cues; a square tick stands in for a click sample that hasn't loaded
                    const kind = this.type === 'sine' ? CUES[this.frequency.value] : 'tick';
                    audio.sounds.push({ time, kind });
                },
                stop() { },
            });
        }
        createBufferSource() {
            return node({
                buffer: null,
                start(time) {
                    const level = this.out.gain.value;
                    const kind = this.buffer.sample === 'hi' ? 'accent' : level > 0.5 ? 'beat' : 'sub';
                    audio.sounds.push({ time, kind });
                },
            });
        }
    }

    // click samples: sounds/<id>_hi.wav decodes to { sample: 'hi' }
    class FakeOfflineAudioContext {
        decodeAudioData(buffer) { return Promise.resolve(buffer); }
    }
    globalThis.fetch = url => Promise.resolve({
        ok: true, url,
        arrayBuffer: () => Promise.resolve({ sample: /_hi\.wav$/.test(url) ? 'hi' : 'lo' }),
    });

    window.AudioContext = FakeAudioContext;
    globalThis.OfflineAudioContext = FakeOfflineAudioContext;
    return audio;
}
