// A scripted run-through of Templing's demo mode (?demo: sample data in memory, the browser's own data untouched),
// for recording demo videos. Drives a real Chrome with Puppeteer, moving a visible pointer between clicks.
//
//   npm install
//   node walkthrough.mjs                              # watch it in a Chrome window (the dev server on :5075)
//   node walkthrough.mjs --record templing.webm       # and record it (needs ffmpeg on PATH)
//
// Options:
//   --url <address>     where the app is (default http://localhost:5075/; e.g. https://lykaiosnz.github.io/templing/)
//   --record <file>     record a .webm of the page with Puppeteer's screencast (needs ffmpeg)
//   --shots <dir>       also save a screenshot at the end of each scene, as <dir>/<n>-<scene>.png
//   --headless          no window (handy with --record)
//   --speed <n>         pauses × 1/n: 2 runs twice as fast (default 1)
//   --size <w>x<h>      the page size (default 1280x800)
//   --light             light mode instead of dark
//   --show-demo-chip    keep the "Demo" chip in the app bar (hidden by default, as it means nothing in a video)
//   --channel <name>    the installed browser to use: chrome (default), chrome-beta, msedge…
//   --browser <path>    or a browser executable
//
// The scenes are below (`scenes`): each is a function of the helpers, so adding or reordering one is a line or two.
// Selectors use the app's aria-labels and hrefs, which DESIGN.md keeps stable.

import puppeteer from 'puppeteer-core';
import { parseArgs } from 'node:util';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const { values: opt } = parseArgs({
    options: {
        url: { type: 'string', default: 'http://localhost:5075/' },
        record: { type: 'string' },
        shots: { type: 'string' },
        headless: { type: 'boolean', default: false },
        speed: { type: 'string', default: '1' },
        size: { type: 'string', default: '1280x800' },
        light: { type: 'boolean', default: false },
        'show-demo-chip': { type: 'boolean', default: false },
        channel: { type: 'string', default: 'chrome' },
        browser: { type: 'string' },
    },
});

const [width, height] = opt.size.split('x').map(Number);
const speed = Number(opt.speed) || 1;
const base = new URL(opt.url.endsWith('/') ? opt.url : opt.url + '/');

// ---------- the browser

const browser = await puppeteer.launch({
    headless: opt.headless,
    ...(opt.browser ? { executablePath: opt.browser } : { channel: opt.channel }),
    defaultViewport: null,
    args: [`--window-size=${width},${height + 140}`, '--autoplay-policy=no-user-gesture-required', '--hide-scrollbars'],
});
const [page] = await browser.pages();
await page.setViewport({ width, height });
await page.evaluateOnNewDocument(pointerScript, opt['show-demo-chip']);

// ---------- helpers for the scenes

const wait = ms => new Promise(r => setTimeout(r, ms / speed));
let mouse = { x: width / 2, y: height / 2 };

/** Glides the pointer to the element's centre and clicks it. */
async function click(selector, { pause = 600 } = {}) {
    const el = await page.waitForSelector(selector, { visible: true, timeout: 15000 });
    await el.scrollIntoView();
    const box = await el.boundingBox();
    const to = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    const steps = Math.max(8, Math.round(Math.hypot(to.x - mouse.x, to.y - mouse.y) / 18));
    await page.mouse.move(to.x, to.y, { steps });
    mouse = to;
    await wait(250);
    await page.mouse.click(to.x, to.y);
    await wait(pause);
}

/** Types like a person, a key at a time. */
async function type(text) {
    await page.keyboard.type(text, { delay: 110 / speed });
}

/** Goes to a page from the navigation drawer (href as in MainLayout: "", "library", "stats"…). */
const nav = href => click(`.app-drawer a[href="${href}"]`, { pause: 1200 });

/** Scrolls the page smoothly by `y` pixels and lets it settle. */
async function scroll(y, pause = 1200) {
    await page.evaluate(y => window.scrollBy({ top: y, behavior: 'smooth' }), y);
    await wait(pause);
}

/** Waits for a selector, if it shows up (e.g. a dialog after stopping). */
const appears = (selector, timeout = 2500) => page.waitForSelector(selector, { visible: true, timeout }).then(() => true, () => false);

// ---------- the scenes

