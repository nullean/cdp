# Nullean.Cdp.Journeys

![Nullean.Cdp](https://raw.githubusercontent.com/nullean/cdp/main/docs/images/brand/png/lockup.png)

Synthetic journeys: plain text files written in agent-browser's command language, parsed, run against Chrome with
Nullean.Cdp, and exported to agent-browser's batch JSON.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Cdp.Journeys?style=flat-square)](https://www.nuget.org/packages/Nullean.Cdp.Journeys)

![cdp run on a journey against a local page: each checkpoint's output between nonce-fenced PAGE_CONTENT markers](https://raw.githubusercontent.com/nullean/cdp/main/docs/images/cdp-run.png)

## Install

```sh
dotnet add package Nullean.Cdp.Journeys
```

## Quick start

`Nullean.Cdp.Journeys` runs *synthetic journeys*: text files with one agent-browser command per line.

```
# checkout.journey
open /shop
find label Search fill lamp
press Enter
wait --text "results"
find role button click --name "Add to basket"
snapshot -c -s main          # a checkpoint: its output is recorded
get text .basket-count       # another
```

```csharp
using Nullean.Cdp.Journeys;

var journey = Journey.Load("journeys/checkout.journey");
var result = await JourneyRunner.RunAsync(journey, browser, new JourneyOptions { BaseUrl = new("http://localhost:5000/") });
foreach (var checkpoint in result.Checkpoints)
    Console.WriteLine($"{checkpoint.Command}\n{checkpoint.Output}");
if (result.Failure is { } failure)
    Console.WriteLine(failure.Describe(result.Source));   // checkout.journey:5: find role button ... and why
```

## What a journey can do

- **The commands** are agent-browser's, written the same way: navigation, every element action (`click`,
  `dblclick`, `hover`, `focus`, `fill`, `type`, `check`, `select`, `upload`, `drag`, `scroll`, ...), `press` and
  `keyboard`, `find` (role, text, label, ..., first, last, nth), `wait` (element, time, text, URL, load state),
  `set` (viewport, media, device, geo, offline, headers, credentials), and the checkpoints `snapshot`, `get`, `is`,
  `console`, `errors`, `network requests` (`--failed` for 4xx, 5xx and requests that got no answer), `links`
  (checks the page's links: same-site by default, `--external` for other sites in the allow list), `a11y` (our own
  accessibility rules), and `timings`, `vitals` and `metrics` (raw, or `--budget lcp=2500 cls=0.1` for stable
  transcripts). `--strict` on `network requests`, `links` and `a11y` fails the step instead of listing.
  `screenshot [selector] <path.png> [--full]` and `network har start` / `network har stop <path>` write into the
  artifacts folder. Left out on purpose: refs, `eval`, cookies and session state, tabs and downloads. Anything
  unsupported is an error that names the line.
- **No refs.** `@e3` changes from run to run, so journeys use `find role … --name`, `find text`, `find label` or CSS.
- **Quoting** is a shell's: double or single quotes, backslash escapes. `# ` starts a comment, but `#id` is a selector.
- **URLs:** `open /shop` resolves against the base URL like a link in a page would, so a base URL for a folder needs
  its trailing slash.
- **Safe to share:** a run reaches only the base URL's host and the hosts in the journey's `allow` lines, on every
  request including clicks, redirects and popups; `file://` stays in the base folder; downloads are denied. Passwords
  are `$env:NAME` references, typed when the step runs and redacted from everything the run reports.
- **Replay in agent-browser:** `journey.ToAgentBrowserBatch(baseUrl)` gives the batch JSON, for
  `agent-browser batch --bail`. A journey using every supported command runs there unchanged.

The same journeys replay in agent-browser, in `dotnet test` (with [Nullean.Snapshots](https://github.com/nullean/snapshots))
and from the [cdp command line tool](https://www.nuget.org/packages/Nullean.Cdp.Cli). The npm package
[`@nullean/journeys`](https://www.npmjs.com/package/@nullean/journeys) runs them on Playwright Test and Elastic Synthetics.

## Learn more

- Documentation: https://cdp.nullean.net/journeys/, and [every command](https://cdp.nullean.net/journeys/commands/)
- The browser client underneath: [Nullean.Cdp](https://www.nuget.org/packages/Nullean.Cdp)
- Source and issues: https://github.com/nullean/cdp
