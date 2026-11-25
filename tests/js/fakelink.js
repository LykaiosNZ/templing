// Fakes for js/link.js tests: a description as Chrome writes it, a relay (rooms of two sockets, every message
// checked by the relay's own validate()), and WebRTC connections that link up once each has the other's fingerprint.
import { extract } from '../../src/Templing/wwwroot/js/link.js';
import { validate } from '../../relay/src/schema.js';

// A description as Chrome writes it for a data channel (trimmed), with the kinds of candidates browsers gather.
export const chromeOffer = [
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

// ---- a fake relay and fake WebRTC, enough to run connect() end to end

/** The relay: rooms of two sockets, every message checked and re-serialised by the relay's own validate(). */
export function fakeRelay({ tamper } = {}) {
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
export function fakeWebRTC() {
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

