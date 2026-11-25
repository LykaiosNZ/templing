import { beforeEach, describe, expect, it } from "vitest";
import { CLOSE, LIFETIME_MS, MAX_JOINS, Room } from "../src/room.js";
import { BUDGET } from "../src/schema.js";

// Stand-ins for the Durable Object state and hibernatable sockets: just enough of the API Room uses. As in the
// runtime, getWebSockets() still returns a socket while it's closing, and sending to a closed socket throws.
class FakeSocket {
    constructor() { this.sent = []; this.closed = null; this.attachment = null; this.readyState = 1; }
    send(m) {
        if (this.readyState !== 1) throw new Error("send after close");
        this.sent.push(m);
    }
    close(code, reason) {
        if (this.readyState === 3) throw new Error("already closed");
        this.closed = { code, reason };
        this.readyState = 2;   // closing until the other end answers
    }
    serializeAttachment(a) { this.attachment = structuredClone(a); }
    deserializeAttachment() { return structuredClone(this.attachment); }
}

class FakeState {
    constructor() {
        this.sockets = [];
        this.alarm = null;
        const data = new Map();
        this.data = data;
        this.storage = {
            get: async (k) => data.get(k),
            put: async (k, v) => { data.set(k, v); },
            setAlarm: async (t) => { this.alarm = t; },
            deleteAll: async () => { data.clear(); this.alarm = null; },
        };
    }
    getWebSockets() { return this.sockets.filter((s) => s.readyState !== 3); }
    acceptWebSocket(ws) { this.sockets.push(ws); }
    setWebSocketAutoResponse() {}
}

const msg = {
    pair: { t: "pair", fp: "ab".repeat(32), mac: "cd".repeat(32) },
    offer: { t: "offer", ufrag: "Xk9a", pwd: "q1w2e3r4t5y6u7i8o9p0a+", setup: "actpass", cands: [] },
    answer: { t: "answer", ufrag: "Yz8b", pwd: "q1w2e3r4t5y6u7i8o9p0a+", setup: "active", cands: [] },
    ice: { t: "ice", cand: { addr: "10.0.0.2", port: 5000, type: "host" } },
    bye: { t: "bye" },
};
const text = (m) => JSON.stringify(m);
const joinedMsg = text({ t: "joined" });

let state, room;
beforeEach(() => { state = new FakeState(); room = new Room(state, {}); });

/** A device joining: admitted, then its socket accepted (what fetch() does around WebSocketPair). */
async function joined() {
    const refused = await room.admit();
    if (refused) return refused;
    const ws = new FakeSocket();
    room.join(ws);
    return ws;
}

/** The far end finishing a close, so the socket is gone. */
async function hangUp(ws) {
    if (ws.readyState === 1) ws.close(1000, "");
    ws.readyState = 3;
    await room.webSocketClose(ws);
}

