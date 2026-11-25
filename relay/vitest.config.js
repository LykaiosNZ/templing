// Two layers of tests:
//   unit     test/*.test.js           plain Node, fakes for the Durable Object state; fast
//   runtime  test/runtime/*.test.js   inside workerd (the Workers runtime, via Miniflare) with the real Durable
//                                     Object, WebSockets, storage, alarms and rate limiter, from wrangler.jsonc
import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export default defineConfig({
    test: {
        projects: [
            {
                test: { name: "unit", include: ["test/*.test.js"], environment: "node" },
            },
            {
                plugins: [cloudflareTest({ wrangler: { configPath: "./wrangler.jsonc" } })],
                test: { name: "runtime", include: ["test/runtime/*.test.js"] },
            },
        ],
    },
});
