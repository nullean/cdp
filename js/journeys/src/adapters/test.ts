// Playwright Test: one test.step per journey line, so the report shows the journey as it was written.
//
//   import { expect, test } from '@playwright/test';
//   import { journeySteps } from '@nullean/journeys/test';
//   test('checkout', ({ page }) => journeySteps(page, `open /\nclick #buy`, { baseUrl: 'http://localhost:5000' }));

import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { joinLine } from '../commandline.ts';
import { parseJourney } from '../parse.ts';
import { JourneyRun } from '../run.ts';
import type { PlaywrightExpect } from '../expect.ts';
import type { Checkpoint, RunOptions } from '../run.ts';

export interface TestJourneyOptions extends RunOptions {
  /** The name used in parse errors, e.g. the journey file's path. */
  source?: string;
}

/** Runs journey text on `page`, returning the checkpoints; a failing step fails the test at that step. */
export async function journeySteps(page: Page, journeyText: string, options: TestJourneyOptions = {}): Promise<Checkpoint[]> {
  const journey = parseJourney(journeyText, options.source);
  // `expect` steps use Playwright Test's own assertions.
  const run = await JourneyRun.start(page, journey, { expect: expect as unknown as PlaywrightExpect, ...options });
  const checkpoints: Checkpoint[] = [];
  try {
    for (const step of journey.steps) {
      await test.step(joinLine(step.args), async () => {
        const checkpoint = await run.step(step);
        if (checkpoint) checkpoints.push(checkpoint);
      });
    }
  } finally {
    await run.dispose();
  }
  return checkpoints;
}
