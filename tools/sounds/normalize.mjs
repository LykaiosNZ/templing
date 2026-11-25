// Peak-normalises the click samples in src/Templing/wwwroot/sounds to -1 dBFS, so every sound plays at the same peak
// level and the engine only ever turns them down (the volume slider). Each file is normalised on its own: the engine
// sets the accent / beat / subdivision levels itself.
//
//   node tools/sounds/normalize.mjs          (from the repo root; running it again changes nothing)
//
// Only 16-bit PCM WAV, which is all the samples are. The file's other chunks are kept as they are.

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const TARGET_DB = -1;
const dir = join(dirname(fileURLToPath(import.meta.url)), '../../src/Templing/wwwroot/sounds');
const target = Math.round(32767 * 10 ** (TARGET_DB / 20));

for (const name of readdirSync(dir).filter(f => f.endsWith('.wav')).sort()) {
    const path = join(dir, name);
    const buf = readFileSync(path);
    if (buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WAVE') throw new Error(`${name}: not a WAV`);

    let fmt = null, data = null;
    for (let at = 12; at + 8 <= buf.length;) {
        const id = buf.toString('ascii', at, at + 4), size = buf.readUInt32LE(at + 4);
        if (id === 'fmt ') fmt = { format: buf.readUInt16LE(at + 8), bits: buf.readUInt16LE(at + 22) };
        if (id === 'data') data = { start: at + 8, end: Math.min(buf.length, at + 8 + size) };
        at += 8 + size + (size & 1);
    }
    if (!fmt || !data) throw new Error(`${name}: no fmt or data chunk`);
    if (fmt.format !== 1 || fmt.bits !== 16) throw new Error(`${name}: not 16-bit PCM`);

    let peak = 0;
    for (let i = data.start; i + 1 < data.end; i += 2) peak = Math.max(peak, Math.abs(buf.readInt16LE(i)));
    if (peak === 0 || Math.abs(peak - target) <= 1) { console.log(`${name.padEnd(32)} unchanged`); continue; }

    const gain = target / peak;
    for (let i = data.start; i + 1 < data.end; i += 2)
        buf.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(buf.readInt16LE(i) * gain))), i);
    writeFileSync(path, buf);
    console.log(`${name.padEnd(32)} ${(20 * Math.log10(peak / 32768)).toFixed(1)} dB -> ${TARGET_DB} dB (${gain >= 1 ? '+' : ''}${(20 * Math.log10(gain)).toFixed(1)} dB)`);
}
