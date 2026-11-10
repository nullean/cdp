// Elastic Synthetics: one step() per journey line, so Kibana shows the journey as it was written.
// `step` is registered while the journey function runs (Synthetics requires that), so the text is parsed
// here and the run starts lazily in the first step.
//
//   import { journey, step } from '@elastic/synthetics';
//   import { journeySteps } from '@nullean/journeys/synthetics';
//   journey('checkout', ({ page, params }) => journeySteps({ page, step, params, expect }, `open /\nclick #buy`, { baseUrl: params.url }));

import type { APIRequestContext, Page } from 'playwright-core';
import { joinLine } from '../commandline.ts';
import { parseJourney } from '../parse.ts';
import { JourneyRun } from '../run.ts';
import { parseHttpFile } from '../http.ts';
import { HttpRun } from '../http-run.ts';
import type { HttpRunOptions } from '../http-run.ts';
import { requirementMet } from '../parse.ts';
import type { PlaywrightExpect } from '../expect.ts';
import type { RunOptions } from '../run.ts';

export interface SyntheticsContext {
  page: Page;
  step: (name: string, callback: () => void | Promise<void>) => void;
  /** The journey's params: `$env:NAME` resolves from here first, then the process environment. */
  params?: Record<string, unknown>;
  /** `expect` from `@elastic/synthetics` (it is Playwright's). Without it, `expect` steps use a built-in fallback. */
  expect?: PlaywrightExpect;
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
      started ??= JourneyRun.start(context.page, journey, { expect: context.expect, ...options, secrets });
      await (await started).step(step);
    });
  }
}

export interface SyntheticsHttpContext {
  /** The journey's `request` (Playwright's APIRequestContext). */
  request: APIRequestContext;
  step: (name: string, callback: () => void | Promise<void>) => void;
  /** The journey's params: `{{$secret NAME}}` and `{{$processEnv NAME}}` resolve from here first, then the process environment. */
  params?: Record<string, unknown>;
}

export interface SyntheticsHttpOptions extends HttpRunOptions {
  source?: string;
}

/**
 * Registers one Synthetics step per request of a `.http` file, in file order, so Kibana shows the file as it was written.
 * A step is named by its request line as written (variables unresolved, so no secret reaches a step name). A file whose
 * `@requires` does not hold, or that is `@mutates` in a read-only run, registers one step that says so.
 */
export function httpSteps(context: SyntheticsHttpContext, httpText: string, options: SyntheticsHttpOptions = {}): void {
  const file = parseHttpFile(httpText, options.source);
  const params = context.params ?? {};
  const secrets = options.secrets ?? ((name: string) => (typeof params[name] === 'string' ? (params[name] as string) : undefined));
  const unmet = file.metadata?.requires?.find((r) => !requirementMet(r, options));
  if (unmet !== undefined || (options.readOnly === true && file.metadata?.mutates === true)) {
    context.step(unmet !== undefined ? `skipped: @requires ${unmet} does not hold` : 'skipped: @mutates in a read-only run', () => {});
    return;
  }
  const run = new HttpRun(context.request, file, { ...options, secrets });
  for (const request of file.requests) {
    context.step(request.name ? `${request.name}: ${request.requestLine}` : request.requestLine, async () => {
      await run.send(request);
    });
  }
}
