import { beforeEach, describe, expect, it } from "vitest";
import { CLOSE, LIFETIME_MS, MAX_MESSAGES, Room } from "../src/room.js";

// Stand-ins for the Durable Object state and hibernatable sockets: just enough of the API Room uses.
class FakeSocket {
    constructor() { this.sent = []; this.closed = null; this.attachment = null; }
    send(m) { this.sent.push(m); }
    close(code, reason) { this.closed = { code, reason }; }
    serializeAttachment(a) { this.attachment = structuredClone(a); }
    deserializeAttachment() { return structuredClone(this.attachment); }
}

class FakeState {
    constructor() { this.sockets = []; this.alarm = null; }
    getWebSockets() { return this.sockets.filter((s) => !s.closed); }
    acceptWebSocket(ws) { this.sockets.push(ws); }
    setWebSocketAutoResponse() {}
    storage = {
        setAlarm: async (t) => { this.alarm = t; },
        deleteAlarm: async () => { this.alarm = null; },
    };
}

const bye = JSON.stringify({ t: "bye" });

let state, room;
beforeEach(() => { state = new FakeState(); room = new Room(state, {}); });

async function joined() {
    const ws = new FakeSocket();
    await room.join(ws);
    return ws;
}

describe("Room", () => {
    it("tells both devices when the second one joins, and sets the expiry with the first", async () => {
        const before = Date.now();
        const a = await joined();
        expect(state.alarm).toBeGreaterThanOrEqual(before + LIFETIME_MS);
        expect(a.sent).toEqual([]);
        const alarm = state.alarm;
        const b = await joined();
        expect(state.alarm).toBe(alarm);
        expect(a.sent).toEqual(['{"t":"joined"}']);
        expect(b.sent).toEqual(['{"t":"joined"}']);
    });

    it("forwards a checked message to the other device only", async () => {
        const a = await joined();
        const b = await joined();
        await room.webSocketMessage(a, JSON.stringify({ t: "bye" }, null, 2));
        expect(b.sent.at(-1)).toBe(bye);
        expect(a.sent).toEqual(['{"t":"joined"}']);
    });

    it("closes a connection that sends anything else", async () => {
        const a = await joined();
        const b = await joined();
        await room.webSocketMessage(a, JSON.stringify({ t: "chat", text: "hi" }));
        expect(a.closed).toEqual({ code: CLOSE.notAllowed[0], reason: CLOSE.notAllowed[1] });
        expect(b.sent).toEqual(['{"t":"joined"}']);
    });

    it("closes a connection after too many messages", async () => {
        const a = await joined();
        const b = await joined();
        for (let i = 0; i < MAX_MESSAGES; i++) await room.webSocketMessage(a, bye);
        expect(a.closed).toBeNull();
        await room.webSocketMessage(a, bye);
        expect(a.closed?.code).toBe(CLOSE.tooMany[0]);
        expect(b.sent.filter((m) => m === bye)).toHaveLength(MAX_MESSAGES);
    });

    it("refuses a third device", async () => {
        await joined();
        await joined();
        const res = await room.fetch();
        expect(res.status).toBe(409);
        expect(state.sockets).toHaveLength(2);
    });

    it("tells the remaining device when one leaves, and clears the alarm when the room empties", async () => {
        const a = await joined();
        const b = await joined();
        a.close(1000, "");
        await room.webSocketClose(a);
        expect(b.sent.at(-1)).toBe('{"t":"left"}');
        expect(state.alarm).not.toBeNull();
        b.close(1000, "");
        await room.webSocketClose(b);
        expect(state.alarm).toBeNull();
    });

    it("closes every connection when the room expires", async () => {
        const a = await joined();
        const b = await joined();
        await room.alarm();
        expect(a.closed?.code).toBe(CLOSE.expired[0]);
        expect(b.closed?.code).toBe(CLOSE.expired[0]);
    });
});
