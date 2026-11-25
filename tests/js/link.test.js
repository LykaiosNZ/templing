import { describe, expect, it } from 'vitest';
import {
    build, cleanName, connect, extract, guessName, matchCode, pairAuth, pairExchange, pairKey, pairRoom, proof, sameText, secretFrom, syncRoom,
    toBase64Url, windowOf, WINDOW_MS,
} from '../../src/Templing/wwwroot/js/link.js';
import { validate } from '../../relay/src/schema.js';
import { chromeOffer, fakeRelay, fakeWebRTC } from './fakelink.js';

describe('descriptions', () => {
    it('extracts the handshake fields, without TCP, relay or duplicate candidates', () => {
        expect(extract(chromeOffer)).toEqual({
            ufrag: 'Xk9a',
            pwd: 'q1w2e3r4t5y6u7i8o9p0a+/b',
            setup: 'actpass',
            fp: 'abcdef0123456789'.repeat(4),
            cands: [
                { addr: '3f2b8c1d-1234-4abc-9def-0123456789ab.local', port: 54321, type: 'host' },
                { addr: '203.0.113.7', port: 61000, type: 'srflx' },
            ],
        });
    });

    it('sends what the relay accepts, unchanged', () => {
        const offer = { t: 'offer', ...extract(chromeOffer) };
        expect(validate(JSON.stringify(offer))).toBe(JSON.stringify(offer));
        const answer = { ...offer, t: 'answer', setup: 'active' };
        expect(validate(JSON.stringify(answer))).toBe(JSON.stringify(answer));
    });

    it('keeps to the relay limit of 8 candidates, hosts first', () => {
        const extra = Array.from({ length: 12 }, (_, i) => `a=candidate:${i} 1 udp 1 10.0.0.${i + 1} ${5000 + i} typ ${i % 2 ? 'srflx raddr 0.0.0.0 rport 0' : 'host'}`);
        const f = extract(chromeOffer.replace('a=ice-ufrag', extra.join('\r\n') + '\r\na=ice-ufrag'));
        expect(f.cands).toHaveLength(8);
        expect(f.cands.slice(0, 7).every((c) => c.type === 'host')).toBe(true);
    });

    it('builds a description that reads back as the same fields', () => {
        const f = extract(chromeOffer);
        const sdp = build(f);
        expect(sdp).toContain('a=fingerprint:sha-256 AB:CD:EF:01');
        expect(sdp).toContain('typ srflx raddr 0.0.0.0 rport 0');
        expect(sdp.endsWith('\r\n')).toBe(true);
        expect(extract(sdp)).toEqual(f);
    });
});

