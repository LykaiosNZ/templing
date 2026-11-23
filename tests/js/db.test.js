// The IndexedDB wrapper, on an in-memory IndexedDB. DataStore.cs talks to it in JSON strings.
import 'fake-indexeddb/auto';
import { describe, it, expect, beforeEach } from 'vitest';
import * as db from '../../src/Templing/wwwroot/js/db.js';

const STORES = ['pieces', 'logs', 'presets', 'settings', 'files', 'plans', 'session'];

beforeEach(async () => { for (const s of STORES) await db.clear(s); });

describe('db.js', () => {
    it('creates every store the app uses', async () => {
        for (const s of STORES) expect(await db.getAll(s)).toBe('[]');
    });

    it('stores rows by Id, as JSON in and out', async () => {
        await db.put('pieces', JSON.stringify({ Id: 'a', Name: 'Spain' }));
        await db.put('pieces', JSON.stringify({ Id: 'a', Name: 'Spain (live)' }));
        expect(JSON.parse(await db.getAll('pieces'))).toEqual([{ Id: 'a', Name: 'Spain (live)' }]);
        expect(JSON.parse(await db.get('pieces', 'a')).Name).toBe('Spain (live)');
        expect(await db.get('pieces', 'missing')).toBeNull();
    });

    it('writes many rows in one go, removes and clears', async () => {
        await db.putMany('logs', JSON.stringify([{ Id: '1' }, { Id: '2' }, { Id: '3' }]));
        await db.remove('logs', '2');
        expect(JSON.parse(await db.getAll('logs')).map(r => r.Id)).toEqual(['1', '3']);
        await db.clear('logs');
        expect(await db.getAll('logs')).toBe('[]');
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
