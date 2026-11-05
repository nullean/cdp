// Elastic Synthetics: one step() per journey line, so Kibana shows the journey as it was written.
// `step` is registered while the journey function runs (Synthetics requires that), so the text is parsed
// here and the run starts lazily in the first step.
//
//   import { journey, step } from '@elastic/synthetics';
//   import { journeySteps } from '@nullean/journeys/synthetics';
//   journey('checkout', ({ page, params }) => journeySteps({ page, step, params }, `open /\nclick #buy`, { baseUrl: params.url }));

import type { Page } from 'playwright-core';
import { joinLine } from '../commandline.ts';
import { parseJourney } from '../parse.ts';
import { JourneyRun } from '../run.ts';
import type { RunOptions } from '../run.ts';

export interface SyntheticsContext {
  page: Page;
  step: (name: string, callback: () => void | Promise<void>) => void;
  /** The journey's params: `$env:NAME` resolves from here first, then the process environment. */
  params?: Record<string, unknown>;
}

export interface SyntheticsJourneyOptions extends RunOptions {
  source?: string;
}

/** Registers one Synthetics step per journey line. A line that does not parse throws here, before anything runs. */
export function journeySteps(context: SyntheticsContext, journeyText: string, options: SyntheticsJourneyOptions = {}): void {
  const journey = parseJourney(journeyText, options.source);
  const params = context.params ?? {};
  const secrets = options.secrets ?? ((name: string) => (typeof params[name] === 'string' ? (params[name] as string) : process.env[name]));
  let started: Promise<JourneyRun> | undefined;
  for (const step of journey.steps) {
    context.step(joinLine(step.args), async () => {
      started ??= JourneyRun.start(context.page, journey, { ...options, secrets });
      await (await started).step(step);
    });
  }
}
