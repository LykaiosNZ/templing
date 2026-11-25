# Templing relay

A Cloudflare Worker + Durable Object that lets two of a person's devices swap a WebRTC handshake, so they can connect **directly** and send a backup between them. It's the only server Templing uses, and it never carries app data.

## What it does, and what it refuses

```
GET wss://<relay>/room/<64 hex>     WebSocket; one Durable Object per room
```

- **A room is two devices, used once.** A third device is refused, and a room accepts 6 joins in all. It closes 10 minutes after its first join, even if it empties and fills again in between, and is then forgotten. So devices work out a fresh room id (a hash of their pairing secret and the time window) each time. When the other device joins or leaves, the relay sends `{t:"joined"}` / `{t:"left"}`.
- **Only handshake messages go through.** Each message is parsed and checked against fixed shapes (`src/schema.js`): fingerprints, ICE username/password (the lengths browsers use), setup role, and candidates (an IPv4 or IPv6 address, or a browser's `.local` name, with a port and type). No free text, no unknown fields, at most 1 KB a message. The relay forwards its own re-serialised copy, with IPv6 addresses in their canonical spelling, never the client's text. Anything else closes the connection.
- **One handshake per connection.** A connection may send 1 `pair`, 1 `offer` or `answer`, 8 `ice` and 1 `bye`. Anything more closes it.
- **No TURN, no data.** `relay` candidates are refused, and the app's data goes over the WebRTC data channel between the devices. If they can't connect directly, the transfer fails and the app suggests a backup file.
- **Only Templing's pages.** The `Origin` must be in `ALLOWED_ORIGINS` (`wrangler.jsonc`), or localhost on any port for development. Browsers always send the real `Origin`, so other websites can't use the relay. Non-browser clients can fake it, but can only swap handshakes.
- **Rate limited:** 20 room joins a minute per IP address, or per /64 for IPv6 (`ratelimits` in `wrangler.jsonc`). Without the binding, the relay refuses every join rather than running unlimited.
- **Nothing stored, no message logs.** Rooms hold only their join count, expiry alarm and each socket's message counts, and are wiped when they expire. Workers Logs (observability) are off. Cloudflare still keeps request analytics, and the account owner can watch live requests with `wrangler tail`. Room ids are hashes the devices work out, so the relay never sees pairing secrets.

The relay does see devices' IP addresses, the times they connect, and their handshake fields (addresses and ports). App data never passes through it: WebRTC encrypts the data channel end to end, and paired devices check each other's certificate fingerprints, so the relay can't read it or put itself in the middle.

### What's left, honestly

- **A few hundred bytes per connection.** Handshake fields necessarily carry bits the sender chooses: a fingerprint is 32 arbitrary bytes, and an address or port is whatever the sender says. So someone with their own client could move about 500 bytes per connection (64 in `pair`, the rest in up to 16 addresses). With joins limited per IP, that's a few KB a minute from one address. That's far too slow to be worth using, but it isn't zero, and no handshake relay can make it zero.
- **The free plan can be exhausted.** Workers Free allows 100,000 Durable Object requests a day, and going over fails rather than bills. Someone with many addresses could use that up and stop the relay for everyone until the next day. Cloudflare's rate limiter is also per location and approximate. If that happens, add a daily cap (a counter Durable Object) or move to the paid plan. Either way, the app falls back to backup files.

## Develop

Node 22.12 or newer (Wrangler needs it).

```bash
cd relay
npm install
npm test          # schema, room and worker logic (Vitest, fake Durable Object state)
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
