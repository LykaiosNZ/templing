import { describe, expect, it } from "vitest";
import worker, { limitKey } from "../src/index.js";

const room = "ab".repeat(32);
const ws = { Upgrade: "websocket" };
const origin = { ...ws, Origin: "https://lykaiosnz.github.io" };

function env({ allow = true, limiter = true } = {}) {
    const calls = { keys: [], rooms: [] };
    return {
        calls,
        ALLOWED_ORIGINS: "https://lykaiosnz.github.io",
        JOIN_LIMIT: limiter ? { limit: async ({ key }) => { calls.keys.push(key); return { success: allow }; } } : undefined,
        ROOMS: {
            idFromName: (n) => { calls.rooms.push(n); return n; },
            get: () => ({ fetch: async () => new Response("room", { status: 200 }) }),
        },
    };
}

const get = (path, headers, e = env()) =>
    worker.fetch(new Request(`https://relay.example${path}`, { headers }), e);

describe("worker", () => {
    it("refuses other paths and bad room ids", async () => {
        expect((await get("/", origin)).status).toBe(404);
        expect((await get("/room/xyz", origin)).status).toBe(404);
        expect((await get(`/room/${room}/x`, origin)).status).toBe(404);
        expect((await get(`/room/${room.toUpperCase()}`, origin)).status).toBe(404);
    });

    it("wants a WebSocket from an allowed origin", async () => {
        expect((await get(`/room/${room}`, { Origin: origin.Origin })).status).toBe(426);
        expect((await get(`/room/${room}`, ws)).status).toBe(403);
        expect((await get(`/room/${room}`, { ...ws, Origin: "https://evil.example" })).status).toBe(403);
    });

    it("rate limits joins by address, and refuses to run without its limiter", async () => {
        const e = env();
        const res = await get(`/room/${room}`, { ...origin, "CF-Connecting-IP": "2001:db8:1:2:3:4:5:6" }, e);
        expect(res.status).toBe(200);
        expect(e.calls.keys).toEqual(["2001:db8:1:2::/64"]);
        expect(e.calls.rooms).toEqual([room]);
        expect((await get(`/room/${room}`, origin, env({ allow: false }))).status).toBe(429);
        expect((await get(`/room/${room}`, origin, env({ limiter: false }))).status).toBe(503);
    });
});

describe("limitKey", () => {
    it("keeps IPv4 as it is", () => {
        expect(limitKey("203.0.113.7")).toBe("203.0.113.7");
        expect(limitKey(null)).toBe("unknown");
    });
    it("groups IPv6 by its /64, however it's written", () => {
        const key = "2001:db8:0:1::/64";
        expect(limitKey("2001:db8:0:1:aaaa:bbbb:cccc:dddd")).toBe(key);
        expect(limitKey("2001:0db8:0000:0001::1")).toBe(key);
        expect(limitKey("2001:DB8:0:1::ffff")).toBe(key);
        expect(limitKey("2001:db8::1")).toBe("2001:db8:0:0::/64");
        expect(limitKey("::1")).toBe("0:0:0:0::/64");
    });
});
