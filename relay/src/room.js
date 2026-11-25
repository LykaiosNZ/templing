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
 * A room allows MAX_JOINS joins within LIFETIME_MS of the first, and the expiry isn't moved by the room emptying, so
 * a device can't keep a room alive or reset its limits by reconnecting. When it expires it's wiped, and the same id
 * would start a new room; devices work out a fresh id for each time window anyway.
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
        try {
            const [client, server] = Object.values(new WebSocketPair());
            this.join(server);
            return new Response(null, { status: 101, webSocket: client });
        } catch {
            return new Response("Unavailable", { status: 503 });   // the join stays counted: one retry lost
        }
    }

    /** Counts a join, or returns the response refusing it. Storage calls hold the input gate, so joins can't interleave. */
    async admit() {
        const joins = (await this.ctx.storage.get("joins")) ?? 0;
        if (joins >= MAX_JOINS) return new Response("Room used up", { status: 410 });
        if (this.open().length >= MAX_PEERS) return new Response("Room full", { status: 409 });
        // both writes issued before awaiting, so they're committed together: a counted join always has an expiry
        const alarm = await this.ctx.storage.getAlarm();
        const writes = [this.ctx.storage.put("joins", joins + 1)];
        if (alarm === null) writes.push(this.ctx.storage.setAlarm(Date.now() + LIFETIME_MS));
        await Promise.all(writes);
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
        // a socket that's closing (the room expired, it broke the rules, or it's leaving) says nothing more
        if (ws.readyState !== OPEN || attachment(ws)?.left) return;
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
        // marked as gone first, so nothing from these sockets (a late message, their "left") reaches a device that
        // joins the same id after the wipe
        for (const ws of this.ctx.getWebSockets()) {
            try { ws.serializeAttachment({ ...attachment(ws), left: true }); } catch { /* already gone */ }
            close(ws, CLOSE.expired);
        }
        await this.ctx.storage.deleteAll();   // also deletes the alarm (compatibility date 2026-02-24 or later)
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
