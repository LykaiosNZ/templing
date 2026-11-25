// Device link: two of a person's devices connect directly over WebRTC and send a backup between them. The relay
// (relay/ in the repo) only lets them swap a handshake; app data goes over the data channel, never through it.
//
// Keys. Pairing starts from a random secret that one device shows as a QR code: a link to …/pair#<secret>. Both
// devices work out the pair's key from it and keep that as a non-extractable CryptoKey in the 'devices' store; the
// secret itself is never stored. Everything after uses the key: relay room ids, and proving who's at the other end.
//
// A connection. Both devices join a relay room whose id is an HMAC of the key, so only the pair can find it (a new
// room every 10 minutes). The offerer (pairing: the device showing the code; later: the lower device id) sends an
// offer, the other an answer: ICE username and password, setup role, the fingerprint of a certificate made for this
// connection, and candidates (extract). Each side rebuilds a description from those fields (build). Once the data
// channel opens, each sends proof(key, role, its fingerprint, the other's): a relay that swapped a fingerprint to sit
// in the middle would be caught, since the two sides would have seen different pairs. Nothing else crosses until
// both proofs check out.
//
// Data channel messages are JSON text, plus binary chunks of a backup between {t:"send"} and {t:"sent"}:
//   {t:"proof", mac} · {t:"hello", id, name} · {t:"send", size} … {t:"sent"} → {t:"received"} | {t:"declined"}
//   {t:"request"} → a send or {t:"declined"} · {t:"forget"}
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

