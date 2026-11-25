// js/link.js's device manager (the part Services/Devices.cs drives): pairings in an in-memory IndexedDB, the
// reconnect loop, sends, and pairing attempts, over the fake relay and WebRTC from fakelink.js.
import 'fake-indexeddb/auto';
import { beforeEach, describe, expect, it } from 'vitest';
import * as link from '../../src/Templing/wwwroot/js/link.js';
import { getRows, putRow, clear } from '../../src/Templing/wwwroot/js/db.js';
import { fakeRelay, fakeWebRTC } from './fakelink.js';

const RELAY = 'wss://relay.test';
const OTHER = 'f'.repeat(16);
const events = [];
const dotnet = { invokeMethodAsync: async (method, type, json) => { if (method === 'OnLinkEvent') events.push([type, JSON.parse(json)]); } };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(check, ms = 2000) {
    const end = Date.now() + ms;
    while (!(await check())) {
        if (Date.now() > end) throw new Error('timed out');
        await wait(10);
    }
}
const paired = async () => (await getRows('devices')).filter((r) => r.Id !== 'self').map((r) => r.Id);

let self;
beforeEach(async () => {
    link.goOffline();
    link.pairCancel();
    await clear('devices');
    events.length = 0;
    self = JSON.parse(await link.init(RELAY, dotnet));
});

/** A device paired with this one (the row as pairing leaves it), and its end of a connection once both are online. */
async function pairedDevice() {
    const key = await link.pairKey(new Uint8Array(32).fill(1), 'a'.repeat(64), 'b'.repeat(64));
    await putRow('devices', { Id: OTHER, Name: 'Laptop', Key: key, Paired: new Date().toISOString() });
    const WebSocketImpl = fakeRelay(), PeerImpl = fakeWebRTC();
    link.useTransport({ WebSocketImpl, PeerImpl });
    await link.goOnline();
    const room = await link.syncRoom(key, link.windowOf(Date.now()));
    const peer = await link.connect({ relay: RELAY, room, key, offerer: OTHER < self.id, WebSocketImpl, PeerImpl }).promise;
    await until(() => events.some(([t]) => t === 'connected'));
    return peer;
}

describe('device manager', () => {
    it('keeps a device forgotten even if its new name arrives at the same moment', async () => {
        const peer = await pairedDevice();
        peer.send({ t: 'hello', id: OTHER, name: 'Laptop 2' });
        await wait(0);                 // the name update is being handled (reading the store)...
        await link.forget(OTHER);      // ...when Forget is tapped
        await wait(100);
        expect(await paired()).toEqual([]);
        expect(events.some(([t]) => t === 'renamed')).toBe(false);
    });

    it('takes a new name from a paired device', async () => {
        const peer = await pairedDevice();
        peer.send({ t: 'hello', id: OTHER, name: 'Laptop 2' });
        await until(() => events.some(([t]) => t === 'renamed'));
        expect((await getRows('devices')).find((r) => r.Id === OTHER).Name).toBe('Laptop 2');
    });

    it('says busy to a second send, and leaves the first one going', async () => {
        await pairedDevice();          // the other end never answers the offer
        const first = link.send(OTHER, new Uint8Array(10));
        await wait(20);
        expect(await link.send(OTHER, new Uint8Array(10))).toBe('busy');
        expect(events.some(([t]) => t === 'transfer-ended')).toBe(false);
        link.goOffline();
        expect(await first).toBe('closed');
    });

    it('runs one reconnect loop per device, even when Settings closes and opens again while it waits', async () => {
        const key = await link.pairKey(new Uint8Array(32).fill(1), 'a'.repeat(64), 'b'.repeat(64));
        await putRow('devices', { Id: OTHER, Name: 'Laptop', Key: key, Paired: new Date().toISOString() });
        let sockets = 0;
        class Unreachable { constructor() { sockets++; setTimeout(() => this.onclose?.()); } send() { } close() { } }
        link.useTransport({ WebSocketImpl: Unreachable, PeerImpl: fakeWebRTC() });
        await link.goOnline();
        await until(() => sockets === 1);
        await wait(50);                // failed; now waiting 2-3 s before trying again
        link.goOffline();
        await link.goOnline();         // Settings again, while the first loop sleeps
        await until(() => sockets === 2);
        await wait(3500);              // both waits are over
        expect(sockets).toBe(3);       // the new loop tried again; the old one stopped
        link.goOffline();
    }, 10_000);

    it('keeps to one pairing when New code is pressed twice', async () => {
        let sockets = 0;
        const Relay = fakeRelay();
        class Counted extends Relay { constructor(url) { super(url); sockets++; } }
        link.useTransport({ WebSocketImpl: Counted, PeerImpl: fakeWebRTC() });
        link.pairStart();
        link.pairStart();
        await wait(50);
        expect(sockets).toBe(1);       // the first was cancelled before it joined a room
        link.pairCancel();
    });
});
