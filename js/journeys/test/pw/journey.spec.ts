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

test('expect steps use Playwright Test\'s own assertions', async ({ page }) => {
  await journeySteps(page, `
    open assertions.html
    expect visible #go
    expect text #count "2 results"
    expect --not visible #gone
    expect title Loaded
  `, { baseUrl: pathToFileURL(site + '/').href });
});

test('a failing expect step fails the test with Playwright\'s message', async ({ page }) => {
  test.fail();
  await journeySteps(page, 'open assertions.html\nexpect --timeout 500 text #count 3', { baseUrl: pathToFileURL(site + '/').href });
});
