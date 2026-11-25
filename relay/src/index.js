// Templing's relay: lets two devices swap a WebRTC handshake so they can connect directly. See README.md.
//
//   GET /room/<64 hex>   (WebSocket upgrade)  → the Room Durable Object of that name
//
// Only Templing's origins may open a room (browsers always send the real Origin), joins are rate limited per IP
// (per /64 for IPv6), and every message is checked against schema.js. The relay never carries app data.

import { isRoomId, originAllowed } from "./schema.js";

export { Room } from "./room.js";

/** The rate-limit key for an address: IPv4 as it is, IPv6 by its /64 (one subscriber usually has a whole /64), an
 * IPv4-mapped IPv6 address as its IPv4 address. Null when there's no address. */
export function limitKey(ip) {
    if (!ip) return null;
    const mapped = ip.match(/^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i);
    if (mapped) return mapped[1];
    if (!ip.includes(":")) return ip;
    const [head, tail = ""] = ip.toLowerCase().split("::");
    const left = head ? head.split(":") : [];
    const right = ip.includes("::") ? (tail ? tail.split(":") : []) : [];
    const groups = ip.includes("::") ? [...left, ...Array(Math.max(0, 8 - left.length - right.length)).fill("0"), ...right] : left;
    return groups.slice(0, 4).map((g) => g.replace(/^0+(?=.)/, "")).join(":") + "::/64";
}

export default {
    async fetch(request, env) {
        const match = new URL(request.url).pathname.match(/^\/room\/([^/]+)$/);
        if (!match || !isRoomId(match[1])) return new Response("Not found", { status: 404 });
        if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
            return new Response("Expected a WebSocket", { status: 426 });
        if (!originAllowed(request.headers.get("Origin"), env.ALLOWED_ORIGINS))
            return new Response("Forbidden", { status: 403 });

        // no limiter, no relay: a missing binding mustn't quietly turn the limit off
        if (!env.JOIN_LIMIT) return new Response("Unavailable", { status: 503 });
        // Cloudflare always sets the address; without one there's nothing to limit by, so no shared fallback key
        const key = limitKey(request.headers.get("CF-Connecting-IP"));
        if (!key) return new Response("Bad request", { status: 400 });
        const { success } = await env.JOIN_LIMIT.limit({ key });
        if (!success) return new Response("Too many requests", { status: 429 });

        return env.ROOMS.get(env.ROOMS.idFromName(match[1])).fetch(request);
    },
};
