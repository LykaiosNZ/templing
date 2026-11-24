// The IndexedDB wrapper, on an in-memory IndexedDB. DataStore.cs writes JSON strings and reads getAll as UTF-8 JSON bytes.
import 'fake-indexeddb/auto';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import * as db from '../../src/Templing/wwwroot/js/db.js';

const STORES = ['pieces', 'logs', 'presets', 'settings', 'files', 'plans', 'session'];

// getAll's bytes, as the JSON text DataStore reads
const all = async store => new TextDecoder().decode(await db.getAll(store));

beforeEach(async () => { for (const s of STORES) await db.clear(s); });

describe('db.js', () => {
    it('creates every store the app uses', async () => {
        for (const s of STORES) expect(await all(s)).toBe('[]');
    });

    it('stores rows by Id, as JSON in and out', async () => {
        await db.put('pieces', JSON.stringify({ Id: 'a', Name: 'Spain' }));
        await db.put('pieces', JSON.stringify({ Id: 'a', Name: 'Spain (live)' }));
        expect(JSON.parse(await all('pieces'))).toEqual([{ Id: 'a', Name: 'Spain (live)' }]);
        expect(JSON.parse(await db.get('pieces', 'a')).Name).toBe('Spain (live)');
        expect(await db.get('pieces', 'missing')).toBeNull();
    });

    it('writes many rows in one go, removes and clears', async () => {
        await db.putMany('logs', JSON.stringify([{ Id: '1' }, { Id: '2' }, { Id: '3' }]));
        await db.remove('logs', '2');
        expect(JSON.parse(await all('logs')).map(r => r.Id)).toEqual(['1', '3']);
        await db.clear('logs');
        expect(await all('logs')).toBe('[]');
    });

    it('takes many rows as UTF-8 bytes too (a restore)', async () => {
        await db.putMany('logs', new TextEncoder().encode(JSON.stringify([{ Id: '1', Notes: 'café' }, { Id: '2' }])));
        expect(JSON.parse(await all('logs'))).toEqual([{ Id: '1', Notes: 'café' }, { Id: '2' }]);
    });

    it('keeps score files as bytes, never through JSON', async () => {
        const bytes = new Uint8Array([1, 2, 3, 250]);
        await db.putFile('f1', 'song.gp', bytes);
        expect(Array.from(await db.getFile('f1'))).toEqual([1, 2, 3, 250]);
        await db.removeFile('f1');
        expect(await db.getFile('f1')).toBeNull();
    });

    it('opens the database the app has always used', async () => {
        const names = (await indexedDB.databases()).map(d => d.name);
        expect(names).toContain('templing');
    });
});

// The demo build: DataStore calls useMemory() before anything else, and db.js then keeps the stores in memory.
describe('db.js in memory (the demo build)', () => {
    it('keeps everything in memory and never touches IndexedDB', async () => {
        vi.resetModules();
        const real = globalThis.indexedDB;
        globalThis.indexedDB = { open() { throw new Error('IndexedDB opened by the demo'); } };
        try {
            const demo = await import('../../src/Templing/wwwroot/js/db.js');
            demo.useMemory();

            const row = { Id: 'a', Name: 'Spain' };
            await demo.put('pieces', JSON.stringify(row));
            await demo.putMany('logs', new TextEncoder().encode(JSON.stringify([{ Id: '1' }, { Id: '2' }])));
            await demo.remove('logs', '1');
            expect(JSON.parse(new TextDecoder().decode(await demo.getAll('pieces')))).toEqual([row]);
            expect(JSON.parse(await demo.get('pieces', 'a'))).toEqual(row);
            expect(await demo.get('pieces', 'missing')).toBeNull();
            expect(JSON.parse(new TextDecoder().decode(await demo.getAll('logs')))).toEqual([{ Id: '2' }]);

            await demo.putFile('f1', 'song.gp', new Uint8Array([1, 2, 3]));
            expect(Array.from(await demo.getFile('f1'))).toEqual([1, 2, 3]);
            await demo.clear('pieces');
            expect(new TextDecoder().decode(await demo.getAll('pieces'))).toBe('[]');
        } finally {
            globalThis.indexedDB = real;
            vi.resetModules();
        }
    });

    it('refuses to switch once IndexedDB is open', () => {
        expect(() => db.useMemory()).toThrow();   // the tests above opened it
    });
});
