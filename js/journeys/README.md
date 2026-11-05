# @nullean/journeys

Parse and run [Nullean.Cdp](https://cdp.nullean.net) journeys (agent-browser commands, one per line) on Playwright:
in Playwright Test, or pushed to [Elastic Synthetics](https://www.elastic.co/docs/solutions/observability/synthetics)
as a browser monitor. The same `.journey` file the .NET tests snapshot.

```text
# checkout.journey
open /
find label Search fill lamp
find role button click --name Search --exact
get text #count
```

## Playwright Test

```ts
import { test } from '@playwright/test';
import { journeySteps } from '@nullean/journeys/test';

test('checkout', ({ page }) =>
  journeySteps(page, readFileSync('checkout.journey', 'utf8'), { baseUrl: 'http://localhost:5000/', source: 'checkout.journey' }));
```

One `test.step` per journey line. A failing step fails the test there.

## Elastic Synthetics

```ts
import { journey, step } from '@elastic/synthetics';
import { journeySteps } from '@nullean/journeys/synthetics';

journey('checkout', ({ page, params }) =>
  journeySteps({ page, step, params }, `open /\nclick #buy`, { baseUrl: params.url }));
```

One `step()` per line, registered synchronously (Synthetics requires it); the run starts in the first step. `$env:NAME`
resolves from `params`, then the environment.

## Without a framework

```ts
import { parseJourney, runJourney } from '@nullean/journeys';

const result = await runJourney(page, parseJourney(text, 'checkout.journey'), { baseUrl });
result.checkpoints; // what each read returned
result.failure;     // the step it stopped at, or undefined
```

## What differs from the .NET runner

Reads (`get`, `is`, `find … text`) print what Nullean.Cdp prints: a test compares the corpus journey's 14 reads with
Nullean.Cdp's own. `network requests`, `links`, `timings`, `vitals` and `metrics` use the same output formats but are
**not yet tested against a browser or compared with Nullean.Cdp**. These differ on purpose, and say so:

| | |
|---|---|
| `snapshot` | Playwright's aria tree, marked `kind: 'aria'`; never compare it with a `.verified.journey`. `-c -i -d -u` are ignored with a warning. |
| `a11y` | axe-core (pass `axe: (await import('@axe-core/playwright')).AxeBuilder`), not Nullean.Cdp's own rules, so counts differ. Fails without it. |
| `network har …` | skipped with a warning: use `recordHar` in the Playwright config. |
| `screenshot` | written only when `artifactsDir` is given; otherwise skipped with a warning (Playwright and Synthetics keep their own). |
| `set viewport … <scale>`, `set device` | the viewport only; scale, user agent and touch are fixed when the context is created. |
| `set credentials` | refused: set `httpCredentials` in the Playwright config (an extra header would go to every host). |
| `console` | `type: text` lines. |

The allow list works as in .NET: the base URL's host, `allow` lines and `allowedHosts`; a refused navigation fails the
step, refused sub-resources are not loaded. Timeouts are Playwright's unless `actionTimeout` or `navigationTimeout` is
given (Nullean.Cdp's are 5 s and 30 s).

## Same parser as .NET

`parseJourney` is a port of `Journey.Parse`. `test/conformance.test.ts` compares it with the JSON `cdp parse` prints for
every corpus journey and with `tests/Nullean.Cdp.Tests/fixtures/journey-errors.json`, whose cases the .NET tests check
too, so the two cannot drift silently.

## Develop

```sh
npm ci
npm test            # parser conformance, and the runner in Chrome (system Chrome; CDP_BROWSER_REQUIRED=1 to fail without)
npm run test:live   # the Playwright Test adapter
npm run build
```
