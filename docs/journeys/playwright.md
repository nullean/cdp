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
  journey into the template literal, or [generate the files](#export-the-monitors). The text is parsed when the
  journey registers, so a bad line fails before anything runs.
- **Secrets** (`$env:NAME`) resolve from the journey's `params`, then from the environment. `params.url` above comes
  from `--params` or `synthetics.config.ts`.
- **`upload` does not work**: Synthetics does not support `setInputFiles` in pushed journeys.
- Synthetics' own limits apply. Elastic's documentation gives a compressed bundle limit of 800 KB on one page and 1500 KB on
  another, and rules out native modules.
- **`expect`** is Playwright's: Synthetics re-exports `@playwright/test`'s `expect`, so pass it in as above. It does not
  support `toHaveScreenshot` or snapshot assertions, which `expect` lines never use.
- The adapter is tested against Synthetics' `step` contract with a stand-in, and generated files run under the real
  `@elastic/synthetics` CLI in the tests. A push to Kibana has not been exercised yet.

## Export the monitors

Journeys that are marked `@monitor` become Synthetics files with one command:

```sh
npx journeys export-synthetics journeys --out synthetics/generated --features navigation-preview --env edge
npx @elastic/synthetics push synthetics/generated --id my-project
```

```text
# journeys/landing/home.journey
# @tags navigation smoke
# @monitor schedule=10 locations=germany,us_east
# @alert pagerduty
open /docs
expect title "Elastic Docs"
```

| Option | |
|---|---|
| `<file, folder or glob>...` | what to export; a folder means every `.journey` file under it |
| `--out <folder>` | where the generated `*.journey.ts` files go, one per exported journey, named by its path below the arguments (`landing/home.journey.ts`) |
| `--features <a,b>` | the feature flags that are on: judged against each `@requires`, and embedded so `feature` blocks skip the way they do in any other run |
| `--build-type`, `--env` | the same for `build-type:` and `env:` conditions |
| `--paging` | turn `@alert pagerduty` into alert configuration (below) |
| `--base-url <url>` | embed the base URL; without it a generated file reads `params.url`, from `--params` or the Synthetics config |
| `--root <folder>` | names are paths under this folder; by default the folder the arguments start in |

- **Which journeys export.** A journey (or `.http` file, see below) needs `@monitor`; its `@requires` must hold for the flags, build type and
  environment you give; and it must not be `@mutates`, because a monitor runs against a live site. Every journey that
  does not export is printed with the reason. A generated file also runs read-only (`readOnly: true`).
- **What the directives become.** `schedule` and `locations` go to `monitor.use` and are checked against what
  Synthetics accepts, so a schedule of 7 minutes fails the export, naming the journey, and not the push. `@tags` become
  the monitor's tags.
- **Alerting.** Kibana routes alerts. Without `--paging` every monitor keeps Kibana's default status alerting. With
  `--paging`, a journey that says `@alert pagerduty` gets `alert: { status: { enabled: true } }` and the tag
  `alert:pagerduty`, which a Kibana rule can route to PagerDuty. The export never talks to Kibana or PagerDuty.
- **A skipped feature block shows as passed.** Synthetics has no skipped steps for a step that ran as a no-op, so a
  block the run does not meet looks like a passing step in Kibana.
- **The output depends only on the inputs**: no timestamps, a stable order, so exporting twice changes nothing in git.
  The files start with a generated-file header, and a later export removes generated files whose journey is no longer
  exported, and only those. `push` replaces the project's monitors, so removing a journey removes its monitor.

## Monitor an API with `.http` files

An endpoint that has no page, such as an MCP server or a JSON API, is a `.http` file: the format of Visual Studio, the
VS Code REST Client and the JetBrains HTTP Client, which [nullean/snapshots](https://github.com/nullean/snapshots)
runs in `dotnet test`. `httpSteps` runs the same file as a Synthetics journey, one step per request:

```ts
import { journey, step } from '@elastic/synthetics';
import { httpSteps } from '@nullean/journeys/synthetics';

journey('mcp', ({ request, params }) =>
  httpSteps({ request, step, params }, `
    @host = ${params.url}
    # @readonly
    # @expect status 200
    # @expect json $.result.tools[*].name contains "search_docs"
    POST {{host}}/docs/_mcp
    Content-Type: application/json

    {"jsonrpc":"2.0","id":1,"method":"tools/list"}
  `, { baseUrl: params.url }));
```

`journeys export-synthetics` exports a `.http` file with `@monitor` the same way as a journey, into
`*.http.journey.ts`. The header directives (`@tags`, `@requires`, `@monitor`, `@alert`, `@mutates`) go in the comments
before the first request, with the journey grammar; Nullean.HttpFileParser reads them as plain comments.

- **A step is a request.** Its name is the request line as written, with `{{variables}}` unresolved, so a secret never
  reaches Kibana. A name from `# @name` leads it. Requests run in file order, and a failed one skips the rest.
- **A 404 or 500 is a response.** Only a request that cannot be resolved or sent, a read-only refusal, or an unmet
  `# @expect` fails: `status 200` or `2xx`, `header content-type ~ json` (a case-insensitive substring), and
  `json $.items[*].name equals|contains "lamp"` (`equals` needs every selected value, `contains` one). The messages are
  Nullean.HttpFileParser's.
- **Read-only.** With `readOnly: true`, which every exported monitor sets, only GET, HEAD and OPTIONS are sent. A
  request that reads although it is not a GET, such as an MCP `tools/list`, says so with `# @readonly`.
- **What is supported.** `METHOD URL`, headers, an inline body, `###` and `### name`, `# @name`, `# @no-redirect`,
  `# @timeout`, file variables (`@host = ...`), and references: `{{host}}`, `{{login.response.body.$.token}}`,
  `{{login.response.headers.Location}}`, `{{$guid}}`, `{{$uuid}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`,
  `{{$randomInt}}`, `{{$processEnv NAME}}`, `{{$env.NAME}}` and `{{$secret NAME}}` (from the journey's `params`, then the
  environment).
- **What is refused, with its line.** File bodies (`< ./file`), `http-client.env.json` environments, `$datetime` and the
  other dynamic variables, references to a request, JetBrains scripts, `>>` redirection, `@prompt`, cURL and GraphQL.
  A URL that starts with `/` needs `baseUrl`, or an absolute `@host`.
- **A difference from the .NET runner.** A number a JSON expectation compares is JavaScript's reading of it, so `1.0`
  is `1`. The two parsers are not run against shared fixtures yet.

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
| `viewport`, `userAgent`, `headers` | the run's defaults, in place before the first step (see [run defaults](index.md#run-defaults)); `userAgent` needs Chromium |
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
| `set useragent` | Chromium only, through a DevTools session, for the page. Other browsers: set `userAgent` in the Playwright config. |
| `expect request` | from Playwright's `request`, `response` and `requestfinished` events; the body is `request.postDataBuffer()`. The same rules as in `cdp run`. |
| `expect same-document` | counts `domcontentloaded`, which a new document fires and `pushState` does not. |
| `set viewport … <scale>`, `set device` | the viewport only. Scale, user agent and touch are fixed when the context is created: use a Playwright project. |
| `set credentials` | refused. Set `httpCredentials` in the Playwright configuration: an extra header would go to every host. |
| `console` | `type: text`, one per line. |

Snapshots of the transcript stay with the .NET run: [Nullean.Snapshots](https://snapshots.nullean.net/formats/browser/)
compares what `cdp run` reads, and the Playwright runs prove the same journey still passes in Playwright and in
production.
