// `expect request` and `expect same-document` on Playwright: the same journeys as tests/Nullean.Cdp.Tests/Live/ExpectRequestTests.cs,
// with the same messages. Skips when Chrome is missing; CDP_BROWSER_REQUIRED=1 makes that a failure.

import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { after, before, test } from 'node:test';
import type { Browser } from 'playwright-core';
import { parseJourney, runJourney } from '../src/index.ts';
import { launchChrome } from './site.ts';

let browser: Browser | undefined;
let skip: string | false = false;
let origin = '';
let stop = () => {};

const html = (body: string) => `<!doctype html><title>T</title><link rel="icon" href="data:,">${body}`;

before(async () => {
  const server = createServer((req, res) => {
    const path = new URL(req.url ?? '/', 'http://x').pathname;
    const page = (body: string) => res.writeHead(200, { 'content-type': 'text/html' }).end(html(body));
    if (path === '/telemetry')
      return page(
        `<h1>Telemetry</h1><script>const post = (p, b) => fetch(p, { method: 'POST', body: b });` +
          `post('/collect/l', JSON.stringify({ resourceLogs: [{ resource: { attributes: [{ key: 'service.name', value: { stringValue: 'docs-frontend' } }] }, scopeLogs: [{ logRecords: [{ eventName: 'browser.navigation_timing' }] }] }] }));` +
          `post('/collect/big', 'x'.repeat(100000)); post('/collect/deny', '{}'); post('/collect/slow', '{}');</script>`,
      );
    if (path === '/spa')
      return page(`<h1>Spa</h1><button id="push" onclick="history.pushState(null, '', '/spa/inner')">Push</button> <a id="full" href="/next">Full</a>`);
    if (path.startsWith('/collect/')) {
      req.resume();
      req.on('end', () => setTimeout(() => res.writeHead(path === '/collect/deny' ? 403 : 200).end(), path === '/collect/slow' ? 1500 : 0));
      return;
    }
    return page('<h1>Next</h1>');
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

async function run(text: string) {
  const page = await (await browser!.newContext()).newPage();
  try {
    return await runJourney(page, parseJourney(text, 'x.journey'), { baseUrl: origin + '/', warn: () => {} });
  } finally {
    await page.context().close();
  }
}

test('passing request assertions record nothing', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run(`open /telemetry
expect request "**/collect/l" --method POST --status 2xx --type Fetch
expect request "**/collect/l" --body-contains '"docs-frontend"'
expect request "**/collect/l" --body-contains browser.navigation_timing
expect request "**/collect/l" --body-regex '"service\\.name".{0,30}docs-frontend'
expect --not request "**/collect/l" --body-contains browser.resource_timing
expect --not request "**/collect/l" --larger-than 1kb
expect request "**/collect/big" --larger-than 64kb
expect request "**/collect/deny" --status 403 --failed
expect --timeout 5000 request "**/collect/slow" --status 200
expect --not request "**/collect/l" --failed
get title`);
  assert.equal(result.failure, undefined, JSON.stringify(result.failure));
  assert.deepEqual(result.checkpoints.map((c) => c.command), ['get title']);
});

test('a body filter that does not hold fails with the requests the URL matched', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run(`open /telemetry
expect --timeout 5000 request "**/collect/l" --status 2xx
expect --timeout 300 request "**/collect/l" --body-contains missing-text`);
  assert.equal(result.failure?.line, 3);
  const message = result.failure!.message;
  assert.ok(
    message.startsWith('Timed out after 0.3s waiting for the requests to include a request matching "**/collect/l" --body-contains "missing-text": received 1 with that URL, none matching the other filters'),
    message,
  );
  assert.match(message, /POST http:\/\/127\.0\.0\.1:\d+\/collect\/l \(Fetch\) 200/);
  assert.ok(!message.includes('docs-frontend'), 'the body is never printed');
});

test('a negated assertion names what it found, and waits for requests in flight', async (t) => {
  if (skip) return t.skip(skip);
  const found = await run(`open /telemetry
expect --timeout 5000 request "**/collect/slow"
expect --timeout 300 --not request "**/collect/*" --failed`);
  assert.equal(found.failure?.line, 3);
  assert.match(found.failure!.message, /include no request matching "\*\*\/collect\/\*" --failed: received found POST http:\/\/127\.0\.0\.1:\d+\/collect\/deny \(Fetch\) 403/);

  const inFlight = await run(`open /telemetry
expect --timeout 5000 request "**/collect/l" --status 2xx
expect --timeout 300 --not request "**/collect/slow" --failed`);
  assert.match(inFlight.failure!.message, /received 1 still in flight: POST http:\/\/127\.0\.0\.1:\d+\/collect\/slow/);
});

test('a size failure shows the body size, not the body', async (t) => {
  if (skip) return t.skip(skip);
  const result = await run(`open /telemetry
expect --timeout 5000 request "**/collect/big" --status 2xx
expect --timeout 300 request "**/collect/big" --larger-than 200kb`);
  assert.match(result.failure!.message, /--larger-than 204800 bytes.*\[100000 bytes\]/);
});

test('same-document tells a pushState from a full navigation', async (t) => {
  if (skip) return t.skip(skip);
  const swap = await run(`open /spa
expect same-document
click #push
expect url "**/spa/inner"
expect same-document
expect --timeout 300 --not same-document`);
  assert.equal(swap.failure?.line, 6);
  assert.equal(swap.failure?.message, 'Timed out after 0.3s waiting for the page to have been replaced by a new document since the last action: received the same document.');

  const full = await run(`open /spa
click #full
expect --not same-document
expect --timeout 300 same-document`);
  assert.equal(full.failure?.line, 4);
  assert.match(full.failure!.message, /received 1 new document\(s\) since the last action/);
});
