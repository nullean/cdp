// A run's defaults (viewport, user agent, headers) are in place before the first step, a journey's own `set` lines
// build on them, and they show on both sides: what the page sees and what the server receives. The same cases as
// RunDefaultsTests in the .NET tests. Skips when Chrome is missing; CDP_BROWSER_REQUIRED=1 makes that a failure.

import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { after, before, test } from 'node:test';
import type { Browser } from 'playwright-core';
import { parseJourney, runJourney } from '../src/index.ts';
import type { RunOptions } from '../src/index.ts';
import { launchChrome } from './site.ts';

let browser: Browser | undefined;
let skip: string | false = false;
let origin = '';
let stop = () => {};

const header = (v: string | string[] | undefined) => String(v ?? 'none').replace(/&/g, '&amp;').replace(/</g, '&lt;');

before(async () => {
  const server = createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'text/html' }).end(
      '<!doctype html><title>Echo</title><link rel="icon" href="data:,">' +
        `<p id="header">${header(req.headers['x-docs-synthetic-monitor'])}</p><p id="journey">${header(req.headers['x-journey'])}</p><p id="request-ua">${header(req.headers['user-agent'])}</p>` +
        '<p id="ua"></p><p id="width"></p>' +
        "<script>document.getElementById('ua').textContent = navigator.userAgent; document.getElementById('width').textContent = String(innerWidth);</script>",
    );
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

const reads = 'open /echo\nget text #ua\nget text #width\nget text #header\nget text #journey\nget text #request-ua\n';
const monitor: Partial<RunOptions> = { headers: { 'X-Docs-Synthetic-Monitor': 'true' }, userAgent: 'Elastic/Synthetics', viewport: { width: 1111, height: 700 } };

// What the page saw (user agent, width) and what the server saw (header, journey header, user agent).
async function run(text: string, defaults: Partial<RunOptions>) {
  const page = await (await browser!.newContext()).newPage();
  const result = await runJourney(page, parseJourney(text, 'defaults.journey'), { baseUrl: origin + '/', actionTimeout: 3000, warn: () => {}, ...defaults });
  assert.equal(result.failure, undefined, JSON.stringify(result.failure));
  return result.checkpoints.filter((c) => c.command.startsWith('get text')).map((c) => c.output);
}

test('the run starts with its viewport, user agent and headers', async (t) => {
  if (skip) return t.skip(skip);
  assert.deepEqual(await run(reads, monitor), ['Elastic/Synthetics', '1111', 'true', 'none', 'Elastic/Synthetics']);
});

test('without defaults nothing changes', async (t) => {
  if (skip) return t.skip(skip);
  const seen = await run(reads, {});
  assert.match(seen[0], /Chrome/);
  assert.doesNotMatch(seen[0], /Elastic\/Synthetics/);
  assert.equal(seen[2], 'none');
  assert.notEqual(seen[1], '1111');
});

test("a journey's headers add to the run's and win when they name the same", async (t) => {
  if (skip) return t.skip(skip);
  assert.deepEqual(await run('set headers \'{"x-journey": "1", "X-Docs-Synthetic-Monitor": "override"}\'\n' + reads, monitor), ['Elastic/Synthetics', '1111', 'override', '1', 'Elastic/Synthetics']);
  assert.deepEqual((await run('set headers \'{"x-journey": "1"}\'\n' + reads, monitor)).slice(2), ['true', '1', 'Elastic/Synthetics']);
});

test("a journey's user agent replaces the run's and the header stays", async (t) => {
  if (skip) return t.skip(skip);
  assert.deepEqual(await run('set useragent Other/1.0\n' + reads, monitor), ['Other/1.0', '1111', 'true', 'none', 'Other/1.0']);
});

test("a journey's viewport replaces the run's", async (t) => {
  if (skip) return t.skip(skip);
  assert.equal((await run('set viewport 800 600\n' + reads, monitor))[1], '800');
});

test("a device keeps the run's user agent", async (t) => {
  if (skip) return t.skip(skip);
  assert.equal((await run("set device 'iPhone 14'\n" + reads, monitor))[0], 'Elastic/Synthetics');
});
