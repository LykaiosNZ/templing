import { validate } from "./schema.js";

export const MAX_PEERS = 2;
export const MAX_MESSAGES = 32;                 // per connection: pair + offer/answer + a few ice + bye, with room to spare
export const LIFETIME_MS = 10 * 60 * 1000;      // a room closes this long after its first device joined

// Close codes (4000-4999 are the application's own).
export const CLOSE = {
    full: [4001, "Room full"],
    tooMany: [4002, "Too many messages"],
    notAllowed: [4003, "Not a handshake message"],
    expired: [4004, "Room expired"],
};

/**
 * One room: at most two devices swapping a WebRTC handshake. Uses the WebSocket Hibernation API, so an idle room
 * costs nothing and the object can be evicted between messages; the only state is each socket's message count
 * (its attachment) and the expiry alarm. Nothing is stored and no message is logged.
 */
export class Room {
    constructor(ctx, env) {
        this.ctx = ctx;
        this.env = env;
        // keep-alives are answered without waking the object, and don't count as messages
        if (globalThis.WebSocketRequestResponsePair)
            ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    }

    async fetch() {
        if (this.ctx.getWebSockets().length >= MAX_PEERS)
            return new Response(CLOSE.full[1], { status: 409 });
        const [client, server] = Object.values(new WebSocketPair());
        await this.join(server);
        return new Response(null, { status: 101, webSocket: client });
    }

    /** Takes in a device's socket (the server end) and tells any device already here. */
    async join(ws) {
        const others = this.ctx.getWebSockets();
        this.ctx.acceptWebSocket(ws);
        ws.serializeAttachment({ sent: 0 });
        if (others.length === 0) await this.ctx.storage.setAlarm(Date.now() + LIFETIME_MS);
        for (const o of others) {
            o.send(JSON.stringify({ t: "joined" }));
            ws.send(JSON.stringify({ t: "joined" }));
        }
    }

    async webSocketMessage(ws, message) {
        const state = ws.deserializeAttachment() ?? { sent: 0 };
        state.sent++;
        if (state.sent > MAX_MESSAGES) return ws.close(...CLOSE.tooMany);
        ws.serializeAttachment(state);

        const clean = validate(message);       // binary messages arrive as ArrayBuffer and fail here
        if (clean === null) return ws.close(...CLOSE.notAllowed);
        for (const o of this.others(ws)) o.send(clean);
    }

    async webSocketClose(ws) {
        for (const o of this.others(ws)) o.send(JSON.stringify({ t: "left" }));
        if (this.others(ws).length === 0) await this.ctx.storage.deleteAlarm();
    }

    async webSocketError(ws) {
        await this.webSocketClose(ws);
    }

    async alarm() {
        for (const ws of this.ctx.getWebSockets()) ws.close(...CLOSE.expired);
    }

    others(ws) {
        return this.ctx.getWebSockets().filter((o) => o !== ws);
    }
}
