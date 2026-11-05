// Every matcher, holding and not holding, through Playwright's own `expect` and through the built-in fallback. The two
// must agree with each other and with what Nullean.Cdp's tests expect of the same page (ExpectationTests.cs): where
// whitespace, case, class lists, regular expressions or aria-disabled are read differently, this is where it shows.

import assert from 'node:assert/strict';
import { after, before, describe, test } from 'node:test';
import { expect } from '@playwright/test';
import type { Browser, Page } from 'playwright-core';
import { parseJourney, runJourney } from '../src/index.ts';
import type { PlaywrightExpect } from '../src/index.ts';
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

// [journey text before the assertion, the assertion, whether it holds]. The page is settled (its delayed changes have
// happened) before each; failing assertions wait their own 400 ms.
const cases: [string, string, boolean][] = [
  ['', 'expect visible #go', true],
  ['', 'expect visible #nothing', false],
  ['', 'expect visible #hidden', false],
  ['', 'expect --not visible #hidden', true],
  ['', 'expect --not visible #go', false],
  ['', 'expect hidden #nothing', true],
  ['', 'expect hidden #hidden', true],
  ['', 'expect hidden #go', false],
  ['', 'expect enabled #go', true],
  ['', 'expect enabled #off', false],
  ['', 'expect enabled #aria-off', false],
  ['', 'expect --not enabled #off', true],
  ['', 'expect --not enabled #nothing', false],
  ['', 'expect enabled #nothing', false],
  ['', 'expect disabled #off', true],
  ['', 'expect disabled #aria-off', true],
  ['', 'expect disabled #go', false],
  ['', 'expect disabled #nothing', false],
  ['', 'expect checked #news', true],
  ['', 'expect checked #plain', true],
  ['', 'expect checked #never', false],
  ['', 'expect unchecked #never', true],
  ['', 'expect unchecked #news', false],
  ['', 'expect --not checked #nothing', false],
  ['focus #name', 'expect focused #name', true],
  ['focus #name', 'expect focused #go', false],
  ['', 'expect editable #name', true],
  ['', 'expect editable #readonly', false],
  ['', 'expect attached #hidden', true],
  ['', 'expect attached #nothing', false],
  ['', 'expect empty #blank', true],
  ['', 'expect empty #empty-div', true],
  ['', 'expect empty #name', false],
  ['', 'expect empty #count', false],
  ['', 'expect text #count "2 results"', true],
  ['', 'expect text #count "2 RESULTS"', false],
  ['', 'expect text #count "2 RESULTS" --ignore-case', true],
  ['', 'expect text #count 2 --contains', true],
  ['', 'expect text #count 2', false],
  ['', "expect text #count '^\\d+ results$' --regex", true],
  ['', "expect text #count '^results' --regex", false],
  ['', 'expect text #spaced "one two three"', true],
  ['', 'expect text #spaced "one two"', false],
  ['', 'expect text #spaced "two" --contains', true],
  ['', 'expect text #hidden secret', true],
  ['', 'expect text find role heading --name Assertions Assertions', true],
  ['', 'expect text find nth 1 li Two', true],
  ['', 'expect text find last li Three', true],
  ['', 'expect text find last li One', false],
  ['', 'expect value #name Ada', true],
  ['', 'expect value #name ada', false],
  ['', "expect value #name '^A' --regex", true],
  ['', 'expect value #name d --contains', true],
  ['', 'expect value #blank ""', true],
  ['', 'expect attr #name placeholder', true],
  ['', 'expect attr #name placeholder Name', true],
  ['', 'expect attr #name placeholder name', false],
  ['', 'expect attr #name placeholder name --ignore-case', true],
  ['', 'expect attr #name nothing', false],
  ['', 'expect --not attr #name nothing', true],
  ['', 'expect attr #name data-state rea --contains', true],
  ['', 'expect class #go "primary big"', true],
  ['', 'expect class #go "big primary"', false],
  ['', 'expect class #go primary', false],
  ['', 'expect class #go "big primary" --contains', true],
  ['', 'expect class #go huge --contains', false],
  ['', "expect class #go '^primary' --regex", true],
  ['', 'expect count "#list li" 3', true],
  ['', 'expect count "#list li" 4', false],
  ['', 'expect count #nothing 0', true],
  ['', 'expect count #go 1', true],
  ['', 'expect count find last li 1', true],
  ['', 'expect title Loaded', true],
  ['', 'expect title Load', false],
  ['', 'expect title Load --contains', true],
  ['', 'expect title "^Load" --regex', true],
  ['', 'expect url "**/assertions.html"', true],
  ['', 'expect url "**/about.html"', false],
  ['', 'expect --not url "**/about.html"', true],
  ['', 'expect url "**/ASSERTIONS.HTML" --ignore-case', true],
  ['', "expect url '^http://127' --regex", true],
  ['', 'expect visible find role button --name Go --exact', true],
  ['', 'expect visible find role button --name Gone', false],
  ['', 'expect --not visible #gone', true],
  ['', 'expect visible #late', true],
  // Negated assertions about an element that is not there. Playwright: only visibility, attachment and counts can be
  // asserted about nothing; every other matcher needs an element, even negated.
  ['', 'expect --not focused #nothing', false],
  ['', 'expect --not editable #nothing', false],
  ['', 'expect --not empty #nothing', false],
  ['', 'expect --not disabled #nothing', false],
  ['', 'expect --not unchecked #nothing', false],
  ['', 'expect --not attached #nothing', true],
  ['', 'expect --not text #nothing x', false],
  ['', 'expect --not value #nothing x', false],
  ['', 'expect --not attr #nothing x y', false],
  ['', 'expect --not class #nothing x', false],
  ['', 'expect --not count #nothing 1', true],
];

