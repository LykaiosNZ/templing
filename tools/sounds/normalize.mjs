// Peak-normalises the click samples in src/Templing/wwwroot/sounds to -1 dBFS, so every sound plays at the same peak
// level and the engine only ever turns them down (the volume slider). Each file is normalised on its own: the engine
// sets the accent / beat / subdivision levels itself.
//
//   node tools/sounds/normalize.mjs          (from the repo root; running it again changes nothing)
//
// Peak normalising: find the loudest sample in the file, work out the gain that would bring it to the target,
// and multiply every sample by that gain. The sound's shape doesn't change, only how loud it is overall.
// dBFS is decibels below full scale, the loudest value a sample can hold (0 dBFS); -1 leaves a little headroom.
//
// Only 16-bit PCM WAV, which is all the samples are. The file's other chunks are kept as they are.

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const TARGET_DB = -1;
const dir = join(dirname(fileURLToPath(import.meta.url)), '../../src/Templing/wwwroot/sounds');
// A 16-bit sample runs from -32768 to 32767; full scale is 32767. Decibels to a plain ratio: 10^(dB / 20),
// so -1 dB is about 0.89 of full scale (29204).
const target = Math.round(32767 * 10 ** (TARGET_DB / 20));

for (const name of readdirSync(dir).filter(f => f.endsWith('.wav')).sort()) {
    const path = join(dir, name);
    const buf = readFileSync(path);
    // A WAV file is a RIFF container: it starts with "RIFF", the file's size, then "WAVE". Anything else isn't one,
    // and rewriting its bytes as audio would corrupt it.
    if (buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WAVE') throw new Error(`${name}: not a WAV`);

    // After that header the file is a list of chunks, each a 4-letter id, its size, then its contents. Two matter:
    // "fmt " says how the audio is stored (format and bits per sample), "data" holds the samples themselves.
    // Others (metadata, markers) are skipped over and left untouched.
    let fmt = null, data = null;
    for (let at = 12; at + 8 <= buf.length;) {
        const id = buf.toString('ascii', at, at + 4), size = buf.readUInt32LE(at + 4);
        if (id === 'fmt ') fmt = { format: buf.readUInt16LE(at + 8), bits: buf.readUInt16LE(at + 22) };
        if (id === 'data') data = { start: at + 8, end: Math.min(buf.length, at + 8 + size) };
        at += 8 + size + (size & 1); // chunks are padded to an even length
    }
    if (!fmt || !data) throw new Error(`${name}: no fmt or data chunk`);
    // Format 1 is plain PCM: each sample is a whole number, here 16 bits (2 bytes, little-endian). The loops below
    // read and write exactly that, so stop on anything else (compressed audio, 24-bit, floating point).
    if (fmt.format !== 1 || fmt.bits !== 16) throw new Error(`${name}: not 16-bit PCM`);

    // The peak: the largest sample, ignoring sign (a wave swings both ways). Stereo channels are interleaved in
    // the data, so this covers both.
    let peak = 0;
    for (let i = data.start; i + 1 < data.end; i += 2) peak = Math.max(peak, Math.abs(buf.readInt16LE(i)));
    // Silence can't be normalised, and a file already at the target is left alone (so a second run changes nothing).
    if (peak === 0 || Math.abs(peak - target) <= 1) { console.log(`${name.padEnd(32)} unchanged`); continue; }

    // The same gain for every sample, rounded back to a whole number and kept in range.
    const gain = target / peak;
    for (let i = data.start; i + 1 < data.end; i += 2)
        buf.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(buf.readInt16LE(i) * gain))), i);
    writeFileSync(path, buf);
    console.log(`${name.padEnd(32)} ${(20 * Math.log10(peak / 32768)).toFixed(1)} dB -> ${TARGET_DB} dB (${gain >= 1 ? '+' : ''}${(20 * Math.log10(gain)).toFixed(1)} dB)`);
}
