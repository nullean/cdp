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
expect text #count "1 result"
```

## Playwright Test

```ts
import { test } from '@playwright/test';
import { journeySteps } from '@nullean/journeys/test';

test('checkout', ({ page }) =>
  journeySteps(page, `
    open /
    find label Search fill lamp
    find role button click --name Search --exact
    expect text #count "1 result"
  `, { baseUrl: 'http://localhost:5000/', source: 'checkout.journey' }));
```

One `test.step` per journey line. A failing step fails the test there. The journey can as well come from a file:
`readFileSync('checkout.journey', 'utf8')`.

## Elastic Synthetics

```ts
import { expect, journey, step } from '@elastic/synthetics';
import { journeySteps } from '@nullean/journeys/synthetics';

journey('checkout', ({ page, params }) =>
  journeySteps({ page, step, params, expect }, `
    open /
    find label Search fill lamp
    find role button click --name Search --exact
    expect text #count "1 result"
  `, { baseUrl: params.url }));
```

One `step()` per line, registered synchronously (Synthetics requires it); the run starts in the first step. `$env:NAME`
resolves from `params`, then the environment. Pass `expect` so `expect` lines use Playwright's assertions.

## Without a framework

```ts
import { parseJourney, runJourney } from '@nullean/journeys';

const journey = parseJourney(`
  open /
  find label Search fill lamp
  expect text #count "1 result"
`, 'checkout.journey');

const result = await runJourney(page, journey, { baseUrl: 'http://localhost:5000/', expect });
result.checkpoints; // what each read returned
result.failure;     // the step it stopped at, or undefined
```

## Assertions

`expect [--not] [--timeout <ms>] <matcher> <target> [value]` checks again and again until it holds or times out, then
fails the step. Matchers: `visible`, `hidden`, `enabled`, `disabled`, `checked`, `unchecked`, `focused`, `editable`,
`attached`, `empty`, `text`, `value`, `attr`, `class`, `count`, `title`, `url`; `--contains`, `--regex`, `--ignore-case`
and `--inner-text` change how text is compared. Each is the Playwright assertion of the same name when `expect` is passed
(`expect` from `@playwright/test`, or from `@elastic/synthetics`); without it a built-in fallback checks the same things.
A passing `expect` records nothing. The full list is in the
[command reference](https://cdp.nullean.net/journeys/commands/#expect).

Only `visible`, `hidden`, `attached` and `count` can be asserted about an element that is not there; every other matcher
needs one, even negated, as in Playwright. Not supported: soft assertions, `toPass`, `toHaveScreenshot` and
`toMatchAriaSnapshot`. Quote a regular expression: unquoted, a backslash escapes the next character.

## What differs from the .NET runner

Reads (`get`, `is`, `find … text`) print what Nullean.Cdp prints: a test compares the corpus journey's 14 reads with
Nullean.Cdp's own. `network requests`, `links`, `timings`, `vitals` and `metrics` use the same output formats but are
**not yet tested against a browser or compared with Nullean.Cdp**. These differ on purpose, and say so:

| | |
|---|---|
| `snapshot` | Playwright's aria tree, marked `kind: 'aria'`; never compare it with a `.verified.journey`. `-c -i -d -u` are ignored with a warning. |
| `a11y` | axe-core (pass `axe: (await import('@axe-core/playwright')).AxeBuilder`), not Nullean.Cdp's own rules, so counts differ. Fails without it. |
| `expect` failures | Playwright's own message under Playwright's `expect`, cut to 14 lines; Nullean.Cdp's wording in the fallback. |
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
too, so the two cannot drift silently. `test/parity.test.ts` does the same for assertions: 96 cases through Playwright's
`expect` and through the fallback must agree.

## Develop

```sh
npm ci
npm test            # parser conformance, assertion parity, and the runner in Chrome (system Chrome; CDP_BROWSER_REQUIRED=1 to fail without)
npm run test:live   # the Playwright Test adapter
npm run build
```
