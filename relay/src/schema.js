// What the relay will pass between two devices: WebRTC handshake fields and nothing else.
//
// The relay never forwards a client's text as it arrived. Each message is parsed, checked field by field against
// the fixed shapes below (no free-text fields, no unknown keys, small limits), and re-serialised, so what reaches
// the other device is the relay's own copy of a handshake. Anything else closes the connection. This keeps the
// relay useless for carrying content: the backup itself goes over the direct WebRTC data channel between the
// devices, never through here.
//
// Messages:
//   { t: "pair",  fp: hex64, mac: hex64 }                      a new device's certificate fingerprint, with a MAC
//                                                               keyed by the one-time secret from the pairing QR
//   { t: "offer" | "answer", ufrag, pwd, setup, cands: [cand] } the parts of an SDP the other side rebuilds
//   { t: "ice", cand }                                          a candidate found after the offer/answer
//   { t: "bye" }                                                done; the other side can close
// cand = { addr: IPv4 | IPv6 | <uuid>.local, port: 1-65535, type: "host" | "srflx" | "prflx" }
//
// The relay itself sends { t: "joined" } and { t: "left" } when the other device comes and goes.

export const MAX_MESSAGE = 1024;   // characters; a full offer with 8 candidates is under 700
export const MAX_CANDIDATES = 8;

const HEX64 = /^[0-9a-f]{64}$/;
const UFRAG = /^[A-Za-z0-9+/]{4,32}$/;     // ICE ufrag / pwd are ice-chars (RFC 8839)
const PWD = /^[A-Za-z0-9+/]{22,64}$/;
const SETUP = new Set(["actpass", "active", "passive"]);
const TYPES = new Set(["host", "srflx", "prflx"]);
const MDNS = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.local$/;
const OCTET = "(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)";
const IPV4 = new RegExp(`^${OCTET}(\\.${OCTET}){3}$`);
const IPV6 = /^[0-9a-f:]{2,39}$/;

/** A room id: 64 lowercase hex characters (a SHA-256 the devices work out; the relay never sees the secret). */
export const isRoomId = (id) => HEX64.test(id);

const hasExactly = (o, keys) => {
    const own = Object.keys(o);
    return own.length === keys.length && keys.every((k) => Object.hasOwn(o, k));
};

const isIPv6 = (s) => IPV6.test(s) && s.includes(":") && s.split(":").length <= 8 && !s.includes(":::");

function candidate(c) {
    if (!c || typeof c !== "object" || Array.isArray(c) || !hasExactly(c, ["addr", "port", "type"])) return null;
    const { addr, port, type } = c;
    if (typeof addr !== "string" || !(IPV4.test(addr) || isIPv6(addr) || MDNS.test(addr))) return null;
    if (!Number.isInteger(port) || port < 1 || port > 65535) return null;
    if (!TYPES.has(type)) return null;
    return { addr, port, type };
}

/** Checks one message from a device. Returns the copy to forward, or null if it isn't an allowed message. */
export function validate(text) {
    if (typeof text !== "string" || text.length > MAX_MESSAGE) return null;
    let m;
    try { m = JSON.parse(text); } catch { return null; }
    if (!m || typeof m !== "object" || Array.isArray(m)) return null;

    switch (m.t) {
        case "pair":
            if (!hasExactly(m, ["t", "fp", "mac"]) || !HEX64.test(m.fp) || !HEX64.test(m.mac)) return null;
            return JSON.stringify({ t: "pair", fp: m.fp, mac: m.mac });

        case "offer":
        case "answer": {
            if (!hasExactly(m, ["t", "ufrag", "pwd", "setup", "cands"])) return null;
            if (typeof m.ufrag !== "string" || !UFRAG.test(m.ufrag)) return null;
            if (typeof m.pwd !== "string" || !PWD.test(m.pwd)) return null;
            if (!SETUP.has(m.setup)) return null;
            if (!Array.isArray(m.cands) || m.cands.length > MAX_CANDIDATES) return null;
            const cands = m.cands.map(candidate);
            if (cands.includes(null)) return null;
            return JSON.stringify({ t: m.t, ufrag: m.ufrag, pwd: m.pwd, setup: m.setup, cands });
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
