# Playwright and Elastic Synthetics

The same `.journey` file runs on [Playwright](https://playwright.dev): as a step-by-step Playwright Test, or as an
[Elastic Synthetics](https://www.elastic.co/docs/solutions/observability/synthetics) browser monitor. The npm package
[`@nullean/journeys`](https://www.npmjs.com/package/@nullean/journeys) parses journeys and runs them on a Playwright
`page`; Nullean.Cdp stays the reference runner, so a journey that passes `cdp run` is the one a monitor runs.

```sh
npm install @nullean/journeys
```

It needs `playwright-core` (any Playwright Test or `@elastic/synthetics` install has it), Node 20 or later (tested on 24), and
nothing from .NET.

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

Each journey line is a `test.step`, named as the normalised line, so the report reads like the file. A failing step
fails the test at that step. `journeySteps` returns the checkpoints (what each read returned). The journey can as well
come from a file: `readFileSync('journeys/checkout.journey', 'utf8')`. [`expect` lines](#assertions) use Playwright
Test's own `expect`.

## Elastic Synthetics

```ts
// checkout.journey.ts
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

Each line is a Synthetics `step()`, so Kibana shows the journey as it was written. Things to know:

- **The journey text lives in the file.** `npx @elastic/synthetics push` bundles code, not data files, so paste the
  journey into the template literal (or generate the file). The text is parsed when the journey registers, so a bad
  line fails before anything runs.
- **Secrets** (`$env:NAME`) resolve from the journey's `params`, then from the environment. `params.url` above comes
  from `--params` or `synthetics.config.ts`.
- **`upload` does not work**: Synthetics does not support `setInputFiles` in pushed journeys.
- Synthetics' own limits apply. Elastic's documentation gives a compressed bundle limit of 800 KB on one page and 1500 KB on
  another, and rules out native modules.
- **`expect`** is Playwright's: Synthetics re-exports `@playwright/test`'s `expect`, so pass it in as above. It does not
  support `toHaveScreenshot` or snapshot assertions, which `expect` lines never use.
- The adapter is tested against Synthetics' `step` contract with a stand-in. A push to Kibana has not been exercised
  yet.

## Assertions

`expect` lines (see the [command reference](commands.md#expect)) run on Playwright's own assertions when the adapter has
`expect`, which the Playwright Test adapter always does and the Synthetics adapter does when you pass it. Each matcher is
the Playwright assertion of the same name: `expect visible #go` is `expect(locator).toBeVisible()`, `expect --not text #n
"3"` is `.not.toHaveText("3")`, `expect class #go big --contains` is `toContainClass`. Without `expect` (a bare
`runJourney`) a small built-in fallback checks the same things and reports in Nullean.Cdp's words, so the step fails with
`Timed out after 5s waiting for css #count to have text "3": received "2 results".` A failure message under Playwright's
`expect` is Playwright's own, without colour and cut to 14 lines.

```ts
await runJourney(page, journey, { expect }); // expect from @playwright/test or @elastic/synthetics
```

The two paths are tested against each other: 96 cases (every matcher, holding and not) run through both and must agree.
That is how the rule that only `visible`, `hidden`, `attached` and `count` can be asserted about a missing element was
settled: Playwright's other assertions need the element, even negated, and so do ours.

Still the same on both: the timeout is `--timeout` on the line, else `actionTimeout`, else Playwright's own 5 s; a regular
expression is JavaScript's here and .NET's in `cdp run`, so keep to syntax both read alike, and quote it (`'^\d+ items$'`);
`url` takes the `wait --url` glob, turned into a regular expression because Playwright does not document its string form
as a glob. Not supported: soft assertions, `toPass`, `toHaveScreenshot`, `toMatchAriaSnapshot` (a tree template: the two
runners print different trees, and Synthetics supports no snapshot assertions).

## Without a framework

```ts
import { parseJourney, runJourney } from '@nullean/journeys';

const result = await runJourney(page, parseJourney(text, 'checkout.journey'), { baseUrl: 'http://localhost:5000/' });
result.checkpoints;  // { line, command, url, output, kind } for every read
result.failure;      // { line, command, message } for the step it stopped at, or undefined
```

`JourneyRun` runs one step at a time (`await run.step(step)`) when a framework wants to own the loop, as the two
adapters do.

| Option | |
|---|---|
| `baseUrl` | what relative `open` URLs resolve against; its host is allowed |
| `allowedHosts` | host patterns allowed beyond the base URL's host and the journey's `allow` lines |
| `secrets` | resolves `$env:NAME`; the process environment by default |
| `artifactsDir` | where `screenshot` writes; without it screenshot steps are skipped |
| `journeyDir` | the folder `upload` reads from |
| `actionTimeout`, `navigationTimeout` | milliseconds; applied to the page when given, Playwright's own otherwise |
| `expect` | Playwright's `expect`, for `expect` steps; the adapters pass it |
| `axe` | `(await import('@axe-core/playwright')).AxeBuilder`, for `a11y` |
| `warn`, `onStep` | called for skipped or different behaviour; before each step |

## The same parser

The package's parser is a port of the one in Nullean.Cdp, and the two are tested against each other: the package's
tests compare it with the JSON `cdp parse` prints for every corpus journey, and with a fixture of 65 invalid journeys
whose exact errors, lines included, the .NET tests check too. A journey that parses in one parses in the other.

```sh
cdp parse checkout.journey     # the steps as JSON, the contract between the two
```

## What is the same, and what is not

The allow list works as it does in `cdp run`: the base URL's host, `allow` lines and `allowedHosts`; a refused
navigation fails the step and refused sub-resources are not loaded. Secrets are resolved when typed and replaced by
their references in everything the run reports. Reads (`get`, `is`, `find … text`) print what Nullean.Cdp prints; the
package's tests compare a real run in Chrome with Nullean.Cdp's own readings. `network requests`, `links`, `timings`,
`vitals` and `metrics` use the same output formats but are not yet tested against a browser.

These differ on purpose, because Playwright produces them differently or owns them:

| | In Playwright |
|---|---|
| `snapshot` | Playwright's aria tree, with `kind: 'aria'` on the checkpoint. It is not the tree Nullean.Cdp prints, so never compare it with a `.verified.journey`. `-c -i -d -u` are ignored with a warning. |
| `a11y` | axe-core, not Nullean.Cdp's own rules, so counts differ. Without `axe` the step fails and says how to pass it. |
| `network har start`, `stop` | skipped with a warning: use `recordHar` in the Playwright configuration. |
| `screenshot` | written only when `artifactsDir` is given; Playwright and Synthetics keep their own screenshots. |
| `expect request` | from Playwright's `request`, `response` and `requestfinished` events; the body is `request.postDataBuffer()`. The same rules as in `cdp run`. |
| `expect same-document` | counts `domcontentloaded`, which a new document fires and `pushState` does not. |
| `set viewport … <scale>`, `set device` | the viewport only. Scale, user agent and touch are fixed when the context is created: use a Playwright project. |
| `set credentials` | refused. Set `httpCredentials` in the Playwright configuration: an extra header would go to every host. |
| `console` | `type: text`, one per line. |

Snapshots of the transcript stay with the .NET run: [Nullean.Snapshots](https://snapshots.nullean.net/formats/browser/)
compares what `cdp run` reads, and the Playwright runs prove the same journey still passes in Playwright and in
production.