export function toBase64Url(bytes) {
    let s = '';
    for (const b of bytes) s += String.fromCharCode(b);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** A pairing secret from a link: exactly 32 bytes, base64url. Null for anything else. */
export function secretFrom(text) {
    if (typeof text !== 'string' || !/^[A-Za-z0-9_-]{43}$/.test(text)) return null;
    const bin = atob(text.replace(/-/g, '+').replace(/_/g, '/') + '=');
    return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

// ---------- keys

const hmacKey = (raw) => crypto.subtle.importKey('raw', raw, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
const sign = async (key, text) => new Uint8Array(await crypto.subtle.sign('HMAC', key, te.encode(text)));

/** The pair's key, from the pairing secret: the same on both devices, and not extractable. */
export async function pairKey(secret) {
    const raw = await sign(await hmacKey(secret), 'templing/pair-key/v1');
    return hmacKey(raw);
}

/** The relay room for pairing with this secret: only someone holding the link can work it out. */
export async function pairRoom(secret) {
    return toHex(await sign(await hmacKey(secret), 'templing/pair-room/v1'));
}

export const windowOf = (now) => Math.floor(now / WINDOW_MS);

/** The relay room where a pair meets during one 10-minute window. */
export async function syncRoom(key, window) {
    return toHex(await sign(key, `templing/sync-room/v1/${window}`));
}

/** What a device sends to prove it holds the pair's key and saw these two fingerprints. */
export async function proof(key, role, mine, theirs) {
    return toHex(await sign(key, `templing/proof/v1/${role}/${mine}/${theirs}`));
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
 * out; rejects with a reason ('closed' if the relay refused or dropped the room, 'left', 'timeout', 'proof',
 * 'cancelled'). Call cancel() to give up.
 */
export function connect({ relay, room, key, offerer, WebSocketImpl = WebSocket, PeerImpl = RTCPeerConnection }) {
    let cancel;
    const promise = new Promise((resolve, reject) => {
        const role = offerer ? 'offer' : 'answer';
        const ws = new WebSocketImpl(`${relay}/room/${room}`);
        const pc = new PeerImpl({ iceServers: ICE_SERVERS });
        const channel = pc.createDataChannel('templing', { negotiated: true, id: 0, ordered: true });
        channel.binaryType = 'arraybuffer';
        let mine = null, theirs = null, done = false, timer = null;

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
        ws.onclose = () => { if (!done) fail('closed'); };
        ws.onerror = () => { };

        channel.onopen = async () => {
            channel.send(JSON.stringify({ t: 'proof', mac: await proof(key, role, mine, theirs) }));
        };
        channel.onmessage = async (e) => {
            if (done) return;
            let m;
            try { m = JSON.parse(e.data); } catch { return fail('proof'); }
            const expected = await proof(key, offerer ? 'answer' : 'offer', theirs, mine);
            if (m.t !== 'proof' || m.mac !== expected) return fail('proof');
            done = true;
            clearTimeout(timer);
            try { ws.close(1000, 'connected'); } catch { }
            resolve(new Peer(pc, channel));
        };
    });
    return { promise, cancel: () => cancel?.() };
}

/** A connected, proven device: JSON messages and backups over the data channel. */
export class Peer {
    constructor(pc, channel) {
        this.pc = pc;
        this.channel = channel;
        this.onmessage = null;          // (message) for anything not part of a transfer
        this.onbackup = null;           // (Uint8Array) when a backup has arrived
        this.onprogress = null;         // (received, size) while one arrives
        this._incoming = null;          // { size, parts, received }
        this._ack = null;               // resolves the send waiting for received / declined
        this.closed = new Promise((resolve) => {
            const end = () => resolve();
            channel.addEventListener('close', end);
            pc.addEventListener('connectionstatechange', () => { if (['failed', 'closed'].includes(pc.connectionState)) end(); });
        });
        this.closed.then(() => { this._ack?.('closed'); this.close(); });
        channel.onmessage = (e) => this._receive(e.data);
    }

    send(message) {
        if (this.channel.readyState === 'open') this.channel.send(JSON.stringify(message));
    }

    /** Sends a backup; resolves 'received', 'declined' or 'closed'. */
    async sendBackup(bytes, onprogress) {
        const ack = new Promise((resolve) => { this._ack = resolve; });
        this.send({ t: 'send', size: bytes.length });
        this.channel.bufferedAmountLowThreshold = HIGH_WATER / 2;
        for (let at = 0; at < bytes.length; at += CHUNK) {
            if (this.channel.readyState !== 'open') return 'closed';
            if (this.channel.bufferedAmount > HIGH_WATER)
                await new Promise((r) => this.channel.addEventListener('bufferedamountlow', r, { once: true }));
            this.channel.send(bytes.subarray(at, Math.min(at + CHUNK, bytes.length)));
            onprogress?.(Math.min(at + CHUNK, bytes.length), bytes.length);
        }
        this.send({ t: 'sent' });
        const result = await ack;
        this._ack = null;
        return result;
    }

    _receive(data) {
        if (typeof data !== 'string') {
            const inc = this._incoming;
            if (!inc) return;
            const part = new Uint8Array(data);
            inc.received += part.length;
            if (inc.received > inc.size) { this._incoming = null; this.send({ t: 'declined' }); return; }
            inc.parts.push(part);
            this.onprogress?.(inc.received, inc.size);
            return;
        }
        let m;
        try { m = JSON.parse(data); } catch { return; }
        if (m.t === 'send') {
            if (!Number.isInteger(m.size) || m.size <= 0 || m.size > MAX_BACKUP) { this.send({ t: 'declined' }); return; }
            this._incoming = { size: m.size, parts: [], received: 0 };
        } else if (m.t === 'sent') {
            const inc = this._incoming;
            this._incoming = null;
            if (!inc || inc.received !== inc.size) { this.send({ t: 'declined' }); return; }
            const bytes = new Uint8Array(inc.size);
            let at = 0;
            for (const p of inc.parts) { bytes.set(p, at); at += p.length; }
            this.send({ t: 'received' });
            this.onbackup?.(bytes);
        } else if (m.t === 'received' || m.t === 'declined') {
            if (this._ack) this._ack(m.t);
            else this.onmessage?.(m);
        } else {
            this.onmessage?.(m);
        }
    }

    close() {
        try { this.channel.close(); } catch { }
        try { this.pc.close(); } catch { }
    }
}

// ---------- devices (the 'devices' store) and what .NET calls

const SELF = 'self';
let relay = null, dotnet = null, self = null;
let online = false;
const peers = new Map();       // device id -> Peer
const loops = new Map();       // device id -> { cancel } for the presence loop's current attempt
let pairing = null;            // { cancel } while pairing

const emit = (type, data) => dotnet?.invokeMethodAsync('OnLinkEvent', type, JSON.stringify(data ?? {}));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const cleanName = (name) => String(name ?? '').replace(/\s+/g, ' ').trim().slice(0, 40) || 'Unnamed device';

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

async function savePaired(id, name, key) {
    const row = { Id: id, Name: cleanName(name), Key: key, Paired: new Date().toISOString() };
    await putRow('devices', row);
    return row;
}

/** Swaps ids and names over a just-proven connection. Resolves the other device's { id, name }. */
function hello(peer) {
    return new Promise((resolve, reject) => {
        const t = setTimeout(() => reject('timeout'), 10_000);
        peer.onmessage = (m) => {
            if (m.t !== 'hello' || typeof m.id !== 'string' || !/^[0-9a-f]{16}$/.test(m.id)) return;
            clearTimeout(t);
            peer.onmessage = null;
            resolve({ id: m.id, name: cleanName(m.name) });
        };
        peer.send({ t: 'hello', id: self.DeviceId, name: self.Name });
    });
}

async function pair(secret, offerer) {
    pairCancel();
    const key = await pairKey(secret);
    const room = await pairRoom(secret);
    const attempt = connect({ relay, room, key, offerer });
    pairing = attempt;
    try {
        const peer = await attempt.promise;
        const other = await hello(peer);
        if (other.id === self.DeviceId) throw 'self';
        const row = await savePaired(other.id, other.name, key);
        emit('paired', describeDevice(row));
        attach(row, peer);
    } catch (reason) {
        if (reason !== 'cancelled') emit('pair-failed', { reason: String(reason) });
    } finally {
        if (pairing === attempt) pairing = null;
    }
}

/** Shows-the-code side: makes a secret, returns it (base64url, for the link) and waits in the pairing room. */
export function pairStart() {
    const secret = crypto.getRandomValues(new Uint8Array(32));
    pair(secret, true);
    return toBase64Url(secret);
}

/** Scanned-the-code side. False if the link's secret isn't one. */
export function pairJoin(text) {
    const secret = secretFrom(text);
    if (!secret) return false;
    pair(secret, false);
    return true;
}

export function pairCancel() {
    pairing?.cancel();
    pairing = null;
}

/** Wires up a connected device: its messages, backups, and going offline again. */
function attach(device, peer) {
    peers.get(device.Id)?.close();
    peers.set(device.Id, peer);
    loops.get(device.Id)?.cancel();
    emit('connected', describeDevice(device));
    peer.onmessage = async (m) => {
        if (m.t === 'hello' && typeof m.name === 'string') {
            device.Name = cleanName(m.name);
            await putRow('devices', device);
            emit('renamed', describeDevice(device));
        } else if (m.t === 'request') {
            emit('requested', describeDevice(device));
        } else if (m.t === 'declined') {
            emit('declined', describeDevice(device));
        } else if (m.t === 'forget') {
            await removeRow('devices', device.Id);
            emit('forgotten', { id: device.Id, name: device.Name });
            peer.close();
        }
    };
    peer.onprogress = (received, size) => emit('progress', { id: device.Id, done: received, size, incoming: true });
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
    let backoff = 2000;
    try {
        while (online && !peers.has(device.Id)) {
            const row = (await devices()).find((d) => d.Id === device.Id);
            if (!row) return;
            const now = Date.now(), w = windowOf(now);
            const attempt = connect({ relay, room: await syncRoom(row.Key, w), key: row.Key, offerer: self.DeviceId < row.Id });
            loop.cancel = attempt.cancel;
            const nextWindow = setTimeout(() => attempt.cancel(), (w + 1) * WINDOW_MS - now + 1000);
            try {
                const peer = await attempt.promise;
                clearTimeout(nextWindow);
                loops.delete(device.Id);
                attach(row, peer);
                return;
            } catch (reason) {
                clearTimeout(nextWindow);
                if (!online) return;
                if (reason === 'cancelled') { backoff = 2000; continue; }   // a new window: straight on to its room
                await sleep(backoff + Math.random() * 1000);
                backoff = Math.min(backoff * 2, 60_000);
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

/** Sends this device's backup to a connected device. Resolves 'received', 'declined', 'closed' or 'offline'. */
export async function send(id, bytes) {
    const peer = peers.get(id);
    if (!peer) return 'offline';
    return peer.sendBackup(bytes, (done, size) => emit('progress', { id, done, size, incoming: false }));
}

/** Asks a connected device for its backup. */
export function request(id) {
    const peer = peers.get(id);
    if (!peer) return false;
    peer.send({ t: 'request' });
    return true;
}

export function decline(id) {
    peers.get(id)?.send({ t: 'declined' });
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
    pairCancel();
    goOffline();
    self = null;
    return init(relay, dotnet);
}
