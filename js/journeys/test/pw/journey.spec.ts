import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { expect, test } from '@playwright/test';
import { journeySteps } from '../../src/adapters/test.ts';

const site = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..', 'tests', 'Nullean.Cdp.Tests', 'site');

test('a journey runs as test steps', async ({ page }) => {
  const checkpoints = await journeySteps(page, 'open actions.html\nclick #hello\nget text #log', {
    baseUrl: pathToFileURL(site + '/').href,
    source: 'actions.journey',
  });
  expect(checkpoints.map((c) => c.output)).toEqual(['clicked hello']);
});

test('a failing step fails the test', async ({ page }) => {
  test.fail();
  await journeySteps(page, 'open actions.html\nclick #nope', { baseUrl: pathToFileURL(site + '/').href, actionTimeout: 1000 });
});
