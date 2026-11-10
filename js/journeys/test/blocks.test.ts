// feature blocks on a real page: a block the run does not meet is skipped as a whole and shows once, as a checkpoint that
// says why. The same cases as FeatureBlockTests in the .NET tests. Skips when Chrome is missing; CDP_BROWSER_REQUIRED=1
// makes that a failure.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import type { Browser } from 'playwright-core';
import { parseJourney, runJourney } from '../src/index.ts';
import type { RunOptions } from '../src/index.ts';
import { launchChrome, startSite } from './site.ts';

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

const both = 'open index.html\nfeature preview\n  get title\nend\nfeature !preview\n  get url\nend\nget title\n';

async function run(text: string, options: Partial<RunOptions> = {}) {
  const page = await (await browser!.newContext()).newPage();
  const steps: number[] = [];
  const result = await runJourney(page, parseJourney(text, 'blocks.journey'), { baseUrl: origin + '/', warn: () => {}, onStep: (s) => steps.push(s.line), ...options });
  return { result, steps, summary: result.checkpoints.map((c) => [c.line, c.command, c.output]) };
}

test('a block runs when its flag is on, and the other is skipped once', async (t) => {
  if (skip) return t.skip(skip);
  const { result, steps, summary } = await run(both, { features: ['preview'] });
  assert.equal(result.failure, undefined);
  assert.deepEqual(summary, [[3, 'get title', 'Fixture shop'], [5, 'feature !preview', 'skipped: !preview'], [8, 'get title', 'Fixture shop']]);
  assert.deepEqual(steps, [1, 3, 8]);
});

test('the other block runs when the flag is off', async (t) => {
  if (skip) return t.skip(skip);
  const { result, steps, summary } = await run(both);
  assert.equal(result.failure, undefined);
  assert.deepEqual(summary, [[2, 'feature preview', 'skipped: preview'], [6, 'get url', origin + '/index.html'], [8, 'get title', 'Fixture shop']]);
  assert.ok(!steps.includes(3));
});

test('build type and environment conditions use what the run knows', async (t) => {
  if (skip) return t.skip(skip);
  const text = 'open index.html\nfeature build-type:codex,assembler env:edge\nget title\nend\n';
  assert.equal((await run(text, { buildType: 'Assembler', env: 'edge' })).summary[0][2], 'Fixture shop');
  assert.equal((await run(text, { buildType: 'assembler', env: 'prod' })).summary[0][2], 'skipped: env:edge');
  assert.equal((await run(text)).summary[0][2], 'skipped: build-type:codex,assembler');
});

test('a failing step inside a running block stops the run', async (t) => {
  if (skip) return t.skip(skip);
  const { result } = await run('open index.html\nfeature preview\n  click #nothing-here\nend\nget title\n', { features: ['preview'], actionTimeout: 1000 });
  assert.equal(result.failure?.line, 3);
  assert.equal(result.checkpoints.length, 0);
});
