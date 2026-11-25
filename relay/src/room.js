import { BUDGET, kindOf, validate } from "./schema.js";

export const MAX_PEERS = 2;
export const MAX_JOINS = 6;                     // per room, for its whole life: two devices with a couple of retries each
export const LIFETIME_MS = 10 * 60 * 1000;      // a room closes this long after its first join, however it's used

// Close codes (4000-4999 are the application's own).
export const CLOSE = {
    tooMany: [4002, "Too many messages"],
    notAllowed: [4003, "Not a handshake message"],
    expired: [4004, "Room expired"],
};

const OPEN = 1;   // WebSocket.OPEN

/**
 * One room: at most two devices swapping a WebRTC handshake. Uses the WebSocket Hibernation API, so an idle room
 * costs nothing and the object can be evicted between messages. State: the room's join count and expiry alarm
 * (storage, wiped when it expires), and each socket's message counts (its attachment). No message is stored or logged.
 *
 * A room id is good for one use: MAX_JOINS joins within LIFETIME_MS of the first, and the expiry isn't moved by the
 * room emptying, so a device can't keep a room alive by reconnecting. Devices work out a fresh id for each window.
 */
export class Room {
    constructor(ctx, env) {
        this.ctx = ctx;
        this.env = env;
        // keep-alives are answered without waking the object, only to the sender, and don't count as messages
        if (globalThis.WebSocketRequestResponsePair)
            ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    }

    async fetch() {
        const refused = await this.admit();
        if (refused) return refused;
        const [client, server] = Object.values(new WebSocketPair());
        this.join(server);
        return new Response(null, { status: 101, webSocket: client });
    }

    /** Counts a join, or returns the response refusing it. Storage calls hold the input gate, so joins can't interleave. */
    async admit() {
        const joins = (await this.ctx.storage.get("joins")) ?? 0;
        if (joins >= MAX_JOINS) return new Response("Room used up", { status: 410 });
        if (this.open().length >= MAX_PEERS) return new Response("Room full", { status: 409 });
        await this.ctx.storage.put("joins", joins + 1);
        if (joins === 0) await this.ctx.storage.setAlarm(Date.now() + LIFETIME_MS);
        return null;
    }

    /** Takes in a device's socket (the server end) and tells any device already here. */
    join(ws) {
        const others = this.open();
        this.ctx.acceptWebSocket(ws);
        ws.serializeAttachment({});
        for (const o of others) {
            send(o, { t: "joined" });
            send(ws, { t: "joined" });
        }
    }

    async webSocketMessage(ws, message) {
        const clean = validate(message);       // binary messages arrive as ArrayBuffer and fail here
        if (clean === null) return close(ws, CLOSE.notAllowed);

        const kind = kindOf(JSON.parse(clean).t);
        const used = ws.deserializeAttachment() ?? {};
        used[kind] = (used[kind] ?? 0) + 1;
        if (used[kind] > BUDGET[kind]) return close(ws, CLOSE.tooMany);
        ws.serializeAttachment(used);

        for (const o of this.open(ws)) send(o, clean);
    }

    async webSocketClose(ws) {
        this.leave(ws);
    }

    async webSocketError(ws) {
        this.leave(ws);
    }

    /** Tells the other device once, however the socket went (close and error can both be reported). */
    leave(ws) {
        const used = attachment(ws);
        if (used?.left) return;
        try { ws.serializeAttachment({ ...used, left: true }); } catch { /* socket already gone */ }
        for (const o of this.open(ws)) send(o, { t: "left" });
    }

    async alarm() {
        for (const ws of this.ctx.getWebSockets()) close(ws, CLOSE.expired);
        await this.ctx.storage.deleteAll();
    }

    /** Sockets that are open (not closing), other than `except`. */
    open(except) {
        return this.ctx.getWebSockets().filter((o) => o !== except && o.readyState === OPEN && !attachment(o)?.left);
    }
}

function attachment(ws) {
    try { return ws.deserializeAttachment(); } catch { return null; }
}

/** Sends, ignoring a socket that closed in the meantime. */
function send(ws, message) {
    try {
        if (ws.readyState === OPEN) ws.send(typeof message === "string" ? message : JSON.stringify(message));
    } catch { /* closed between the check and the send */ }
}

function close(ws, [code, reason]) {
    try { ws.close(code, reason); } catch { /* already closed */ }
}
