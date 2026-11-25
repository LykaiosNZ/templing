// The relay in the Workers runtime: real requests through the Worker to the Room Durable Object, real WebSockets,
// storage, alarms, eviction (hibernation) and the rate-limit binding. These pin down the runtime behaviour the unit
// tests' fakes assume.
import { evictDurableObject, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { env, exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { CLOSE, MAX_JOINS } from "../../src/room.js";

const msg = {
    offer: { t: "offer", ufrag: "Xk9a", pwd: "q1w2e3r4t5y6u7i8o9p0a+", setup: "actpass", fp: "ab".repeat(32), cands: [] },
    answer: { t: "answer", ufrag: "Yz8b", pwd: "q1w2e3r4t5y6u7i8o9p0a+", setup: "active", fp: "cd".repeat(32), cands: [] },
    bye: { t: "bye" },
};
const text = (m) => JSON.stringify(m);

const newRoom = () => [...crypto.getRandomValues(new Uint8Array(32))].map((b) => b.toString(16).padStart(2, "0")).join("");
let ipCount = 0;
const newIp = () => `198.51.100.${++ipCount % 250}`;   // a fresh address per join, clear of the 20-a-minute limit
const stubFor = (room) => env.ROOMS.get(env.ROOMS.idFromName(room));

function request(room, { origin = "http://localhost:5075", ip = newIp(), upgrade = true } = {}) {
    const headers = { "CF-Connecting-IP": ip };
    if (origin) headers.Origin = origin;
    if (upgrade) headers.Upgrade = "websocket";
    return exports.default.fetch(new Request(`https://relay.test/room/${room}`, { headers }));
}

/** Joins a room. Returns the response status, and for a 101 the device's end with what it receives. */
async function connect(room, options) {
    const res = await request(room, options);
    if (res.status !== 101) return { status: res.status };
    const ws = res.webSocket;
    const device = { status: 101, ws, got: [], closed: null };
    ws.addEventListener("message", (e) => { device.got.push(e.data); });
    ws.addEventListener("close", (e) => { device.closed = { code: e.code, reason: e.reason }; });
    ws.accept();
    device.send = (m) => ws.send(typeof m === "string" ? m : text(m));
    return device;
}

/** Waits for a condition the relay brings about asynchronously. */
async function until(check, ms = 2000) {
    const end = Date.now() + ms;
    while (!check()) {
        if (Date.now() > end) throw new Error("timed out waiting");
        await new Promise((r) => setTimeout(r, 10));
    }
}
const quiet = () => new Promise((r) => setTimeout(r, 100));   // time for anything (wrongly) forwarded to arrive

const joined = text({ t: "joined" });
const left = text({ t: "left" });

describe("worker", () => {
    it("refuses bad paths, plain requests and other origins", async () => {
        const room = newRoom();
        expect((await exports.default.fetch("https://relay.test/room/xyz")).status).toBe(404);
        expect((await request(room, { upgrade: false })).status).toBe(426);
        expect((await request(room, { origin: null })).status).toBe(403);
        expect((await request(room, { origin: "https://evil.example" })).status).toBe(403);
        expect((await request(room, { origin: "https://lykaiosnz.github.io" })).status).toBe(101);
    });

    it("rate limits joins from one address", async () => {
        const ip = "203.0.113.99";
        const statuses = [];
        for (let i = 0; i < 25; i++) statuses.push((await request(newRoom(), { ip })).status);
        expect(statuses.slice(0, 20).every((s) => s === 101)).toBe(true);
        expect(statuses.slice(20)).toContain(429);
    });
});

describe("room", () => {
    it("lets exactly two of three simultaneous joins in", async () => {
        const room = newRoom();
        const results = await Promise.all([connect(room), connect(room), connect(room)]);
        expect(results.map((r) => r.status).sort()).toEqual([101, 101, 409]);
        const [a, b] = results.filter((r) => r.status === 101);
        await until(() => a.got.length === 1 && b.got.length === 1);
        expect(a.got).toEqual([joined]);
        expect(b.got).toEqual([joined]);
    });

    it("forwards the relay's own copy of a handshake message, to the other device only", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        a.send(JSON.stringify(msg.offer, null, 2));
        await until(() => b.got.length === 2);
        expect(b.got[1]).toBe(text(msg.offer));
        await quiet();
        expect(a.got).toEqual([joined]);
    });

    it("closes a device that sends anything else, and tells the other it left", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        a.send({ t: "chat", text: "hello" });
        await until(() => a.closed && b.got.includes(left));
        expect(a.closed.code).toBe(CLOSE.notAllowed[0]);
        expect(b.got).toEqual([joined, left]);
    });

    it("closes a device that sends a second offer", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        a.send(msg.offer);
        a.send(msg.answer);
        await until(() => a.closed);
        expect(a.closed.code).toBe(CLOSE.tooMany[0]);
        expect(b.got.filter((m) => m.includes("ufrag"))).toHaveLength(1);
    });

    it("keeps each device's message counts across eviction (hibernation)", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        a.send(msg.offer);
        await until(() => b.got.length === 2);
        await evictDurableObject(stubFor(room));   // the object goes; the sockets hibernate
        a.send(msg.bye);                           // still forwarded after waking
        await until(() => b.got.length === 3);
        a.send(msg.answer);                        // a second offer/answer: the count survived
        await until(() => a.closed);
        expect(a.closed.code).toBe(CLOSE.tooMany[0]);
    });

    it("has room for a device again once the other has left", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        a.ws.close(1000, "done");
        await until(() => b.got.includes(left));
        const c = await connect(room);
        expect(c.status).toBe(101);
        await until(() => c.got.includes(joined));
    });

    it("is used up after MAX_JOINS joins", async () => {
        const room = newRoom();
        for (let i = 0; i < MAX_JOINS; i++) {
            const d = await connect(room);
            expect(d.status).toBe(101);
            d.ws.close(1000, "");
        }
        expect((await connect(room)).status).toBe(410);
    });

    it("expires: closes both devices, wipes its storage and alarm, and a rejoin starts afresh", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        const stub = stubFor(room);
        expect(await runInDurableObject(stub, (_, state) => state.storage.getAlarm())).not.toBeNull();

        expect(await runDurableObjectAlarm(stub)).toBe(true);
        await until(() => a.closed && b.closed);
        expect(a.closed.code).toBe(CLOSE.expired[0]);
        expect(b.closed.code).toBe(CLOSE.expired[0]);
        expect(await runInDurableObject(stub, (_, state) => state.storage.getAlarm())).toBeNull();
        expect(await runInDurableObject(stub, (_, state) => state.storage.get("joins"))).toBeUndefined();

        const c = await connect(room);
        expect(c.status).toBe(101);
        expect(await runInDurableObject(stub, (_, state) => state.storage.get("joins"))).toBe(1);
        expect(await runInDurableObject(stub, (_, state) => state.storage.getAlarm())).not.toBeNull();
        await quiet();
        // nothing from the expired room's devices. Here their closes have finished before c joins; the narrower race
        // (a close still finishing after the rejoin) is covered by the unit test of the same name in room.test.js
        expect(c.got).toEqual([]);
    });

    it("answers keep-alive pings without counting them", async () => {
        const room = newRoom();
        const a = await connect(room);
        const b = await connect(room);
        for (let i = 0; i < 20; i++) a.send("ping");
        await until(() => a.got.filter((m) => m === "pong").length === 20);
        a.send(msg.offer);
        await until(() => b.got.length === 2);
        expect(a.closed).toBeNull();
    });
});
