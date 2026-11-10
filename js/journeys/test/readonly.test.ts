// A read-only run refuses every write before it leaves the browser, and fails the step that made it. Skips when
// Chrome is missing; CDP_BROWSER_REQUIRED=1 makes that a failure.

import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { after, before, test } from 'node:test';
import type { Browser } from 'playwright-core';
import { parseJourney, runJourney } from '../src/index.ts';
import { launchChrome } from './site.ts';

const page = `<!doctype html><title>Writes</title><link rel="icon" href="data:,">
<button onclick="fetch('/write', {method: 'POST', body: 'x'})">Save with fetch</button>
<button onclick="fetch('/telemetry', {method: 'POST', body: 'x'})">Send telemetry</button>
<form method="post" action="/form"><button>Save with form</button></form>
<a href="/next">Next page</a>`;

let browser: Browser | undefined;
let skip: string | false = false;
let origin = '';
let writes = 0;
let stop = () => {};

before(async () => {
  const server = createServer((req, res) => {
    if (req.method !== 'GET') writes++;
    const next = req.url === '/next';
    res.writeHead(200, { 'content-type': 'text/html' }).end(next ? '<title>Next</title><h1>Next</h1>' : req.method === 'GET' ? page : '<title>Saved</title>');
  });
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  stop = () => server.close();
  ({ browser, skip } = await launchChrome());
});

after(async () => {
  await browser?.close();
  stop();
});

async function run(text: string, options: { readOnly?: boolean; writableUrls?: string[] } = { readOnly: true }) {
  writes = 0;
  const p = await (await browser!.newContext()).newPage();
  const result = await runJourney(p, parseJourney(text, 'read-only.journey'), { baseUrl: origin + '/', actionTimeout: 3000, warn: () => {}, ...options });
  await new Promise((r) => setTimeout(r, 100));
  return result;
}

test('a fetch POST fails the step and never reaches the server', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run('open /\nfind role button click --name "Save with fetch"\nwait 300\nget title');
  assert.ok(result.failure, 'the run should fail');
  assert.match(result.failure.message, /^Blocked POST http:\/\/127\.0\.0\.1:\d+\/write: this run is read-only/);
  assert.equal(writes, 0);
});

test('a form POST fails the step and never reaches the server', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run('open /\nfind role button click --name "Save with form"\nget title');
  assert.match(result.failure?.message ?? '', /^Blocked POST .*\/form: this run is read-only/);
  assert.equal(writes, 0);
});

test('reads still work', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run('open /\nfind role link click --name "Next page"\nwait --url **/next\nget title');
  assert.equal(result.failure, undefined);
  assert.equal(writes, 0);
});

test('a writable URL lets its write through, and no other', async (t) => {
  if (skip) return t.skip(skip);
  const ok = await run('open /\nfind role button click --name "Send telemetry"\nwait 300\nget title', { readOnly: true, writableUrls: ['**/telemetry'] });
  assert.equal(ok.failure, undefined);
  assert.equal(writes, 1);
  const refused = await run('open /\nfind role button click --name "Save with fetch"\nwait 300\nget title', { readOnly: true, writableUrls: ['**/telemetry'] });
  assert.ok(refused.failure);
  assert.equal(writes, 0);
});

test('without readOnly the write goes through', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run('open /\nfind role button click --name "Save with fetch"\nwait 300\nget title', {});
  assert.equal(result.failure, undefined);
  assert.equal(writes, 1);
});
