// Device link: two of a person's devices connect directly over WebRTC and send a backup between them. The relay
// (relay/ in the repo) only lets them swap a handshake; app data goes over the data channel, never through it.
//
// Pairing. One device shows a pairing code: ten characters someone can type (K7Q2M-9XDAB, about 50 random bits), as
// text and as a QR code of a link to …/pair#<code>. The other device scans the link or types the code. The code only
// proves the pairing (it can't be guessed in its 10 minutes, and the steps below don't rest on it being long): both devices join a relay room and prove (below) that they hold it, then each adds 32 fresh random bytes
// over the now encrypted channel, and the pair's key is HMAC(secret, both nonces). The device showing the code first
// commits to its nonce (sends its hash), the other then sends its nonce, and only then is the first revealed: neither
// side can pick its nonce after seeing the other's, so no one can try nonces until the two screens' codes match. So someone who learns the secret
// later (from the browser's history, a clipboard, a photo of the code) can't work out the key. Both screens then show
// a six-digit code derived from the key and that connection's two certificate fingerprints, and the device showing
// the QR code asks "Pair with …?": whoever got to the code first, and any relay in the middle, would show a different
// code. Only then do both keep the key, as a non-extractable CryptoKey in the 'devices' store.
//
// A connection. Both devices join a relay room whose id is an HMAC of the key (pairing: of the secret), so only the
// pair can find it (a new room every 10 minutes). The offerer (pairing: the device showing the code; later: the lower
// device id) sends an offer, the other an answer: ICE username and password, setup role, the fingerprint of a
// certificate made for this connection, and candidates (extract). Each side rebuilds a description from those fields
// (build). Once the data channel opens, each sends proof(key, role, its fingerprint, the other's): a relay that swapped
// a fingerprint to sit in the middle would be caught, since the two sides would have seen different pairs. Messages
// are handled strictly in order, and nothing but the proof is read until both proofs check out.
//
// Data channel messages are JSON text, plus binary chunks of a backup between {t:"accept"} and {t:"sent"}:
//   {t:"proof", mac}
//   pairing (pairExchange): showing {t:"pair", id, name, commit} → other {t:"pair", id, name, nonce}
//     → showing {t:"pair-reveal", nonce}; then {t:"pair-ok"} | {t:"pair-cancel"} from the device showing the code
//   {t:"hello", id, name} (names may change) · {t:"forget"}
//   {t:"send", size} → {t:"accept"} | {t:"declined"}; after accept the chunks, {t:"sent"} → {t:"received"} | {t:"declined"}
//   {t:"request"} → a send or {t:"request-declined"}
// A backup is only taken in once the receiving user accepts it, or after this device asked for it; one offer or
// request at a time per device, anything more is declined.
//
// .NET (Services/Devices.cs) drives it: init, pairing, online/offline while Settings is open, send/request/forget.
// Events go back through the DotNetObjectReference: OnLinkEvent(type, json) and OnBackupReceived(id, bytes).

import { getRows, putRow, removeRow } from './db.js';

export const WINDOW_MS = 10 * 60 * 1000;     // relay rooms last 10 minutes (relay/src/room.js), so the sync room changes as often
export const MAX_CANDIDATES = 8;             // the relay's limit per offer or answer
export const MAX_BACKUP = 200_000_000;       // as SettingsPage.MaxBackupBytes
const CHUNK = 16 * 1024;
const HIGH_WATER = 1 << 20;                  // pause sending while this much is queued on the channel
const CONNECT_MS = 20_000;                   // from the other device joining to the channel proving itself
const ICE_SERVERS = [{ urls: 'stun:stun.cloudflare.com:3478' }];   // finds a public address; never relays data (no TURN)
const PRIORITY = { host: 2130706431, prflx: 1862270975, srflx: 1694498815 };
const te = new TextEncoder();

// ---------- bytes

export const toHex = (bytes) => [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, '0')).join('');

// Pairing codes use Crockford's base 32: digits and capitals without I, L, O and U, so nothing reads as something
// else, and typing O, I or L for 0, 1 or 1 still works.
const CODE_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
export const CODE_LENGTH = 10;

/** A new pairing code: ten random characters (256 is a multiple of 32, so each is uniform). */
export function newCode() {
    return [...crypto.getRandomValues(new Uint8Array(CODE_LENGTH))].map((b) => CODE_ALPHABET[b & 31]).join('');
}

