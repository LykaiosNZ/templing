// Templing's relay: lets two devices swap a WebRTC handshake so they can connect directly. See README.md.
//
//   GET /room/<64 hex>   (WebSocket upgrade)  → the Room Durable Object of that name
//
// Only Templing's origins may open a room (browsers always send the real Origin), joins are rate limited per IP,
// and every message is checked against schema.js. The relay never carries app data.

import { isRoomId, originAllowed } from "./schema.js";

export { Room } from "./room.js";

export default {
    async fetch(request, env) {
        const match = new URL(request.url).pathname.match(/^\/room\/([^/]+)$/);
        if (!match || !isRoomId(match[1])) return new Response("Not found", { status: 404 });
        if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
            return new Response("Expected a WebSocket", { status: 426 });
        if (!originAllowed(request.headers.get("Origin"), env.ALLOWED_ORIGINS))
            return new Response("Forbidden", { status: 403 });

        if (env.JOIN_LIMIT) {
            const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
            const { success } = await env.JOIN_LIMIT.limit({ key: ip });
            if (!success) return new Response("Too many requests", { status: 429 });
        }

        return env.ROOMS.get(env.ROOMS.idFromName(match[1])).fetch(request);
    },
};
