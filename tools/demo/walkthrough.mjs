// A scripted run-through of Templing's demo build (-p:Demo=true: sample data in memory, the browser's own data untouched),
// for recording demo videos. Drives a real Chrome with Puppeteer, moving a visible pointer between clicks.
//
//   dotnet run --project src/Templing/Templing.csproj --launch-profile http --property:Demo=true   # the demo on :5075
//   npm install
//   node walkthrough.mjs                              # watch it in a Chrome window
//   node walkthrough.mjs --record templing.webm       # and record it (npm install brings ffmpeg: ffmpeg-static)
//
// Options:
//   --url <address>     where the demo is (default http://localhost:5075/; or https://lykaiosnz.github.io/templing/demo/)
//   --record <file>     record a .webm of the page with Puppeteer's screencast
//   --gif <file>        record a .gif instead (a .webm converted with ffmpeg, kept at the speed it was recorded:
//                       use --speed to make it quicker, so the clicks and animations still look natural)
//   --gif-width <px>    the GIF's width (default 960; the height follows)
//   --gif-fps <n>       the GIF's frames per second (default 15)
//   --mp4 <file>        record an .mp4 (H.264, full size and colour, far smaller than a GIF; GitHub plays one dropped
//                       into a README, issue or PR). Goes with --gif too: one run, both files
//   --mp4-crf <n>       the MP4's quality, 18 (sharper, bigger) to 28 (default 23)
//   --shots <dir>       also save a screenshot at the end of each scene, as <dir>/<n>-<scene>.png
//   --headless          no window (handy with --record, --gif and --mp4)
//   --speed <n>         pauses × 1/n: 2 runs twice as fast (default 1)
//   --size <w>x<h>      the page size (default 1280x800)
//   --light             light mode instead of dark
//   --hide-demo-chip    hide the "Demo" chip in the app bar
//   --channel <name>    the installed browser to use: chrome (default), chrome-beta, msedge…
//   --browser <path>    or a browser executable
//
// The scenes are below (`scenes`): each is a function of the helpers, so adding or reordering one is a line or two.
// Selectors use the app's aria-labels and hrefs, which DESIGN.md keeps stable.

import puppeteer from 'puppeteer-core';
import ffmpegPath from 'ffmpeg-static';   // a prebuilt ffmpeg for this platform, for recording
import { parseArgs } from 'node:util';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';