/** A pairing code as typed or linked ("k7q2m-9xdab", "K7Q2M 9XDAB", with O for 0…), in its one form; null if it isn't one. */
export function normalizeCode(text) {
    if (typeof text !== 'string') return null;
    const code = text.toUpperCase().replace(/[\s-]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1');
    return code.length === CODE_LENGTH && [...code].every((c) => CODE_ALPHABET.includes(c)) ? code : null;
}

/** The pairing secret a code stands for; null if the text isn't a code. */
export function secretFrom(text) {
    const code = normalizeCode(text);
    return code ? te.encode(`templing/pair-code/v1/${code}`) : null;
}

/** Compares two strings in time that doesn't depend on where they differ. */
export function sameText(a, b) {
    if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    return diff === 0;
}

const isHex = (s, bytes) => typeof s === 'string' && new RegExp(`^[0-9a-f]{${bytes * 2}}$`).test(s);

// ---------- keys

const hmacKey = (raw) => crypto.subtle.importKey('raw', raw, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
const sign = async (key, text) => new Uint8Array(await crypto.subtle.sign('HMAC', key, te.encode(text)));

/** The key that proves both devices hold the pairing secret, for the pairing connection only. */
export async function pairAuth(secret) {
    return hmacKey(await sign(await hmacKey(secret), 'templing/pair-auth/v2'));
}

/** The relay room for pairing with this secret: only someone holding the link can work it out. */
export async function pairRoom(secret) {
    return toHex(await sign(await hmacKey(secret), 'templing/pair-room/v2'));
}

/** The pair's key: the secret and both devices' fresh nonces (sent over the proven channel). Not extractable. */
export async function pairKey(secret, offerNonce, answerNonce) {
    return hmacKey(await sign(await hmacKey(secret), `templing/pair-key/v2/${offerNonce}/${answerNonce}`));
}

/** The six digits both screens show while pairing: the same only if both saw the same key and fingerprints. */
export async function matchCode(key, offerFp, answerFp) {
    const b = await sign(key, `templing/match/v2/${offerFp}/${answerFp}`);
    const n = ((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]) >>> 0;
    return String(n % 1_000_000).padStart(6, '0');
}

export const windowOf = (now) => Math.floor(now / WINDOW_MS);

/** The relay room where a pair meets during one 10-minute window. */
export async function syncRoom(key, window) {
    return toHex(await sign(key, `templing/sync-room/v2/${window}`));
}

/** What a device sends to prove it holds the key and saw these two fingerprints. */
export async function proof(key, role, mine, theirs) {
    return toHex(await sign(key, `templing/proof/v2/${role}/${mine}/${theirs}`));
}

// ---------- session descriptions

/** The handshake fields of a local description: what goes to the relay (relay/src/schema.js). */
export function extract(sdp) {
    const attr = (name) => sdp.match(new RegExp(`^a=${name}:(.+?)\\s*$`, 'm'))?.[1];
    const fp = sdp.match(/^a=fingerprint:sha-256 ([0-9A-Fa-f:]{95})\s*$/m)?.[1];
    const cands = [];
    for (const m of sdp.matchAll(/^a=candidate:\S+ (\d+) (\S+) \d+ (\S+) (\d+) typ (\S+)/gm)) {
        const [, component, protocol, addr, port, type] = m;
        if (component !== '1' || protocol.toLowerCase() !== 'udp' || !(type in PRIORITY)) continue;   // no TCP, no relay
        const cand = { addr: addr.toLowerCase(), port: Number(port), type };
        if (!cands.some((c) => c.addr === cand.addr && c.port === cand.port)) cands.push(cand);
    }
    const rank = { host: 0, srflx: 1, prflx: 2 };
    cands.sort((a, b) => rank[a.type] - rank[b.type]);
    return {
        ufrag: attr('ice-ufrag'),
        pwd: attr('ice-pwd'),
        setup: attr('setup'),
        fp: fp?.replace(/:/g, '').toLowerCase(),
        cands: cands.slice(0, MAX_CANDIDATES),
    };
}

/** A data-channel-only description from handshake fields, for setRemoteDescription. */
export function build(f) {
    const fp = f.fp.toUpperCase().match(/../g).join(':');
    const lines = [
        'v=0', 'o=- 1 2 IN IP4 127.0.0.1', 's=-', 't=0 0', 'a=group:BUNDLE 0', 'a=msid-semantic: WMS',
        'm=application 9 UDP/DTLS/SCTP webrtc-datachannel', 'c=IN IP4 0.0.0.0',
        `a=ice-ufrag:${f.ufrag}`, `a=ice-pwd:${f.pwd}`, 'a=ice-options:trickle',
        `a=fingerprint:sha-256 ${fp}`, `a=setup:${f.setup}`, 'a=mid:0', 'a=sctp-port:5000', 'a=max-message-size:262144',
    ];
    f.cands.forEach((c, i) => lines.push(
        `a=candidate:${i + 1} 1 udp ${PRIORITY[c.type] - i} ${c.addr} ${c.port} typ ${c.type}${c.type === 'host' ? '' : ' raddr 0.0.0.0 rport 0'}`));
    lines.push('a=end-of-candidates');
    return lines.join('\r\n') + '\r\n';
}

function gathered(pc, ms = 3000) {
    return new Promise((resolve) => {
        if (pc.iceGatheringState === 'complete') return resolve();
        const check = () => { if (pc.iceGatheringState === 'complete') { pc.removeEventListener('icegatheringstatechange', check); resolve(); } };
        pc.addEventListener('icegatheringstatechange', check);
        setTimeout(resolve, ms);
    });
}

// ---------- one connection

/**
 * Joins a relay room and connects to the device that joins it too. Resolves with a Peer once both proofs check
 * out; rejects with a reason ('unreachable' if the relay couldn't be reached or refused the room, 'closed' if it
 * dropped it, 'left', 'timeout', 'proof', 'cancelled'). onopen is called once the room is joined. Call cancel() to
 * give up.
 */
export function connect({ relay, room, key, offerer, onopen, WebSocketImpl = WebSocket, PeerImpl = RTCPeerConnection }) {
    let cancel;
    const promise = new Promise((resolve, reject) => {
        const role = offerer ? 'offer' : 'answer';
        const ws = new WebSocketImpl(`${relay}/room/${room}`);
        const pc = new PeerImpl({ iceServers: ICE_SERVERS });
        const channel = pc.createDataChannel('templing', { negotiated: true, id: 0, ordered: true });
        channel.binaryType = 'arraybuffer';
        let mine = null, theirs = null, done = false, timer = null;
        let checking = false;
        const early = [];      // whatever arrives after the other's proof, while ours is still being checked: kept, in order

        const fail = (reason) => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            try { ws.close(); } catch { }
            try { channel.close(); } catch { }
            try { pc.close(); } catch { }
            reject(reason);
        };
        cancel = () => fail('cancelled');
        const sendRelay = (m) => { try { ws.send(JSON.stringify(m)); } catch { fail('closed'); } };

        async function describe(kind) {
            const desc = kind === 'offer' ? await pc.createOffer() : await pc.createAnswer();
            await pc.setLocalDescription(desc);
            await gathered(pc);
            const f = extract(pc.localDescription.sdp);
            mine = f.fp;
            return { t: kind, ...f };
        }

        ws.onmessage = async (e) => {
            let m;
            try { m = JSON.parse(e.data); } catch { return; }
            try {
                if (m.t === 'joined') {
                    timer = setTimeout(() => fail('timeout'), CONNECT_MS);
                    if (offerer) sendRelay(await describe('offer'));
                } else if (m.t === 'offer' && !offerer && !theirs) {
                    theirs = m.fp;
                    await pc.setRemoteDescription({ type: 'offer', sdp: build(m) });
                    sendRelay(await describe('answer'));
                } else if (m.t === 'answer' && offerer && !theirs) {
                    theirs = m.fp;
                    await pc.setRemoteDescription({ type: 'answer', sdp: build(m) });
                } else if (m.t === 'left') {
                    fail('left');
                }
            } catch {
                fail('closed');
            }
        };
        let opened = false;
        ws.onopen = () => { opened = true; onopen?.(); };
        ws.onclose = () => { if (!done) fail(opened ? 'closed' : 'unreachable'); };
        ws.onerror = () => { };

        channel.onopen = async () => {
            channel.send(JSON.stringify({ t: 'proof', mac: await proof(key, role, mine, theirs) }));
        };
        // synchronous, so messages keep their order: the first must be the proof, the rest wait for it to check out
        channel.onmessage = (e) => {
            if (done) return;
            if (checking) { early.push(e.data); return; }
            checking = true;
            let m;
            try { m = typeof e.data === 'string' ? JSON.parse(e.data) : null; } catch { m = null; }
            if (m?.t !== 'proof') return fail('proof');
            proof(key, offerer ? 'answer' : 'offer', theirs, mine).then((expected) => {
                if (done) return;
                if (!sameText(m.mac, expected)) return fail('proof');
                done = true;
                clearTimeout(timer);
                try { ws.close(1000, 'connected'); } catch { }
                const fps = offerer ? { offer: mine, answer: theirs } : { offer: theirs, answer: mine };
                resolve(new Peer(pc, channel, fps, early));
            });
        };
    });
    return { promise, cancel: () => cancel?.() };
}

/** A connected, proven device: JSON messages and backups over the data channel. */
export class Peer {
    constructor(pc, channel, fps = {}, early = []) {
        this.pc = pc;
        this.channel = channel;
        this.fps = fps;                 // { offer, answer }: this connection's certificate fingerprints
        this._queue = [];               // messages that came while nothing listened, for the next onmessage
        this.onmessage = null;          // (message) for anything not part of a transfer
        this.onoffer = null;            // (size) the other device wants to send a backup: call accept() or decline()
        this.onbackup = null;           // (Uint8Array) when a backup has arrived
        this.onprogress = null;         // (received, size) while one arrives
        this.expecting = false;         // this device asked for a backup, so the next one is taken without asking
        this._offered = 0;              // the size of a backup offered and not yet accepted or declined
        this._incoming = null;          // { size, parts, received } once accepted
        this._ack = null;               // resolves the send waiting for accept / received / declined
        this._aborted = false;          // an incoming backup was turned down part way: its "sent" needs no reply
        this.onaborted = null;          // () an accepted or offered backup won't arrive after all
        this.closed = new Promise((resolve) => {
            const end = () => resolve();
            channel.addEventListener('close', end);
            pc.addEventListener('connectionstatechange', () => { if (['failed', 'closed'].includes(pc.connectionState)) end(); });
        });
        this.closed.then(() => {
            this._ack?.('closed');
            if (this._incoming || this._offered) { this._incoming = null; this._offered = 0; this.onaborted?.(); }
            this.close();
        });
        channel.onmessage = (e) => this._receive(e.data);
        // what came in while the proof was checked: once whoever awaited this connection has set its handlers
        if (early.length) setTimeout(() => { for (const d of early) this._receive(d); });
    }

    // Messages are never lost between one handler and the next (pairing awaits crypto between its steps, and the
    // other device may already have answered): while onmessage is null they wait here, and setting it hands them over.
    get onmessage() { return this._onmessage; }
    set onmessage(handler) {
        this._onmessage = handler;
        while (this._onmessage && this._queue.length) this._onmessage(this._queue.shift());
    }

    _deliver(m) {
        if (this._onmessage) this._onmessage(m);
        else if (this._queue.length < 64) this._queue.push(m);   // a few at most in practice; never unbounded
    }

    send(message) {
        if (this.channel.readyState === 'open') this.channel.send(JSON.stringify(message));
    }

    _reply() {
        return new Promise((resolve) => { this._ack = resolve; });
    }

    /** Offers a backup and sends it once accepted. Resolves 'received', 'declined' or 'closed'. */
    async sendBackup(bytes, onprogress) {
        if (this._ack) return 'declined';   // one at a time
        let reply = this._reply();
        this.send({ t: 'send', size: bytes.length });
        const answer = await reply;
        this._ack = null;
        if (answer !== 'accept') return answer;
        reply = this._reply();
        this.channel.bufferedAmountLowThreshold = HIGH_WATER / 2;
        for (let at = 0; at < bytes.length; at += CHUNK) {
            if (this.channel.readyState !== 'open') { this._ack = null; return 'closed'; }
            if (this.channel.bufferedAmount > HIGH_WATER) {
                // a closed channel never drains: don't wait for it
                await Promise.race([new Promise((r) => this.channel.addEventListener('bufferedamountlow', r, { once: true })), this.closed]);
                if (this.channel.readyState !== 'open') { this._ack = null; return 'closed'; }
            }
            this.channel.send(bytes.subarray(at, Math.min(at + CHUNK, bytes.length)));
            onprogress?.(Math.min(at + CHUNK, bytes.length), bytes.length);
        }
        this.send({ t: 'sent' });
        const result = await reply;
        this._ack = null;
        return result;
    }

    /** Takes the backup on offer. */
    accept() {
        if (!this._offered) return;
        this._incoming = { size: this._offered, parts: [], received: 0 };
        this._offered = 0;
        this.send({ t: 'accept' });
    }

    /** Turns down the backup on offer. */
    decline() {
        if (!this._offered) return;
        this._offered = 0;
        this.send({ t: 'declined' });
    }

    _receive(data) {
        if (typeof data !== 'string') {
            const inc = this._incoming;
            if (!inc) return;   // nothing accepted: dropped, never buffered
            const part = new Uint8Array(data);
            inc.received += part.length;
            if (inc.received > inc.size) {   // more than it said: turned down, once
                this._incoming = null;
                this._aborted = true;
                this.send({ t: 'declined' });
                this.onaborted?.();
                return;
            }
            inc.parts.push(part);
            this.onprogress?.(inc.received, inc.size);
            return;
        }
        let m;
        try { m = JSON.parse(data); } catch { return; }
        if (m.t === 'send') {
            const ok = Number.isInteger(m.size) && m.size > 0 && m.size <= MAX_BACKUP;
            if (!ok || this._offered || this._incoming) { this.send({ t: 'declined' }); return; }
            this._offered = m.size;
            if (this.expecting) { this.expecting = false; this.accept(); }
            else this.onoffer?.(m.size);
        } else if (m.t === 'sent') {
            const inc = this._incoming;
            this._incoming = null;
            if (!inc && this._aborted) { this._aborted = false; return; }   // already turned down
            if (!inc || inc.received !== inc.size) {
                this.send({ t: 'declined' });
                if (inc) this.onaborted?.();
                return;
            }
            const bytes = new Uint8Array(inc.size);
            let at = 0;
            for (const p of inc.parts) { bytes.set(p, at); at += p.length; }
            this.send({ t: 'received' });
            this.onbackup?.(bytes);
        } else if ((m.t === 'accept' || m.t === 'received' || m.t === 'declined') && this._ack) {
            this._ack(m.t);
        } else if (m.t !== 'accept' && m.t !== 'received' && m.t !== 'declined') {
            this._deliver(m);   // (a late transfer reply with no send waiting for it is dropped)
        }
    }

    close() {
        try { this.channel.close(); } catch { }
        try { this.pc.close(); } catch { }
    }
}

/** The next message of one of these kinds (anything else is ignored meanwhile), or a rejection on close / timeout. */
export function next(peer, kinds, ms) {
    return new Promise((resolve, reject) => {
        let settled = false;
        const finish = (fn, v) => {
            if (settled) return;
            settled = true;
            clearTimeout(t);
            if (peer.onmessage === handler) peer.onmessage = null;
            fn(v);
        };
        const t = setTimeout(() => finish(reject, 'timeout'), ms);
        const handler = (m) => { if (kinds.includes(m.t)) finish(resolve, m); };
        peer.onmessage = handler;
        peer.closed.then(() => finish(reject, 'closed'));
    });
}

/** The next pairing message of this kind; the other device cancelling at any step ends the pairing ('declined'). */
const step = (peer, kind) => next(peer, [kind, 'pair-cancel'], 15_000).then((m) => {
    if (m.t === 'pair-cancel') throw 'declined';
    return m;
});

const sha256 = async (text) => toHex(await crypto.subtle.digest('SHA-256', te.encode(text)));

/**
 * The pairing exchange over a proven connection: ids, names and nonces, committed before they're revealed, then the
 * matching code and the showing side's confirmation (ask). Resolves the other device as { id, name, key }; rejects
 * 'proof' (a bad message or nonce), 'self', 'declined' (cancelled on the other device), 'cancelled' (here),
 * 'closed' or 'timeout'. onCheck({ name, code, confirming }) shows the code.
 */
export async function pairExchange(peer, secret, showing, me, { ask, onCheck }) {
    const nonce = toHex(crypto.getRandomValues(new Uint8Array(32)));
    const valid = (m) => isHex(m.id, 8);
    let other, theirNonce;
    if (showing) {
        const reply = step(peer, 'pair');
        peer.send({ t: 'pair', id: me.id, name: me.name, commit: await sha256(nonce) });
        other = await reply;
        if (other.id === me.id) throw 'self';
        if (!valid(other) || !isHex(other.nonce, 32)) throw 'proof';
        theirNonce = other.nonce;
        peer.send({ t: 'pair-reveal', nonce });   // only now: their nonce is fixed
    } else {
        other = await step(peer, 'pair');
        if (other.id === me.id) throw 'self';
        if (!valid(other) || !isHex(other.commit, 32)) throw 'proof';
        const reveal = step(peer, 'pair-reveal');
        peer.send({ t: 'pair', id: me.id, name: me.name, nonce });
        theirNonce = (await reveal).nonce;
        if (!isHex(theirNonce, 32) || !sameText(await sha256(theirNonce), other.commit)) throw 'proof';
    }
    const name = cleanName(other.name);
    const key = await pairKey(secret, showing ? nonce : theirNonce, showing ? theirNonce : nonce);
    const code = await matchCode(key, peer.fps.offer, peer.fps.answer);
    await onCheck({ id: other.id, name, code, confirming: showing });
    if (showing) {
        // the person holding this device says the codes match, unless the other device cancels or goes first
        const cancelled = next(peer, ['pair-cancel'], WINDOW_MS).then(() => { throw 'declined'; });
        cancelled.catch(() => { });
        const ok = await Promise.race([ask(), cancelled]);
        if (!ok) { peer.send({ t: 'pair-cancel' }); throw 'cancelled'; }
        peer.send({ t: 'pair-ok' });
    } else {
        const word = await next(peer, ['pair-ok', 'pair-cancel'], WINDOW_MS);
        if (word.t !== 'pair-ok') throw 'declined';
    }
    return { id: other.id, name, key };
}

// ---------- devices (the 'devices' store) and what .NET calls

const SELF = 'self';
let relay = null, dotnet = null, self = null;
let online = false;
const peers = new Map();       // device id -> Peer
const loops = new Map();       // device id -> { cancel } for the presence loop's current attempt
let pairing = null;            // { cancel, peer, confirm } while pairing
let transport = {};            // the WebSocket and RTCPeerConnection to use: the browser's, or the tests' fakes

/** Tests only: fakes for the relay socket and WebRTC (connect's WebSocketImpl / PeerImpl). */
export function useTransport(t) { transport = t ?? {}; }

const emit = (type, data) => dotnet?.invokeMethodAsync('OnLinkEvent', type, JSON.stringify(data ?? {}));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** A device name as shown: no control or formatting characters (which could make it read as something else), one line, 40 characters. */
export function cleanName(name) {
    const clean = String(name ?? '').replace(/\s+/g, ' ').replace(/[\p{C}]/gu, '').trim();
    return [...clean].slice(0, 40).join('').trim() || 'Unnamed device';   // by character, so an emoji isn't cut in half
}

/** A starting name for this device, from the browser: "Chrome on Windows". The user can rename it. */
export function guessName(ua = navigator.userAgent) {
    const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
    const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android'
        : /Mac OS X/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : /Linux|CrOS/.test(ua) ? 'Linux' : null;
    return os ? `${browser} on ${os}` : browser;
}

const newId = () => toHex(crypto.getRandomValues(new Uint8Array(8)));
const devices = async () => (await getRows('devices')).filter((r) => r.Id !== SELF);
const describeDevice = (d) => ({ id: d.Id, name: d.Name, paired: d.Paired, connected: peers.has(d.Id) });

/** Starts the link: this device's id and name (made on first use). Returns them as JSON. */
export async function init(relayUrl, ref) {
    relay = relayUrl.replace(/\/+$/, '');
    dotnet = ref;
    self = (await getRows('devices')).find((r) => r.Id === SELF);
    if (!self) {
        self = { Id: SELF, DeviceId: newId(), Name: guessName() };
        await putRow('devices', self);
    }
    return JSON.stringify({ id: self.DeviceId, name: self.Name });
}

export async function list() {
    return JSON.stringify((await devices()).map(describeDevice));
}

export async function renameSelf(name) {
    self.Name = cleanName(name);
    await putRow('devices', self);
    for (const p of peers.values()) p.send({ t: 'hello', id: self.DeviceId, name: self.Name });
    return self.Name;
}

async function pair(secret, showing) {
    pairCancel();
    // claimed before anything is awaited, so a second New code or a closed dialog in the meantime cancels this one
    const state = { cancel: () => { state.cancelled = true; }, peer: null, confirm: null, cancelled: false };
    pairing = state;
    const room = await pairRoom(secret), key = await pairAuth(secret);
    if (state.cancelled || pairing !== state) return;
    const attempt = connect({
        ...transport, relay, room, key, offerer: showing,
        onopen: () => { if (pairing === state) emit('pair-ready'); },   // the code can be used now
    });
    state.cancel = attempt.cancel;
    try {
        const peer = state.peer = await attempt.promise;
        const other = await pairExchange(peer, secret, showing, { id: self.DeviceId, name: self.Name }, {
            ask: () => new Promise((r) => { state.confirm = r; }),
            onCheck: async (check) => {
                const again = (await devices()).some((d) => d.Id === check.id);
                if (pairing === state) emit('pair-check', { ...check, again });   // not into a newer pairing's dialog
            },
        });
        const row = { Id: other.id, Name: other.name, Key: other.key, Paired: new Date().toISOString() };
        attach(row, peer);     // its handlers first, so nothing the other device sends next is missed
        state.peer = null;     // it's the device's connection now
        await putRow('devices', row);
        emit('paired', describeDevice(row));
    } catch (reason) {
        const peer = state.peer;
        setTimeout(() => peer?.close(), 500);   // after a pair-cancel has had time to go
        if ((reason !== 'cancelled' || !showing) && pairing === state) emit('pair-failed', { reason: String(reason) });
    } finally {
        if (pairing === state) pairing = null;
    }
}

/** Shows-the-code side: makes a pairing code, returns it (for the text and the link) and waits in its room. */
export function pairStart() {
    const code = newCode();
    pair(secretFrom(code), true);
    return code;
}

/** Scanned-the-link or typed-the-code side. False if the text isn't a pairing code. */
export function pairJoin(text) {
    const secret = secretFrom(text);
    if (!secret) return false;
    pair(secret, false);
    return true;
}

/** The device showing the code: the person checked the codes match (true) or didn't (false). */
export function pairConfirm(ok) {
    pairing?.confirm?.(!!ok);
}

export function pairCancel() {
    const p = pairing;
    pairing = null;
    if (p?.confirm) { p.confirm(false); return; }   // pair() tells the other device, then closes
    p?.cancel();
    const peer = p?.peer;
    if (peer) {
        peer.send({ t: 'pair-cancel' });            // so the device showing the code stops asking
        setTimeout(() => peer.close(), 500);
    }
}

/** A transfer's progress, to .NET at most every 100 ms (each one redraws Settings), and always the last. */
function progress(id, incoming) {
    let last = 0;
    return (done, size) => {
        const now = Date.now();
        if (done < size && now - last < 100) return;
        last = now;
        emit('progress', { id, done, size, incoming });
    };
}

/** Wires up a connected device: its messages, backups, and going offline again. */
function attach(device, peer) {
    peers.get(device.Id)?.close();
    peers.set(device.Id, peer);
    loops.get(device.Id)?.cancel();
    emit('connected', describeDevice(device));
    peer.asked = false;   // the other device asked for this one's backup, and hasn't had an answer yet
    peer.onmessage = async (m) => {
        if (m.t === 'hello' && typeof m.name === 'string') {
            const name = cleanName(m.name);
            if (name === device.Name) return;   // nothing new (it says hello on every connection)
            // Forget and "Delete all data" take this connection out of peers at once: if that happened while the
            // store was read, the name update mustn't write the pairing back
            if (peers.get(device.Id) !== peer || !(await devices()).some((d) => d.Id === device.Id)) return;
            if (peers.get(device.Id) !== peer) return;   // and again after that read
            device.Name = name;
            await putRow('devices', device);
            emit('renamed', describeDevice(device));
        } else if (m.t === 'request') {
            if (peer.asked) return;   // one question at a time
            peer.asked = true;
            emit('requested', describeDevice(device));
        } else if (m.t === 'request-declined') {
            peer.expecting = false;
            emit('declined', describeDevice(device));
        } else if (m.t === 'forget') {
            await removeRow('devices', device.Id);
            emit('forgotten', { id: device.Id, name: device.Name });
            peer.close();
        }
    };
    peer.onoffer = (size) => emit('offered', { ...describeDevice(device), size });
    peer.onprogress = progress(device.Id, true);
    peer.onaborted = () => emit('transfer-ended', { id: device.Id });
    peer.onbackup = (bytes) => dotnet?.invokeMethodAsync('OnBackupReceived', device.Id, bytes);
    peer.send({ t: 'hello', id: self.DeviceId, name: self.Name });   // names may have changed since pairing
    peer.closed.then(() => {
        if (peers.get(device.Id) !== peer) return;
        peers.delete(device.Id);
        emit('disconnected', { id: device.Id });
        if (online) stayInTouch(device);
    });
}

/** While online, keeps trying to reach a paired device in its current sync room. */
async function stayInTouch(device) {
    if (loops.has(device.Id)) return;
    const loop = { cancel: () => { } };
    loops.set(device.Id, loop);
    // goOffline (or another loop) takes this one out of loops: checked after every wait, so a loop asleep in its
    // backoff when Settings closed doesn't carry on beside the new one when Settings opens again
    const current = () => online && loops.get(device.Id) === loop;
    let backoff = 2000;
    try {
        while (current() && !peers.has(device.Id)) {
            const row = (await devices()).find((d) => d.Id === device.Id);
            if (!row || !current()) return;
            const now = Date.now(), w = windowOf(now);
            const room = await syncRoom(row.Key, w);
            if (!current()) return;
            const attempt = connect({ ...transport, relay, room, key: row.Key, offerer: self.DeviceId < row.Id });
            loop.cancel = attempt.cancel;
            const nextWindow = setTimeout(() => attempt.cancel(), (w + 1) * WINDOW_MS - now + 1000);
            try {
                const peer = await attempt.promise;
                clearTimeout(nextWindow);
                if (!current()) { peer.close(); return; }
                loops.delete(device.Id);
                attach(row, peer);
                return;
            } catch (reason) {
                clearTimeout(nextWindow);
                if (!current()) return;
                if (reason === 'cancelled') { backoff = 2000; continue; }   // a new window: straight on to its room
                await sleep(backoff + Math.random() * 1000);                // jitter only; nothing secret
                backoff = Math.min(backoff * 2, 60_000);
                if (!current()) return;
            }
        }
    } finally {
        if (loops.get(device.Id) === loop) loops.delete(device.Id);
    }
}

/** Settings is open: look for every paired device. */
export async function goOnline() {
    online = true;
    for (const d of await devices()) if (!peers.has(d.Id)) stayInTouch(d);
}

/** Settings closed: stop looking, and close every connection. */
export function goOffline() {
    online = false;
    for (const l of loops.values()) l.cancel();
    loops.clear();
    for (const p of peers.values()) p.close();
    peers.clear();
}

/** Sends this device's backup to a connected device. Resolves 'received', 'declined', 'closed', 'busy' or 'offline'. */
export async function send(id, bytes) {
    const peer = peers.get(id);
    if (!peer) return 'offline';
    if (peer._ack) return 'busy';   // one at a time; the running one keeps its progress (and a request still waits for its answer)
    peer.asked = false;
    const result = await peer.sendBackup(bytes, progress(id, false));
    emit('transfer-ended', { id });
    return result;
}

/** Asks a connected device for its backup; the one that comes back is taken without asking again. */
export function request(id) {
    const peer = peers.get(id);
    if (!peer) return false;
    peer.expecting = true;
    peer.send({ t: 'request' });
    return true;
}

/** Takes the backup a device offered. */
export function accept(id) {
    peers.get(id)?.accept();
}

/** Says no: to a device's request for this one's backup, or to the backup it offered. */
export function decline(id) {
    const peer = peers.get(id);
    if (!peer) return;
    if (peer._offered) { peer.decline(); return; }
    if (peer.asked) { peer.asked = false; peer.send({ t: 'request-declined' }); }
}

/** Forgets a paired device here, and on it too if it's connected. */
export async function forget(id) {
    const peer = peers.get(id);
    if (peer) {
        peer.send({ t: 'forget' });
        peers.delete(id);
        setTimeout(() => peer.close(), 500);
    }
    loops.get(id)?.cancel();
    loops.delete(id);
    await removeRow('devices', id);
}

/** After "Delete all data" cleared the store: drop every connection and start again as a new device. */
export async function reset() {
    const wasOnline = online;
    pairCancel();
    goOffline();
    self = null;
    const me = await init(relay, dotnet);
    if (wasOnline) await goOnline();   // Settings is still open: a device paired from here on should connect
    return me;
}
