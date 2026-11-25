import { describe, expect, it } from "vitest";
import { MAX_CANDIDATES, MAX_MESSAGE, isRoomId, originAllowed, validate } from "../src/schema.js";

const hex = "ab".repeat(32);
const offer = {
    t: "offer",
    ufrag: "Xk9a",
    pwd: "q1w2e3r4t5y6u7i8o9p0a+/b",
    setup: "actpass",
    cands: [
        { addr: "3f2b8c1d-1234-4abc-9def-0123456789ab.local", port: 54321, type: "host" },
        { addr: "203.0.113.7", port: 61000, type: "srflx" },
        { addr: "2001:db8::1", port: 9, type: "host" },
    ],
};
const ok = (m) => expect(validate(JSON.stringify(m))).toBe(JSON.stringify(m));
const no = (m) => expect(validate(typeof m === "string" ? m : JSON.stringify(m))).toBeNull();

describe("validate", () => {
    it("passes each kind of handshake message", () => {
        ok({ t: "pair", fp: hex, mac: hex });
        ok(offer);
        ok({ ...offer, t: "answer", setup: "active" });
        ok({ t: "ice", cand: offer.cands[1] });
        ok({ t: "bye" });
    });

    it("forwards its own copy, not the client's text", () => {
        const spaced = JSON.stringify({ t: "bye" }, null, 4);
        expect(validate(spaced)).toBe('{"t":"bye"}');
        const reordered = JSON.stringify({ cand: { type: "host", port: 1, addr: "10.0.0.2" }, t: "ice" });
        expect(validate(reordered)).toBe('{"t":"ice","cand":{"addr":"10.0.0.2","port":1,"type":"host"}}');
    });

    it("rejects anything that isn't a known message", () => {
        no("not json");
        no("[]");
        no("null");
        no({ t: "chat", text: "hello" });
        no({ t: "joined" });               // only the relay says this
        no({});
    });

    it("rejects extra or missing fields", () => {
        no({ t: "bye", note: "x" });
        no({ t: "pair", fp: hex });
        no({ t: "pair", fp: hex, mac: hex, name: "Laptop" });
        no({ ...offer, extra: 1 });
        no({ t: "ice", cand: { ...offer.cands[0], foundation: "1" } });
    });

    it("rejects fields that could carry free text", () => {
        no({ t: "pair", fp: "AB".repeat(32), mac: hex });       // uppercase hex
        no({ t: "pair", fp: hex + "00", mac: hex });
        no({ ...offer, ufrag: "has space" });
        no({ ...offer, ufrag: "x".repeat(33) });
        no({ ...offer, pwd: "short" });
        no({ ...offer, setup: "holdconn" });
        no({ t: "ice", cand: { addr: "example.com", port: 1, type: "host" } });
        no({ t: "ice", cand: { addr: "not-a-uuid.local", port: 1, type: "host" } });
        no({ t: "ice", cand: { addr: "256.1.1.1", port: 1, type: "host" } });
        no({ t: "ice", cand: { addr: "10.0.0.1", port: 0, type: "host" } });
        no({ t: "ice", cand: { addr: "10.0.0.1", port: 70000, type: "host" } });
        no({ t: "ice", cand: { addr: "10.0.0.1", port: 1.5, type: "host" } });
        no({ t: "ice", cand: { addr: "10.0.0.1", port: 1, type: "relay" } });   // no TURN: nothing is relayed
    });

    it("limits candidates and size", () => {
        const many = Array.from({ length: MAX_CANDIDATES + 1 }, (_, i) => ({ addr: `10.0.0.${i + 1}`, port: 1, type: "host" }));
        no({ ...offer, cands: many });
        ok({ ...offer, cands: many.slice(0, MAX_CANDIDATES) });
        expect(validate(" ".repeat(MAX_MESSAGE - 11) + '{"t":"bye"}')).not.toBeNull();
        expect(validate(" ".repeat(MAX_MESSAGE - 10) + '{"t":"bye"}')).toBeNull();
    });

    it("rejects binary messages", () => {
        expect(validate(new ArrayBuffer(8))).toBeNull();
    });
});

describe("isRoomId", () => {
    it("accepts 64 lowercase hex characters only", () => {
        expect(isRoomId(hex)).toBe(true);
        expect(isRoomId(hex.toUpperCase())).toBe(false);
        expect(isRoomId(hex.slice(1))).toBe(false);
        expect(isRoomId("../" + hex.slice(3))).toBe(false);
    });
});

describe("originAllowed", () => {
    const list = "https://lykaiosnz.github.io, https://example.test";
    it("allows the configured origins and localhost", () => {
        expect(originAllowed("https://lykaiosnz.github.io", list)).toBe(true);
        expect(originAllowed("https://example.test", list)).toBe(true);
        expect(originAllowed("http://localhost:5075", list)).toBe(true);
        expect(originAllowed("https://127.0.0.1:7212", list)).toBe(true);
    });
    it("refuses everything else", () => {
        expect(originAllowed(null, list)).toBe(false);
        expect(originAllowed("https://evil.example", list)).toBe(false);
        expect(originAllowed("https://lykaiosnz.github.io.evil.example", list)).toBe(false);
        expect(originAllowed("http://localhost.evil.example", list)).toBe(false);
        expect(originAllowed("http://lykaiosnz.github.io", list)).toBe(false);
    });
});