const { values: opt } = parseArgs({
    options: {
        url: { type: 'string', default: 'http://localhost:5075/' },
        record: { type: 'string' },
        gif: { type: 'string' },
        'gif-width': { type: 'string', default: '960' },
        'gif-fps': { type: 'string', default: '15' },
        mp4: { type: 'string' },
        'mp4-crf': { type: 'string', default: '23' },
        shots: { type: 'string' },
        headless: { type: 'boolean', default: false },
        speed: { type: 'string', default: '1' },
        size: { type: 'string', default: '1280x800' },
        light: { type: 'boolean', default: false },
        'hide-demo-chip': { type: 'boolean', default: false },
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
await page.evaluateOnNewDocument(pointerScript, opt['hide-demo-chip']);

// ---------- helpers for the scenes

const wait = ms => new Promise(r => setTimeout(r, ms / speed));
// for the app's own timing (the search's debounce), which --speed doesn't change
const realTime = ms => new Promise(r => setTimeout(r, ms));
let mouse = { x: width / 2, y: height / 2 };

/**
 * Waits until the page has drawn: no placeholder cards or rows showing (the Library draws its cards a moment after
 * the page, slower still on the dev server) and every chart drawn. Pauses only start after this, so each screen is
 * shown finished for as long as the scene says. A page that never settles is shown as it is, after a warning.
 */
async function ready(timeout = 20000) {
    try {
        await page.waitForFunction(() => {
            const shown = el => el.offsetParent !== null || getComputedStyle(el).position === 'fixed';
            if ([...document.querySelectorAll('.card-placeholder, .row-placeholder')].some(shown)) return false;
            // a chart box (.viz) without "No data yet" is drawn once ECharts has put its SVG in it
            return [...document.querySelectorAll('.viz')].every(v => v.querySelector('.viz-empty, svg'));
        }, { timeout, polling: 100 });
        // and one more frame, for what was just drawn to be painted
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
    } catch {
        console.warn(`  (${new URL(page.url()).pathname} still drawing after ${timeout / 1000} s; carrying on)`);
    }
}

/** Glides the pointer to the element's centre and clicks it, then waits for the page to draw (see ready). */
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
    await ready();
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

// ---------- the scenes

const scenes = {
    async dashboard() {
        await wait(3000);
        await scroll(400, 1500);
        await scroll(-400, 1000);
    },

    async library() {
        await nav('library');
        await page.waitForSelector('.piece-card-link, .piece-row', { visible: true });
        await wait(1500);
        await click('#library-search');
        await type('sweep');
        await realTime(400);   // the search waits for typing to stop
        await ready();
        await wait(1800);
        await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
        await page.keyboard.press('Backspace');
        await realTime(400);
        await ready();
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
        // the session panel offers to log what was played: log it
        await click('.session-end button::-p-text(Log)', { pause: 2500 });
        await click('.mud-dialog button::-p-text(Save)', { pause: 1500 });
    },

    async stats() {
        await nav('stats');
        await wait(2500);
        for (let i = 0; i < 4; i++) await scroll(450, 2000);
        await scroll(-2000, 800);
    },
};

// ---------- run

// a GIF or MP4 is recorded as a .webm first, somewhere temporary, and converted at the end
const converted = opt.gif || opt.mp4;
const tempDir = converted ? mkdtempSync(join(tmpdir(), 'templing-demo-')) : null;
const video = converted ? join(tempDir, 'walkthrough.webm') : opt.record;

let recorder, ok = false;
try {
    if (opt.gif && !opt.gif.endsWith('.gif')) throw new Error('--gif needs a .gif file name');
    if (opt.mp4 && !opt.mp4.endsWith('.mp4')) throw new Error('--mp4 needs a .mp4 file name');
    if (opt.record && converted) throw new Error('--record, or --gif and --mp4: not both');
    await page.goto(base.href, { waitUntil: 'networkidle2' });
    await page.waitForSelector('.brand', { visible: true, timeout: 60000 });
    await ready();
    // the scenes use the demo's sample pieces, and a normal build would be someone's real data
    if (!await page.$('.demo-chip')) throw new Error(`${base.href} isn't the demo build: run the app with --property:Demo=true (or use .../templing/demo/)`);
    if (opt.light) await click('[aria-label="Toggle dark mode"]', { pause: 500 });
    await page.mouse.move(mouse.x, mouse.y);
    if (video) {
        if (!video.endsWith('.webm')) throw new Error('--record needs a .webm file name');
        recorder = await page.screencast({ path: video, ffmpegPath });
    }
    if (opt.shots) mkdirSync(opt.shots, { recursive: true });
    let n = 0;
    for (const [name, scene] of Object.entries(scenes)) {
        console.log(`· ${name}`);
        await scene();
        if (opt.shots) await page.screenshot({ path: join(opt.shots, `${String(++n).padStart(2, '0')}-${name}.png`) });
    }
    ok = true;
} catch (e) {
    console.error(e.message.includes('ffmpeg') ? `${e.message}\nRecording uses ffmpeg from ffmpeg-static (${ffmpegPath}): run npm install again.` : e);
    process.exitCode = 1;
} finally {
    await recorder?.stop();
    await browser.close();
}
if (ok && converted) {
    for (const [file, kind, convert] of [[opt.gif, 'GIF', toGif], [opt.mp4, 'MP4', toMp4]]) {
        if (!file) continue;
        console.log(`· converting to ${kind}`);
        try { convert(video, file); console.log(`Recorded ${file}`); }
        catch (e) { console.error(`Couldn't make the ${kind}: ${e.message}`); process.exitCode = 1; }
    }
}
else if (ok) console.log(opt.record ? `Recorded ${opt.record}` : 'Done');
if (tempDir) rmSync(tempDir, { recursive: true, force: true });

// A .webm as a GIF at the same speed: fewer frames and a smaller size, and a palette made from this video
// (palettegen/paletteuse), as GIF's 256 colours otherwise band the app's dark gradients.
function toGif(from, to) {
    const fps = Number(opt['gif-fps']) || 15, w = Number(opt['gif-width']) || 960;
    const filter = `fps=${fps},scale=${w}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4`;
    execFileSync(ffmpegPath, ['-v', 'error', '-y', '-i', from, '-vf', filter, to], { stdio: 'inherit' });
}

// A .webm as an H.264 MP4 at the same size and speed. yuv420p, as the screencast's RGB frames won't play in most players
// as H.264; even dimensions, which yuv420p needs; faststart, so a browser plays it before it has all downloaded.
function toMp4(from, to) {
    const crf = String(Number(opt['mp4-crf']) || 23);
    execFileSync(ffmpegPath, ['-v', 'error', '-y', '-i', from, '-vf', 'scale=trunc(iw/2)*2:trunc(ih/2)*2',
        '-c:v', 'libx264', '-preset', 'slow', '-crf', crf, '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-an', to], { stdio: 'inherit' });
}

// Runs in the page (before the app): a pointer that follows the mouse and rings on clicks, since a recording shows none;
// and, if asked, no "Demo" chip.
function pointerScript(hideChip) {
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
            ${hideChip ? '.demo-chip { display: none !important; }' : ''}`;
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