describe('keys', () => {
    const secret = new Uint8Array(32).map((_, i) => i);

    it('reads a pairing secret from its link text, and nothing else', () => {
        const text = toBase64Url(secret);
        expect(text).toHaveLength(43);
        expect(secretFrom(text)).toEqual(secret);
        expect(secretFrom(text.slice(1))).toBeNull();
        expect(secretFrom(text + 'A')).toBeNull();
        expect(secretFrom('!'.repeat(43))).toBeNull();
        expect(secretFrom(null)).toBeNull();
    });

    const nonce = (n) => String(n).repeat(64).slice(0, 64);

    it('gives both devices the same rooms and proofs from the same secret and nonces', async () => {
        const [a, b] = [await pairKey(secret, nonce(1), nonce(2)), await pairKey(new Uint8Array(secret), nonce(1), nonce(2))];
        expect(await pairRoom(secret)).toMatch(/^[0-9a-f]{64}$/);
        expect(await syncRoom(a, 7)).toBe(await syncRoom(b, 7));
        expect(await proof(a, 'offer', 'x', 'y')).toBe(await proof(b, 'offer', 'x', 'y'));
    });

    it('makes the pair key from both nonces, so the pairing secret alone is not enough', async () => {
        const key = await pairKey(secret, nonce(1), nonce(2));
        for (const other of [await pairKey(secret, nonce(1), nonce(3)), await pairKey(secret, nonce(2), nonce(1)), await pairAuth(secret)])
            expect(await syncRoom(other, 7)).not.toBe(await syncRoom(key, 7));
    });

    it('shows a six-digit code that changes with either fingerprint or the key', async () => {
        const key = await pairKey(secret, nonce(1), nonce(2));
        const code = await matchCode(key, 'aa', 'bb');
        expect(code).toMatch(/^\d{6}$/);
        expect(await matchCode(await pairKey(secret, nonce(1), nonce(2)), 'aa', 'bb')).toBe(code);
        expect(await matchCode(key, 'aa', 'cc')).not.toBe(code);
        expect(await matchCode(key, 'cc', 'bb')).not.toBe(code);
        expect(await matchCode(await pairKey(secret, nonce(1), nonce(4)), 'aa', 'bb')).not.toBe(code);
    });

    it('changes the room every window, and keeps rooms and proofs apart', async () => {
        const key = await pairKey(secret, nonce(1), nonce(2));
        expect(await syncRoom(key, 7)).not.toBe(await syncRoom(key, 8));
        expect(windowOf(7 * WINDOW_MS)).toBe(7);
        expect(windowOf(8 * WINDOW_MS - 1)).toBe(7);
        expect(await pairRoom(secret)).not.toBe(await syncRoom(key, 0));
    });

    it('makes a proof that fails if a fingerprint, the role or the key differs', async () => {
        const key = await pairAuth(secret);
        const other = await pairAuth(new Uint8Array(32).fill(9));
        const good = await proof(key, 'offer', 'aa', 'bb');
        expect(await proof(key, 'offer', 'aa', 'cc')).not.toBe(good);   // a relay swapped the other fingerprint
        expect(await proof(key, 'offer', 'bb', 'aa')).not.toBe(good);
        expect(await proof(key, 'answer', 'aa', 'bb')).not.toBe(good);
        expect(await proof(other, 'offer', 'aa', 'bb')).not.toBe(good);
    });
});

describe('sameText', () => {
    it('compares whole strings', () => {
        expect(sameText('abc', 'abc')).toBe(true);
        expect(sameText('abc', 'abd')).toBe(false);
        expect(sameText('abc', 'ab')).toBe(false);
        expect(sameText(undefined, 'abc')).toBe(false);
    });
});

describe('cleanName', () => {
    it('keeps a name to one plain line', () => {
        expect(cleanName('  My\nphone  ')).toBe('My phone');
        expect(cleanName('Lap\u202Etop')).toBe('Laptop');          // right-to-left override
        expect(cleanName('Phone\u200B\u0007')).toBe('Phone');     // zero-width space, bell
        expect(cleanName('x'.repeat(60))).toHaveLength(40);
        expect(cleanName('\u202E')).toBe('Unnamed device');
    });
});

