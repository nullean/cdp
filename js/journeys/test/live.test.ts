// Runs the corpus journey in a real Chrome through Playwright and compares what it reads with what Nullean.Cdp read
// (test/reference/everything.json). Skips when Chrome is missing; CDP_BROWSER_REQUIRED=1 makes that a failure.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { after, before, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import type { Browser } from 'playwright-core';
import { parseJourney, runJourney } from '../src/index.ts';
import { launchChrome, siteFolder as site, startSite } from './site.ts';

const here = dirname(fileURLToPath(import.meta.url));

let browser: Browser | undefined;
let origin = '';
let skip: string | false = false;
let stopSite = () => {};

before(async () => {
  const served = await startSite();
  origin = served.origin;
  stopSite = served.stop;
  ({ browser, skip } = await launchChrome());
});

after(async () => {
  await browser?.close();
  stopSite();
});

const read = (name: string) => readFileSync(join(site, 'journeys', name), 'utf8');

test('everything.journey reads what Nullean.Cdp reads', async (t) => {
  if (skip) return t.skip(skip);
  const page = await (await browser!.newContext()).newPage();
  const journey = parseJourney(read('everything.journey'), 'everything.journey');
  const result = await runJourney(page, journey, { baseUrl: origin + '/', journeyDir: join(site, 'journeys'), warn: () => {} });
  assert.equal(result.failure, undefined, JSON.stringify(result.failure));
  const actual = result.checkpoints
    .filter((c) => c.kind === 'text')
    .map((c) => ({ line: c.line, command: c.command, output: c.output.replaceAll(origin + '/', '{origin}/') }));
  const reference = JSON.parse(readFileSync(join(here, 'reference', 'everything.json'), 'utf8')) as { checkpoints: unknown[] };
  assert.deepEqual(actual, reference.checkpoints);
});

test('a failing step stops the run and names the step', async (t) => {
  if (skip) return t.skip(skip);
  const page = await (await browser!.newContext()).newPage();
  const journey = parseJourney('open actions.html\nget title\nfind role button click --name "No such button"\nget url', 'x.journey');
  const result = await runJourney(page, journey, { baseUrl: origin + '/', warn: () => {} });
  assert.equal(result.checkpoints.length, 1);
  assert.equal(result.failure?.line, 3);
  assert.equal(result.failure?.command, 'find role button click --name "No such button"');
});

test('a host outside the allow list is refused', async (t) => {
  if (skip) return t.skip(skip);
  const page = await (await browser!.newContext()).newPage();
  const journey = parseJourney('open https://elsewhere.test/', 'x.journey');
  const result = await runJourney(page, journey, { baseUrl: origin + '/', warn: () => {} });
  assert.match(result.failure?.message ?? '', /Opening https:\/\/elsewhere\.test\/ is refused: elsewhere\.test is not in the journey's allow list/);
});

test('a secret is resolved when typed and redacted from what is reported', async (t) => {
  if (skip) return t.skip(skip);
  const page = await (await browser!.newContext()).newPage();
  const journey = parseJourney('open actions.html\nfill #name $env:NJ_SECRET\nget value #name', 'x.journey');
  const result = await runJourney(page, journey, { baseUrl: origin + '/', secrets: (n) => (n === 'NJ_SECRET' ? 'hunter2hunter2' : undefined), warn: () => {} });
  assert.equal(result.failure, undefined);
  assert.equal(result.checkpoints[0].output, '$env:NJ_SECRET');
});

test('the synthetics adapter registers a step per line, synchronously, and runs them in order', async (t) => {
  if (skip) return t.skip(skip);
  const { journeySteps } = await import('../src/adapters/synthetics.ts');
  const page = await (await browser!.newContext()).newPage();
  const registered: { name: string; run: () => void | Promise<void> }[] = [];
  journeySteps(
    { page, step: (name, run) => registered.push({ name, run }), params: { url: origin } },
    'open actions.html\nclick #hello\nget text #log',
    { baseUrl: origin + '/' },
  );
  assert.deepEqual(registered.map((r) => r.name), ['open actions.html', 'click #hello', 'get text #log']);
  for (const r of registered) await r.run();
  assert.equal(await page.locator('#log').innerText(), 'clicked hello');
});

test('the synthetics adapter reads secrets from params before the environment', async (t) => {
  if (skip) return t.skip(skip);
  const { journeySteps } = await import('../src/adapters/synthetics.ts');
  const page = await (await browser!.newContext()).newPage();
  const registered: (() => void | Promise<void>)[] = [];
  journeySteps(
    { page, step: (_n, run) => registered.push(run), params: { NJ_PARAM_SECRET: 'param-secret-value' } },
    'open actions.html\nfill #name $env:NJ_PARAM_SECRET',
    { baseUrl: origin + '/' },
  );
  for (const run of registered) await run();
  assert.equal(await page.locator('#name').inputValue(), 'param-secret-value');
});

test('an unparsable journey throws when steps are registered, before anything runs', async () => {
  const { journeySteps } = await import('../src/adapters/synthetics.ts');
  assert.throws(() => journeySteps({ page: undefined as never, step: () => {} }, 'open /\nfrobnicate', { source: 'x.journey' }), /x\.journey:2: 'frobnicate' is not supported/);
});