describe("Room", () => {
    it("tells both devices when the second one joins, and sets the expiry once", async () => {
        const before = Date.now();
        const a = await joined();
        expect(state.alarm).toBeGreaterThanOrEqual(before + LIFETIME_MS);
        expect(a.sent).toEqual([]);
        const alarm = state.alarm;
        const b = await joined();
        expect(state.alarm).toBe(alarm);
        expect(a.sent).toEqual([joinedMsg]);
        expect(b.sent).toEqual([joinedMsg]);
    });

    it("forwards a checked message to the other device only", async () => {
        const a = await joined();
        const b = await joined();
        await room.webSocketMessage(a, JSON.stringify(msg.bye, null, 2));
        expect(b.sent.at(-1)).toBe(text(msg.bye));
        expect(a.sent).toEqual([joinedMsg]);
    });

    it("closes a connection that sends anything else", async () => {
        const a = await joined();
        const b = await joined();
        await room.webSocketMessage(a, text({ t: "chat", text: "hi" }));
        expect(a.closed).toEqual({ code: CLOSE.notAllowed[0], reason: CLOSE.notAllowed[1] });
        expect(b.sent).toEqual([joinedMsg]);
    });

    it("lets a connection send one handshake's worth of each message", async () => {
        const a = await joined();
        const b = await joined();
        await room.webSocketMessage(a, text(msg.pair));
        await room.webSocketMessage(a, text(msg.offer));
        for (let i = 0; i < BUDGET.ice; i++) await room.webSocketMessage(a, text(msg.ice));
        await room.webSocketMessage(a, text(msg.bye));
        expect(a.closed).toBeNull();
        expect(b.sent).toHaveLength(1 + 1 + 1 + BUDGET.ice + 1);
    });

    for (const [extra, after] of [["pair", ["pair"]], ["offer", ["offer"]], ["answer", ["offer"]], ["bye", ["bye"]]]) {
        it(`closes a connection that sends another ${extra} after ${after.join(", ")}`, async () => {
            const a = await joined();
            const b = await joined();
            for (const m of after) await room.webSocketMessage(a, text(msg[m]));
            await room.webSocketMessage(a, text(msg[extra]));
            expect(a.closed?.code).toBe(CLOSE.tooMany[0]);
            expect(b.sent).toHaveLength(1 + after.length);
        });
    }

    it("closes a connection after too many candidates", async () => {
        const a = await joined();
        await joined();
        for (let i = 0; i < BUDGET.ice; i++) await room.webSocketMessage(a, text(msg.ice));
        expect(a.closed).toBeNull();
        await room.webSocketMessage(a, text(msg.ice));
        expect(a.closed?.code).toBe(CLOSE.tooMany[0]);
    });

    it("refuses a third device", async () => {
        await joined();
        await joined();
        const res = await joined();
        expect(res.status).toBe(409);
        expect(state.sockets).toHaveLength(2);
    });

    it("doesn't count a closing socket as a device, so the other can rejoin straight away", async () => {
        const a = await joined();
        await joined();
        a.close(1000, "");   // closing, still listed by getWebSockets()
        expect(state.getWebSockets()).toContain(a);
        const c = await joined();
        expect(c).toBeInstanceOf(FakeSocket);
    });

    it("is used up after MAX_JOINS joins, however often devices leave", async () => {
        for (let i = 0; i < MAX_JOINS; i++) await hangUp(await joined());
        const res = await joined();
        expect(res.status).toBe(410);
    });

    it("keeps its expiry when it empties and fills again", async () => {
        const a = await joined();
        const alarm = state.alarm;
        await hangUp(a);
        expect(state.alarm).toBe(alarm);
        await joined();
        expect(state.alarm).toBe(alarm);
    });

    it("starts each connection's message counts afresh, but a reconnect uses up a join", async () => {
        await joined();
        const a = await joined();
        await room.webSocketMessage(a, text(msg.offer));
        await hangUp(a);
        const again = await joined();
        await room.webSocketMessage(again, text(msg.offer));
        expect(again.closed).toBeNull();
        expect(state.data.get("joins")).toBe(3);
    });

    it("tells the remaining device once when one leaves", async () => {
        const a = await joined();
        const b = await joined();
        await room.webSocketError(a);
        a.readyState = 3;
        await room.webSocketClose(a);
        expect(b.sent.filter((m) => m === text({ t: "left" }))).toHaveLength(1);
    });

    it("doesn't throw when both devices go at once", async () => {
        const a = await joined();
        const b = await joined();
        a.readyState = 3;
        b.readyState = 3;
        await expect(room.webSocketClose(a)).resolves.toBeUndefined();
        await expect(room.webSocketClose(b)).resolves.toBeUndefined();
    });

    it("closes every connection and forgets the room when it expires", async () => {
        const a = await joined();
        const b = await joined();
        await room.alarm();
        expect(a.closed?.code).toBe(CLOSE.expired[0]);
        expect(b.closed?.code).toBe(CLOSE.expired[0]);
        expect(state.data.size).toBe(0);
        // the far ends answering the close: nothing is sent to sockets already closing
        await expect(hangUp(a)).resolves.toBeUndefined();
        await expect(hangUp(b)).resolves.toBeUndefined();
    });
});
