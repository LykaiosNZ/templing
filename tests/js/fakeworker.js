// A Worker stand-in that runs the real script a page hands it, so a test can drive metronome.js's tick worker.
//
//   const workers = installFakeWorker();   // after vi.useFakeTimers(), before the code under test makes one
//   workers                                // every Worker made, each with .messages (what the page posted) and .terminated
//
// jsdom has no Worker, and no URL.createObjectURL either. Here a Blob's text is kept by the URL made for it, and
// `new Worker(url)` runs that text with its own onmessage / postMessage / timers. Messages go both ways on a 0 ms
// timeout, as a browser delivers them from its event loop, not straight away.
//
// The worker's timers are the ones in place when this is installed, so a test can then slow the page's own timers
// (as a browser does in a hidden tab) and the worker keeps time.
import { vi } from 'vitest';

export function installFakeWorker() {
    const workers = [];
    const scripts = new Map();
    const { setTimeout: later, setInterval: every, clearInterval: clear } = globalThis;
    let next = 0;

    const RealBlob = globalThis.Blob;
    class FakeBlob extends RealBlob {
        constructor(parts = [], options) { super(parts, options); this.text_ = parts.join(''); }
    }
    class FakeURL extends URL { }
    FakeURL.createObjectURL = blob => { const url = `blob:fake/${next++}`; scripts.set(url, blob.text_); return url; };
    FakeURL.revokeObjectURL = url => scripts.delete(url);

    class FakeWorker {
        constructor(url) {
            const source = scripts.get(url);
            if (source == null) throw new Error(`no script at ${url}`);
            this.onmessage = null;
            this.onerror = null;
            this.messages = [];
            this.terminated = false;
            const timers = new Set();
            this.timers = timers;
            const scope = {
                onmessage: null,
                postMessage: data => later(() => { if (!this.terminated) this.onmessage?.({ data }); }, 0),
                setInterval: (fn, ms) => { const id = every(fn, ms); timers.add(id); return id; },
                clearInterval: id => { clear(id); timers.delete(id); },
            };
            this.scope = scope;
            new Function('scope', `with (scope) {\n${source}\n}`)(scope);
            workers.push(this);
        }
        postMessage(data) {
            this.messages.push(data);
            later(() => { if (!this.terminated) this.scope.onmessage?.({ data }); }, 0);
        }
        terminate() {
            this.terminated = true;
            for (const id of this.timers) clear(id);
        }
    }

    vi.stubGlobal('Blob', FakeBlob);
    vi.stubGlobal('URL', FakeURL);
    vi.stubGlobal('Worker', FakeWorker);
    return workers;
}

// What a browser does to a hidden tab that isn't making sound: its timers fire at most once per `ms`
// (once a second at first, once a minute after a while). Workers made by installFakeWorker aren't affected.
export function throttlePageTimers(ms) {
    const { setTimeout: t, setInterval: i } = globalThis;
    vi.stubGlobal('setTimeout', (fn, delay, ...args) => t(fn, Math.max(delay ?? 0, ms), ...args));
    vi.stubGlobal('setInterval', (fn, delay, ...args) => i(fn, Math.max(delay ?? 0, ms), ...args));
}
