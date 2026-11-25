# Templing relay

A Cloudflare Worker + Durable Object that lets two of a person's devices swap a WebRTC handshake, so they can connect **directly** and send a backup between them. It's the only server Templing uses, and it never carries app data.

## What it does, and what it refuses

```
GET wss://<relay>/room/<64 hex>     WebSocket; one Durable Object per room
```

- **A room is two devices.** A third is refused. The room closes 10 minutes after the first device joins. When the other device joins or leaves, the relay sends `{t:"joined"}` / `{t:"left"}`.
- **Only handshake messages go through.** Each message is parsed and checked against fixed shapes (`src/schema.js`): fingerprints, ICE username/password, setup role, and up to 8 candidates (an IP or a browser's `.local` name, port, type). No free text, no unknown fields, at most 1 KB a message and 32 messages a connection. The relay forwards its own re-serialised copy, never the client's text. Anything else closes the connection.
- **No TURN, no data.** `relay` candidates are refused, and the app's data goes over the WebRTC data channel between the devices. If they can't connect directly, the transfer fails and the app suggests a backup file.
- **Only Templing's pages.** The `Origin` must be in `ALLOWED_ORIGINS` (`wrangler.jsonc`), or localhost on any port for development. Browsers always send the real `Origin`, so other websites can't use the relay. Non-browser clients can fake it, but can only swap handshakes.
- **Rate limited:** 20 room joins a minute per IP address (`ratelimits` in `wrangler.jsonc`).
- **Nothing stored, nothing logged.** Rooms hold only each socket's message count and the expiry alarm. Observability (Workers logs) is off. Room ids are hashes the devices work out, so the relay never sees pairing secrets.

The relay does see devices' IP addresses, the times they connect, and their handshake fields (addresses and ports). App data never passes through it: WebRTC encrypts the data channel end to end, and paired devices check each other's certificate fingerprints, so the relay can't read it or put itself in the middle.

## Develop

Node 22.12 or newer (Wrangler needs it).

```bash
cd relay
npm install
npm test          # schema + room logic (Vitest, fake Durable Object state)
npm run dev       # wrangler dev: ws://localhost:8787/room/<64 hex>
```

## Deploy

Once, in Cloudflare (the Workers Free plan is enough: SQLite-backed Durable Objects and WebSocket hibernation are included):

1. **Deploy from your machine** the first time, so you can see it work: `npx wrangler login`, then `npm run deploy`. It prints the URL, `https://templing-relay.<your subdomain>.workers.dev`.
2. **Let GitHub deploy after that:** create an API token in the Cloudflare dashboard (My Profile → API Tokens → *Edit Cloudflare Workers* template), and add repository secrets `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` (the account id is on the Workers & Pages overview). `.github/workflows/relay.yml` then deploys whenever `relay/` changes on `main`, or when run by hand.

To try it from a browser console on an allowed origin (e.g. the dev server at `http://localhost:5075`), open two tabs:

```js
const room = "ab".repeat(32);
const ws = new WebSocket(`wss://templing-relay.<subdomain>.workers.dev/room/${room}`);
ws.onmessage = (e) => console.log("got", e.data);
ws.onclose = (e) => console.log("closed", e.code, e.reason);
// in the second tab, after "joined":
ws.send(JSON.stringify({ t: "bye" }));                   // arrives in the first tab
ws.send(JSON.stringify({ t: "chat", text: "hello" }));   // closes with 4003 Not a handshake message
```
