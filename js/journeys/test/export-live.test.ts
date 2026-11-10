// Runs generated Synthetics files with the real @elastic/synthetics CLI against the corpus site, in the installed Chrome:
// the journey registers, its steps run, a feature block runs only when its flag was exported as on, and a failing step
// fails the run. Needs the built package (the generated files import @nullean/journeys/synthetics), so it skips before
// `npm run build`; pushing to Kibana is not exercised. CDP_BROWSER_REQUIRED=1 makes a missing Chrome a failure.

import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { after, before, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { main } from '../src/cli.ts';
import { launchChrome, startSite } from './site.ts';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const work = join(root, '.generated-test');
const cli = join(root, 'node_modules', '@elastic', 'synthetics', 'dist', 'cli.js');

let origin = '';
let stopSite = () => {};
let skip: string | false = false;

before(async () => {
  const served = await startSite();
  origin = served.origin;
  stopSite = served.stop;
  const chrome = await launchChrome();
  await chrome.browser?.close();
  skip = chrome.skip || (existsSync(join(root, 'dist', 'adapters', 'synthetics.js')) ? false : 'run `npm run build` first: the generated files import the built package');
  rmSync(work, { recursive: true, force: true });
  mkdirSync(join(work, 'journeys', 'smoke'), { recursive: true });
  writeFileSync(
    join(work, 'journeys', 'smoke', 'index.journey'),
    '# @tags smoke\n# @monitor schedule=5 locations=germany\nopen index.html\nget title\nfeature navigation-preview\n  expect visible #does-not-exist\nend\nexpect visible h1\n',
  );
});

after(() => {
  stopSite();
  rmSync(work, { recursive: true, force: true });
});

// Not spawnSync: the test site is served from this process, and a blocked event loop cannot answer Chrome.
async function exportAndRun(features: string, out: string): Promise<{ status: number; stdout: string; stderr: string }> {
  const args = ['export-synthetics', join(work, 'journeys'), '--out', join(work, out), '--base-url', origin + '/', ...(features ? ['--features', features] : [])];
  assert.equal(main(args, root, { out: () => {}, err: (l) => assert.fail(l) }), 0);
  return new Promise((done) =>
    execFile(process.execPath, [cli, join(work, out), '--playwright-options', '{"channel":"chrome"}'], { cwd: root }, (error, stdout, stderr) =>
      done({ status: error ? ((error as { code?: number }).code ?? 1) : 0, stdout, stderr }),
    ),
  );
}

test('a generated journey runs in Synthetics, and its feature block is skipped without the flag', async (t) => {
  if (skip) return t.skip(skip);
  const run = await exportAndRun('', 'off');
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /Journey: smoke\/index/);
  assert.match(run.stdout, /4 passed/);
});

test('with the flag exported as on, the feature block runs and a failing step fails the run', async (t) => {
  if (skip) return t.skip(skip);
  const run = await exportAndRun('navigation-preview', 'on');
  assert.notEqual(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /expect visible #does-not-exist' failed/);
});