describe('every matcher agrees between Playwright expect and the fallback', () => {
  for (const mode of ['playwright', 'fallback'] as const) {
    describe(mode, () => {
      let page: Page | undefined;
      before(async () => {
        if (skip) return;
        page = await (await browser!.newContext()).newPage();
        await page.goto(`${origin}/assertions.html`);
        await page.waitForSelector('#late');
      });

      for (const [setup, line, holds] of cases) {
        test(`${setup ? setup + '; ' : ''}${line} ${holds ? 'holds' : 'fails'}`, async (t) => {
          if (skip) return t.skip(skip);
          const text = `${setup ? setup + '\n' : ''}${line.replace('expect ', 'expect --timeout 400 ')}`;
          const result = await runJourney(page!, parseJourney(text, 'parity.journey'), {
            baseUrl: origin + '/',
            warn: () => {},
            expect: mode === 'playwright' ? (expect as unknown as PlaywrightExpect) : undefined,
          });
          assert.equal(result.failure === undefined, holds, result.failure?.message ?? 'passed, but should have failed');
        });
      }
    });
  }
});

describe('the fallback reports in Nullean.Cdp\'s words', () => {
  const messages: [string, string][] = [
    ['expect --timeout 200 text #count 3', 'Timed out after 0.2s waiting for css #count to have text "3": received "2 results".'],
    ['expect --timeout 200 visible #nothing', 'Timed out after 0.2s waiting for css #nothing to be visible: received no element matches.'],
    ['expect --timeout 200 --not visible #go', 'Timed out after 0.2s waiting for css #go to not be visible: received visible.'],
    ['expect --timeout 200 count "#list li" 4', 'Timed out after 0.2s waiting for css "#list li" to have count 4: received 3.'],
    ['expect --timeout 200 attr #name nothing', 'Timed out after 0.2s waiting for css #name to have attribute nothing: received no such attribute.'],
    ['expect --timeout 200 title Other', 'Timed out after 0.2s waiting for the page title to have title "Other": received "Loaded".'],
  ];
  for (const [line, message] of messages) {
    test(line, async (t) => {
      if (skip) return t.skip(skip);
      const page = await (await browser!.newContext()).newPage();
      await page.goto(`${origin}/assertions.html`);
      await page.waitForSelector('#late');
      const result = await runJourney(page, parseJourney(line, 'm.journey'), { baseUrl: origin + '/', warn: () => {} });
      assert.equal(result.failure?.message, message);
      assert.equal(result.failure?.line, 1);
    });
  }
});
