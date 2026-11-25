import { describe, expect, it } from 'vitest';
import {
    build, cleanName, connect, extract, guessName, matchCode, pairAuth, pairKey, pairRoom, proof, sameText, secretFrom, syncRoom,
    toBase64Url, windowOf, WINDOW_MS,
} from '../../src/Templing/wwwroot/js/link.js';
import { validate } from '../../relay/src/schema.js';

// A description as Chrome writes it for a data channel (trimmed), with the kinds of candidates browsers gather.
const chromeOffer = [
    'v=0', 'o=- 4611731400430051336 2 IN IP4 127.0.0.1', 's=-', 't=0 0', 'a=group:BUNDLE 0', 'a=extmap-allow-mixed',
    'a=msid-semantic: WMS', 'm=application 9 UDP/DTLS/SCTP webrtc-datachannel', 'c=IN IP4 0.0.0.0',
    'a=candidate:3015186216 1 udp 2113937151 3f2b8c1d-1234-4abc-9def-0123456789ab.local 54321 typ host generation 0 network-cost 999',
    'a=candidate:842163049 1 udp 1677729535 203.0.113.7 61000 typ srflx raddr 0.0.0.0 rport 0 generation 0 network-cost 999',
    'a=candidate:1 1 tcp 1518280447 3f2b8c1d-1234-4abc-9def-0123456789ab.local 9 typ host tcptype active',
    'a=candidate:2 1 udp 41885439 198.51.100.9 3478 typ relay raddr 203.0.113.7 rport 61000',
    'a=candidate:3015186216 1 udp 2113937151 3f2b8c1d-1234-4abc-9def-0123456789ab.local 54321 typ host generation 0',
    'a=ice-ufrag:Xk9a', 'a=ice-pwd:q1w2e3r4t5y6u7i8o9p0a+/b', 'a=ice-options:trickle',
    'a=fingerprint:sha-256 AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89',
    'a=setup:actpass', 'a=mid:0', 'a=sctp-port:5000', 'a=max-message-size:262144', '',
].join('\r\n');

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

// ---- a fake relay and fake WebRTC, enough to run connect() end to end

/** The relay: rooms of two sockets, every message checked and re-serialised by the relay's own validate(). */
function fakeRelay({ tamper } = {}) {
    const rooms = new Map();
    class FakeSocket {
        constructor(url) {
            const room = url.split('/room/')[1];
            if (!rooms.has(room)) rooms.set(room, []);
            this.peers = rooms.get(room);
            this.peers.push(this);
            setTimeout(() => {
                if (this.peers.length === 2) for (const s of this.peers) s.onmessage?.({ data: '{"t":"joined"}' });
            });
        }
        send(text) {
            let clean = validate(text);
            if (clean === null) throw new Error(`the relay would refuse: ${text}`);
            if (tamper) clean = tamper(clean);
            for (const s of this.peers) if (s !== this) setTimeout(() => s.onmessage?.({ data: clean }));
        }
        close() { this.closed = true; }
    }
    return FakeSocket;
}

/** WebRTC: two connections link up once each has the other's fingerprint (as DTLS insists), then their channels open. */
function fakeWebRTC() {
    const all = [];
    let n = 0;
    const sdp = (fp, setup) => chromeOffer
        .replace(/a=fingerprint:.*$/m, `a=fingerprint:sha-256 ${fp.toUpperCase().match(/../g).join(':')}`)
        .replace('a=setup:actpass', `a=setup:${setup}`);

    class FakeChannel {
        constructor() { this.readyState = 'connecting'; this.bufferedAmount = 0; this.listeners = {}; }
        addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }
        send(data) {
            if (this.readyState !== 'open') throw new Error('not open');
            const copy = typeof data === 'string' ? data : new Uint8Array(data).slice().buffer;
            setTimeout(() => this.other.onmessage?.({ data: copy }));
        }
        close() {
            if (this.readyState === 'closed') return;
            this.readyState = 'closed';
            for (const fn of this.listeners.close ?? []) fn();
            this.other?.close();
        }
    }

    class FakePC {
        constructor() {
            this.fp = (++n).toString(16).padStart(2, '0').repeat(32);
            this.iceGatheringState = 'complete';
            this.connectionState = 'new';
            all.push(this);
        }
        createDataChannel() { return (this.channel = new FakeChannel()); }
        async createOffer() { return { type: 'offer', sdp: sdp(this.fp, 'actpass') }; }
        async createAnswer() { return { type: 'answer', sdp: sdp(this.fp, 'active') }; }
        async setLocalDescription(d) { this.localDescription = d; this.link(); }
        async setRemoteDescription(d) { this.remote = extract(d.sdp).fp; this.link(); }
        addEventListener() { }
        close() { this.connectionState = 'closed'; this.channel?.close(); }
        link() {
            const other = all.find((p) => p !== this && p.fp === this.remote && p.remote === this.fp && p.localDescription);
            if (!other || !this.localDescription || this.channel.other) return;
            this.channel.other = other.channel;
            other.channel.other = this.channel;
            setTimeout(() => {
                for (const c of [this.channel, other.channel]) { c.readyState = 'open'; c.onopen?.(); }
            });
        }
    }
    return FakePC;
}

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
        a.value.send({ t: 'send', size: 5 });
        const second = new Promise((r) => { a.value.onmessage = r; });
        a.value.send({ t: 'send', size: 6 });
        expect(await second).toEqual({ t: 'declined' });
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