const scenes = {
    async dashboard() {
        await wait(3000);
        await scroll(400, 1500);
        await scroll(-400, 1000);
    },

    async library() {
        await nav('library');
        await wait(1500);
        await click('#library-search');
        await type('sweep');
        await wait(1800);
        await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
        await page.keyboard.press('Backspace');
        await wait(1200);
    },

    async piece() {
        await click('a[href="library/demo-spain"]', { pause: 2500 });
        await scroll(500, 2500);   // tempo over time
        await scroll(600, 2500);   // sections and logs
        await scroll(-1100, 1000);
    },

    async metronome() {
        await click('a[href="metronome/piece/demo-spain"]', { pause: 2000 });
        await click('button[aria-label="Start"]', { pause: 6000 });
        await click('[aria-label="5 faster"]', { pause: 2500 });
        await click('[aria-label="5 faster"]', { pause: 4000 });
        await click('[aria-label="Stop"]', { pause: 1500 });
        // the session panel offers to log what was played: log it (the History scene shows it at the top)
        await click('.session-end button::-p-text(Log)', { pause: 2500 });
        await click('.mud-dialog button::-p-text(Save)', { pause: 1500 });
    },

    async plans() {
        await nav('plans');
        await wait(1500);
        await click('[aria-label="Run Daily warm-up"]', { pause: 2500 });
        await click('button[aria-label="Start"]', { pause: 7000 });   // the step's speed trainer
        await click('.plan-bar .plan-end', { pause: 1500 });
        // the step has practice time that isn't logged: discard it
        await click(await appears('.confirm-alt') ? '.confirm-alt' : '.confirm-ok', { pause: 1500 });
    },

    async timer() {
        await nav('timer');
        await click('button[aria-label="Start"]', { pause: 4000 });
        await click('[aria-label="Stop"]', { pause: 1500 });
        await click('.session-end button::-p-text(Discard)', { pause: 1000 });
    },

    async stats() {
        await nav('stats');
        await wait(2500);
        for (let i = 0; i < 4; i++) await scroll(450, 2000);
        await scroll(-2000, 800);
    },

    async history() {
        await nav('history');
        await wait(2500);
        await scroll(500, 1500);
        await scroll(-500, 800);
    },

    async theme() {
        await nav('');
        await click('[aria-label="Toggle dark mode"]', { pause: 2500 });
        await click('[aria-label="Toggle dark mode"]', { pause: 1500 });
    },
};

// ---------- run

let recorder;
try {
    await page.goto(new URL('?demo', base).href, { waitUntil: 'networkidle2' });
    await page.waitForSelector('.brand', { visible: true, timeout: 60000 });
    if (opt.light) await click('[aria-label="Toggle dark mode"]', { pause: 500 });
    await page.mouse.move(mouse.x, mouse.y);
    if (opt.record) {
        if (!opt.record.endsWith('.webm')) throw new Error('--record needs a .webm file name');
        recorder = await page.screencast({ path: opt.record });
    }
    if (opt.shots) mkdirSync(opt.shots, { recursive: true });
    let n = 0;
    for (const [name, scene] of Object.entries(scenes)) {
        console.log(`· ${name}`);
        await scene();
        if (opt.shots) await page.screenshot({ path: join(opt.shots, `${String(++n).padStart(2, '0')}-${name}.png`) });
    }
    console.log(opt.record ? `Recorded ${opt.record}` : 'Done');
} catch (e) {
    console.error(e.message.includes('ffmpeg') ? `${e.message}\nRecording needs ffmpeg on PATH (https://ffmpeg.org/download.html).` : e);
    process.exitCode = 1;
} finally {
    await recorder?.stop();
    await browser.close();
}

// Runs in the page (before the app): a pointer that follows the mouse and rings on clicks, since a recording shows none;
// and, for a video, no "Demo" chip.
function pointerScript(showChip) {
    addEventListener('DOMContentLoaded', () => {
        const style = document.createElement('style');
        style.textContent = `
            #demo-pointer { position: fixed; z-index: 100000; left: 0; top: 0; width: 22px; height: 22px; margin: -11px 0 0 -11px;
                border-radius: 50%; background: rgba(255,255,255,.35); border: 2px solid rgba(20,20,20,.6); pointer-events: none;
                box-shadow: 0 1px 6px rgba(0,0,0,.35); transition: transform .12s; }
            #demo-pointer.down { transform: scale(.7); }
            .demo-ring { position: fixed; z-index: 99999; width: 44px; height: 44px; margin: -22px 0 0 -22px; border-radius: 50%;
                border: 3px solid rgba(255,160,60,.9); pointer-events: none; animation: demo-ring .5s ease-out forwards; }
            @keyframes demo-ring { from { transform: scale(.3); opacity: 1; } to { transform: scale(1.3); opacity: 0; } }
            ${showChip ? '' : '.demo-chip { display: none !important; }'}`;
        document.head.appendChild(style);
        const dot = document.createElement('div');
        dot.id = 'demo-pointer';
        document.body.appendChild(dot);
        const at = e => { dot.style.left = e.clientX + 'px'; dot.style.top = e.clientY + 'px'; };
        addEventListener('mousemove', at, true);
        addEventListener('mousedown', e => {
            at(e); dot.classList.add('down');
            const ring = document.createElement('div');
            ring.className = 'demo-ring'; ring.style.left = e.clientX + 'px'; ring.style.top = e.clientY + 'px';
            document.body.appendChild(ring); setTimeout(() => ring.remove(), 600);
        }, true);
        addEventListener('mouseup', () => dot.classList.remove('down'), true);
    });
}