describe('guessName', () => {
    it('names the browser and system', () => {
        expect(guessName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36')).toBe('Chrome on Windows');
        expect(guessName('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1')).toBe('Safari on iPhone');
        expect(guessName('Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36')).toBe('Chrome on Android');
        expect(guessName('Mozilla/5.0 (Macintosh; Intel Mac OS X 14.5; rv:130.0) Gecko/20100101 Firefox/130.0')).toBe('Firefox on Mac');
        expect(guessName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 Edg/140.0')).toBe('Edge on Windows');
    });
});

describe('connect', () => {
    const secret = new Uint8Array(32).fill(3);

    async function pairUp({ keyB, tamper } = {}) {
        const WebSocketImpl = fakeRelay({ tamper });
        const PeerImpl = fakeWebRTC();
        const key = await pairAuth(secret);
        const room = await pairRoom(secret);
        const a = connect({ relay: 'wss://relay.test', room, key, offerer: true, WebSocketImpl, PeerImpl });
        const b = connect({ relay: 'wss://relay.test', room, key: keyB ?? key, offerer: false, WebSocketImpl, PeerImpl });
        return Promise.allSettled([a.promise, b.promise]);
    }

    it('connects two devices with the same key, and sends a backup once the other accepts it', async () => {
        const [a, b] = await pairUp();
        expect(a.status).toBe('fulfilled');
        expect(b.status).toBe('fulfilled');
        expect(a.value.fps.offer).toBe(b.value.fps.offer);
        expect(a.value.fps.answer).toBe(b.value.fps.answer);
        const backup = crypto.getRandomValues(new Uint8Array(50_000));
        const got = new Promise((r) => { b.value.onbackup = r; });
        b.value.onoffer = (size) => { expect(size).toBe(50_000); b.value.accept(); };
        const progress = [];
        const result = await a.value.sendBackup(backup, (done, size) => progress.push([done, size]));
        expect(result).toBe('received');
        expect(await got).toEqual(backup);
        expect(progress.at(-1)).toEqual([50_000, 50_000]);
    });

    it('sends nothing when the other declines, and takes one it asked for without asking', async () => {
        const [a, b] = await pairUp();
        b.value.onoffer = () => b.value.decline();
        expect(await a.value.sendBackup(new Uint8Array(10))).toBe('declined');
        b.value.onoffer = () => { throw new Error('asked although it was expecting one'); };
        b.value.expecting = true;
        const got = new Promise((r) => { b.value.onbackup = r; });
        expect(await a.value.sendBackup(new Uint8Array([1, 2, 3]))).toBe('received');
        expect(await got).toEqual(new Uint8Array([1, 2, 3]));
    });

    it('drops data nobody accepted, and turns down a second offer while one waits', async () => {
        const [a, b] = await pairUp();
        const offers = [];
        b.value.onoffer = (size) => offers.push(size);
        let backups = 0;
        b.value.onbackup = () => backups++;
        a.value.channel.send(new Uint8Array(1000));          // chunks without an accepted offer
        const replies = [];
        recordSends(b.value, 'b', replies);
        a.value.send({ t: 'send', size: 5 });
        a.value.send({ t: 'send', size: 6 });
        await new Promise((r) => setTimeout(r, 20));
        expect(replies).toEqual(['b:declined']);   // the second offer, while the first waits
        a.value.send({ t: 'sent' });
        await new Promise((r) => setTimeout(r, 20));
        expect(offers).toEqual([5]);
        expect(backups).toBe(0);
        expect(b.value._incoming).toBeNull();
    });

    it('passes other messages through once connected', async () => {
        const [a, b] = await pairUp();
        const got = new Promise((r) => { b.value.onmessage = r; });
        a.value.send({ t: 'request' });
        expect(await got).toEqual({ t: 'request' });
    });

    it('says the relay is unreachable when its socket never opens, and closed when it drops after', async () => {
        const key = await pairAuth(secret);
        class Refused { constructor() { setTimeout(() => this.onclose?.()); } send() { } close() { } }
        class Dropped { constructor() { setTimeout(() => { this.onopen?.(); this.onclose?.(); }); } send() { } close() { } }
        const PeerImpl = fakeWebRTC();
        const opened = [];
        const refused = connect({ relay: 'wss://relay.test', room: 'r', key, offerer: true, WebSocketImpl: Refused, PeerImpl, onopen: () => opened.push('refused') });
        const dropped = connect({ relay: 'wss://relay.test', room: 'r', key, offerer: true, WebSocketImpl: Dropped, PeerImpl, onopen: () => opened.push('dropped') });
        await expect(refused.promise).rejects.toBe('unreachable');
        await expect(dropped.promise).rejects.toBe('closed');
        expect(opened).toEqual(['dropped']);
    });

    it('refuses a device that has a different key', async () => {
        const [a, b] = await pairUp({ keyB: await pairAuth(new Uint8Array(32).fill(4)) });
        expect(a).toEqual({ status: 'rejected', reason: 'proof' });
        expect(b).toEqual({ status: 'rejected', reason: 'proof' });
    });

    it('never connects if the relay changes a fingerprint', async () => {
        // the relay rewriting the answer's fingerprint: the offerer's connection won't link to anything real,
        // and with the fake there's nothing in the middle to answer, so the connect times out rather than proving
        const tamper = (text) => text.replace(/"fp":"[0-9a-f]{64}"/, `"fp":"${'ee'.repeat(32)}"`);
        const settled = await Promise.race([pairUp({ tamper }), new Promise((r) => setTimeout(() => r('pending'), 300))]);
        expect(settled).toBe('pending');
    });
});

// ---- pairing (pairExchange) and transfers over two connected fake devices

async function connectedPair(secret = new Uint8Array(32).fill(5)) {
    const WebSocketImpl = fakeRelay();
    const PeerImpl = fakeWebRTC();
    const key = await pairAuth(secret);
    const room = await pairRoom(secret);
    const a = connect({ relay: 'wss://relay.test', room, key, offerer: true, WebSocketImpl, PeerImpl });
    const b = connect({ relay: 'wss://relay.test', room, key, offerer: false, WebSocketImpl, PeerImpl });
    return Promise.all([a.promise, b.promise]);
}

/** Records every message each side sends, in order, as "side:type". */
function recordSends(peer, side, log, change) {
    const send = peer.send.bind(peer);
    peer.send = (m) => { log.push(`${side}:${m.t}`); send(change ? change(m) : m); };
}

describe('pairExchange', () => {
    const secret = new Uint8Array(32).fill(5);
    const me = (id, name) => ({ id: id.repeat(16).slice(0, 16), name });

    async function run({ ok = true, changeShowing, log = [], checks = [] } = {}) {
        const [show, join] = await connectedPair(secret);
        recordSends(show, 'show', log, changeShowing);
        recordSends(join, 'join', log);
        const shown = pairExchange(show, secret, true, me('a', 'Phone'), { ask: async () => ok, onCheck: (c) => checks.push(c) });
        const joined = pairExchange(join, secret, false, me('b', 'Laptop'), { ask: async () => true, onCheck: (c) => checks.push(c) });
        return { show, join, results: await Promise.allSettled([shown, joined]), log, checks };
    }

    it('pairs with one key and one code on both sides', async () => {
        const { results: [a, b], checks } = await run();
        expect(a.status).toBe('fulfilled');
        expect(b.status).toBe('fulfilled');
        expect(a.value).toMatchObject({ id: 'b'.repeat(16), name: 'Laptop' });
        expect(b.value).toMatchObject({ id: 'a'.repeat(16), name: 'Phone' });
        expect(await syncRoom(a.value.key, 1)).toBe(await syncRoom(b.value.key, 1));
        expect(checks.map((c) => c.code)[0]).toBe(checks.map((c) => c.code)[1]);
        expect(checks.find((c) => c.confirming).name).toBe('Laptop');
    });

    it('commits before revealing: the showing side sends its nonce only after the other has sent its own', async () => {
        const sent = [];
        const { log } = await run({ log: [], changeShowing: (m) => { sent.push(m); return m; } });
        expect(log).toEqual(['show:pair', 'join:pair', 'show:pair-reveal', 'show:pair-ok']);
        const first = sent[0];
        expect(first.commit).toMatch(/^[0-9a-f]{64}$/);
        expect(first.nonce).toBeUndefined();   // the first message carries only the commitment
    });

    it('refuses a reveal that does not match the commitment', async () => {
        const forged = 'f'.repeat(64);
        const { results: [, b] } = await run({ changeShowing: (m) => (m.t === 'pair-reveal' ? { ...m, nonce: forged } : m) });
        expect(b).toEqual({ status: 'rejected', reason: 'proof' });
    });

    it('tells the other side when the showing side says the codes differ', async () => {
        const { results: [a, b] } = await run({ ok: false });
        expect(a).toEqual({ status: 'rejected', reason: 'cancelled' });
        expect(b).toEqual({ status: 'rejected', reason: 'declined' });
    });

    it('stops asking when the other side cancels or goes', async () => {
        for (const how of ['cancel', 'close']) {
            const [show, join] = await connectedPair(secret);
            let asked;
            const asking = new Promise((r) => { asked = r; });
            const shown = pairExchange(show, secret, true, me('a', 'Phone'), { ask: () => { asked(); return new Promise(() => { }); }, onCheck: () => { } });
            const joined = pairExchange(join, secret, false, me('b', 'Laptop'), { ask: async () => true, onCheck: () => { } });
            joined.catch(() => { });
            await asking;
            if (how === 'cancel') join.send({ t: 'pair-cancel' }); else join.close();
            await expect(shown).rejects.toBe(how === 'cancel' ? 'declined' : 'closed');
        }
    });

    it('ends the pairing as cancelled when the other side cancels before revealing', async () => {
        const [show, join] = await connectedPair(secret);
        const joined = pairExchange(join, secret, false, me('b', 'Laptop'), { ask: async () => true, onCheck: () => { } });
        show.send({ t: 'pair', id: 'a'.repeat(16), name: 'Phone', commit: 'c'.repeat(64) });
        await new Promise((r) => setTimeout(r, 20));   // the joining side has sent its nonce and waits for the reveal
        show.send({ t: 'pair-cancel' });
        await expect(joined).rejects.toBe('declined');
    });

    it('refuses its own id', async () => {
        const [show, join] = await connectedPair(secret);
        const same = me('a', 'Twin');
        const shown = pairExchange(show, secret, true, same, { ask: async () => true, onCheck: () => { } });
        const joined = pairExchange(join, secret, false, same, { ask: async () => true, onCheck: () => { } });
        await expect(joined).rejects.toBe('self');
        join.close();   // as pair() does after a failure, so the other side stops waiting
        await expect(shown).rejects.toBe('closed');
    });
});

describe('transfers', () => {
    it('gives up a send when the connection closes while it waits for the channel to drain', async () => {
        const [a, b] = await connectedPair();
        b.onoffer = () => b.accept();
        let aborted = false;
        b.onaborted = () => { aborted = true; };
        a.channel.bufferedAmount = 1e9;   // never drains
        const sending = a.sendBackup(new Uint8Array(100_000));
        await new Promise((r) => setTimeout(r, 20));
        a.close();
        expect(await sending).toBe('closed');
        await new Promise((r) => setTimeout(r, 20));
        expect(aborted).toBe(true);
    });

    it('turns down more data than offered once, and says so once', async () => {
        const [a, b] = await connectedPair();
        b.onoffer = () => b.accept();
        let aborted = 0;
        b.onaborted = () => aborted++;
        const replies = [];
        recordSends(b, 'b', replies);
        a.send({ t: 'send', size: 5 });
        await new Promise((r) => setTimeout(r, 10));
        a.channel.send(new Uint8Array(10));   // twice what it offered
        a.send({ t: 'sent' });
        await new Promise((r) => setTimeout(r, 20));
        expect(replies).toEqual(['b:accept', 'b:declined']);   // accepted, then turned down once (not again at "sent")
        expect(aborted).toBe(1);
    });

    it('keeps a declined request apart from a send in progress', async () => {
        const [a, b] = await connectedPair();
        const other = [];
        a.onmessage = (m) => other.push(m.t);
        b.onoffer = () => { b.send({ t: 'request-declined' }); b.accept(); };   // a decline of some earlier Get, mid-offer
        const got = new Promise((r) => { b.onbackup = r; });
        expect(await a.sendBackup(new Uint8Array([7, 8]))).toBe('received');
        expect(await got).toEqual(new Uint8Array([7, 8]));
        expect(other).toEqual(['request-declined']);
    });
});
