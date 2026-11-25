// What the relay will pass between two devices: WebRTC handshake fields and nothing else.
//
// The relay never forwards a client's text as it arrived. Each message is parsed, checked field by field against
// the fixed shapes below (no free-text fields, no unknown keys, small limits), and re-serialised, so what reaches
// the other device is the relay's own copy of a handshake. Anything else closes the connection. This keeps the
// relay useless for carrying content: the backup itself goes over the direct WebRTC data channel between the
// devices, never through here.
//
// Messages:
//   { t: "offer" | "answer", ufrag, pwd, setup, fp, cands: [cand] }
//                                                               the parts of an SDP the other side rebuilds; fp is
//                                                               the sender's DTLS certificate fingerprint (SHA-256)
//   { t: "ice", cand }                                          a candidate found after the offer/answer
//   { t: "bye" }                                                done; the other side can close
// cand = { addr: IPv4 | IPv6 | <uuid>.local, port: 1-65535, type: "host" | "srflx" | "prflx" }
//
// Devices make a new certificate for every connection, and once connected each proves to the other, over the
// encrypted data channel, that it holds their shared pairing key and saw the same two fingerprints. So a relay that
// swapped a fingerprint would be caught, and the relay never needs anything that identifies a device.
//
// Every field still carries some bits the sender picks (a fingerprint is 32 arbitrary bytes, an address is an
// address), so the room also limits how many of each message a connection may send (BUDGET): about 450 bytes of
// handshake per connection, with joins capped per room and per IP (room.js, index.js).
//
// The relay itself sends { t: "joined" } and { t: "left" } when the other device comes and goes.

export const MAX_MESSAGE = 1024;   // characters; a full offer with 8 candidates is under 700
export const MAX_CANDIDATES = 8;

const HEX64 = /^[0-9a-f]{64}$/;
// ICE ufrag / pwd are ice-chars (RFC 8839). Browsers send ufrag 4 (Chrome, Safari) or 8 (Firefox) characters and
// pwd 22-24 or 32, so allow just those ranges.
const UFRAG = /^[A-Za-z0-9+/]{4,8}$/;
const PWD = /^[A-Za-z0-9+/]{22,32}$/;
const SETUP = new Set(["actpass", "active", "passive"]);
const TYPES = new Set(["host", "srflx", "prflx"]);
// browsers hide host addresses behind a random (version 4) UUID
const MDNS = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\.local$/;
const OCTET = "(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)";
const IPV4 = new RegExp(`^${OCTET}(\\.${OCTET}){3}$`);
const IPV6_CHARS = /^[0-9A-Fa-f:.]{2,45}$/;

/** How many of each kind of message one connection may send. Anything more closes it. */
export const BUDGET = { sdp: 1, ice: 8, bye: 1 };
const KIND = { offer: "sdp", answer: "sdp", ice: "ice", bye: "bye" };
/** The budget a checked message counts against. */
export const kindOf = (t) => KIND[t];

/** A room id: 64 lowercase hex characters (a SHA-256 the devices work out; the relay never sees the secret). */
export const isRoomId = (id) => HEX64.test(id);

const hasExactly = (o, keys) => {
    const own = Object.keys(o);
    return own.length === keys.length && keys.every((k) => Object.hasOwn(o, k));
};

/** An IPv6 address in its one canonical spelling (RFC 5952, as URL writes it), or null. Forwarding the canonical
 * form means the way an address is written (leading zeros, where :: goes) can't carry anything. */
export function canonicalIPv6(s) {
    if (typeof s !== "string" || !IPV6_CHARS.test(s) || !s.includes(":")) return null;
    try {
        const host = new URL(`http://[${s}]`).hostname;
        return host.slice(1, -1);
    } catch {
        return null;
    }
}

function candidate(c) {
    if (!c || typeof c !== "object" || Array.isArray(c) || !hasExactly(c, ["addr", "port", "type"])) return null;
    const { addr, port, type } = c;
    if (typeof addr !== "string") return null;
    const clean = IPV4.test(addr) || MDNS.test(addr) ? addr : canonicalIPv6(addr);
    if (clean === null) return null;
    if (!Number.isInteger(port) || port < 1 || port > 65535) return null;
    if (!TYPES.has(type)) return null;
    return { addr: clean, port, type };
}

/** Checks one message from a device. Returns the copy to forward, or null if it isn't an allowed message. */
export function validate(text) {
    if (typeof text !== "string" || text.length > MAX_MESSAGE) return null;
    let m;
    try { m = JSON.parse(text); } catch { return null; }
    if (!m || typeof m !== "object" || Array.isArray(m)) return null;

    switch (m.t) {
        case "offer":
        case "answer": {
            if (!hasExactly(m, ["t", "ufrag", "pwd", "setup", "fp", "cands"])) return null;
            if (typeof m.ufrag !== "string" || !UFRAG.test(m.ufrag)) return null;
            if (typeof m.pwd !== "string" || !PWD.test(m.pwd)) return null;
            if (!SETUP.has(m.setup)) return null;
            if (typeof m.fp !== "string" || !HEX64.test(m.fp)) return null;
            if (!Array.isArray(m.cands) || m.cands.length > MAX_CANDIDATES) return null;
            const cands = m.cands.map(candidate);
            if (cands.includes(null)) return null;
            return JSON.stringify({ t: m.t, ufrag: m.ufrag, pwd: m.pwd, setup: m.setup, fp: m.fp, cands });
        }

        case "ice": {
            if (!hasExactly(m, ["t", "cand"])) return null;
            const cand = candidate(m.cand);
            return cand && JSON.stringify({ t: "ice", cand });
        }

        case "bye":
            return hasExactly(m, ["t"]) ? JSON.stringify({ t: "bye" }) : null;

        default:
            return null;
    }
}

/** Origins allowed to open a room: the configured list, plus localhost on any port for development. */
export function originAllowed(origin, allowed) {
    if (!origin) return false;
    if (/^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])(:\d{1,5})?$/.test(origin)) return true;
    return (allowed ?? "").split(",").map((s) => s.trim()).filter(Boolean).includes(origin);
}
