// Bundles entry.js into one minified script at src/Templing/wwwroot/lib/echarts/echarts.min.js.
import { build } from 'esbuild';
import { readFileSync, statSync } from 'node:fs';
import { gzipSync } from 'node:zlib';

const out = new URL('../../src/Templing/wwwroot/lib/echarts/echarts.min.js', import.meta.url);
const { version } = JSON.parse(readFileSync(new URL('./node_modules/echarts/package.json', import.meta.url)));

await build({
    entryPoints: [new URL('./entry.js', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')],
    bundle: true, minify: true, format: 'iife', target: 'es2020', legalComments: 'none',
    outfile: out.pathname.replace(/^\/([A-Za-z]:)/, '$1'),
    banner: { js: `/* Apache ECharts ${version}, trimmed build (tools/echarts). Apache-2.0: see LICENSE and NOTICE in this folder. */` },
});

const size = statSync(out).size, gz = gzipSync(readFileSync(out)).length;
console.log(`Wrote echarts.min.js (ECharts ${version}): ${(size / 1024).toFixed(0)} KB, ${(gz / 1024).toFixed(0)} KB gzipped`);
