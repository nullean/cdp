<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/brand/lockup.svg">
    <img src="docs/images/brand/lockup-light.svg" width="196" height="96" alt="Nullean.Cdp">
  </picture>
</p>

<p align="center">A small Chrome DevTools Protocol client for .NET, and the synthetic journeys built on it: plain text browser checks that replay in agent-browser, <code>dotnet test</code> and a CLI.</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Nullean.Cdp"><img src="https://img.shields.io/nuget/v/Nullean.Cdp?style=flat-square" alt="NuGet"></a>
  <a href="https://github.com/nullean/cdp/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/nullean/cdp/ci.yml?branch=main&style=flat-square&label=ci" alt="CI"></a>
  <a href="https://cdp.nullean.net"><img src="https://img.shields.io/badge/docs-cdp.nullean.net-20a0a0?style=flat-square" alt="Docs"></a>
</p>

![cdp run on a five-step journey against a local page: the journey, then each checkpoint's output between nonce-fenced PAGE_CONTENT markers, then "ok search.journey: 5 steps, 2 checkpoints"](docs/images/cdp-run.png)

A small Chrome DevTools Protocol client for .NET: find and launch Chrome, drive pages with agent-browser style
locators, and read the accessibility tree. Native AOT and trim clean, with no dependencies beyond the BCL.

It is the browser underneath *synthetic journeys*: plain text files written in agent-browser's command language that
replay in agent-browser, in `dotnet test` (with [Nullean.Snapshots](https://github.com/nullean/snapshots)) and from
a command line tool. The npm package [`@nullean/journeys`](https://www.npmjs.com/package/@nullean/journeys) runs the same
journeys on Playwright Test and Elastic Synthetics (see `js/journeys`).

```
# Get started from the landing page
open /
find role link click --name "Get started"
wait main
snapshot -c -s main
```

Status: early, 0.x. See [ROADMAP.md](ROADMAP.md).

## Install

```sh
dotnet add package Nullean.Cdp               # the CDP client
dotnet add package Nullean.Cdp.Journeys      # parse and run journeys from .NET
dotnet tool install -g Nullean.Cdp.Cli       # the cdp command
```

## Quick start

```csharp
using Nullean.Cdp;
using Nullean.Cdp.Accessibility;

await using var browser = await Browser.LaunchAsync();      // headless, temporary profile
await using var context = await browser.NewContextAsync();  // own cookies and storage
var page = await context.NewPageAsync();
await page.GotoAsync("http://localhost:5000/");

Console.Write(await page.SnapshotAsync(new AxSnapshotOptions { Compact = true, Selector = "main" }));
// - main
//   - heading "Fixture shop" [level=1]
//   - textbox "Search"
//   - checkbox "Only in stock" [checked=true]
```

Locators wait for their element and act like a user:

```csharp
await page.Label("Search").FillAsync("lamp");
await page.Role("checkbox", "Only in stock").CheckAsync();
await page.Role("button", "Go").ClickAsync();         // waits for any navigation it starts
await page.Text("3 results").WaitAsync();
```

`page.SendAsync(method, params)` and `browser.Connection` reach any CDP command the library does not wrap.

## Journeys

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

## The CLI

```sh
cdp check journeys/*.journey                             # parse only, every error with its line
cdp run journeys/checkout.journey --base-url http://localhost:5000/
cdp run site/journeys/*.journey --base-url ./site --json
cdp snapshot http://localhost:5000/ --compact            # one page's accessibility tree
cdp batch journeys/checkout.journey --base-url http://localhost:5000/ | agent-browser batch --bail
cdp chrome                                               # which Chrome, or where it looked
```

Everything the page produced is printed between `--- PAGE_CONTENT nonce=… ---` markers with a nonce per run, so an
agent reading the output can tell the page's words from the tool's. Exit codes: 0 ok, 1 failed, 2 usage, 3 no browser.
The command name `cdp` is a working name.

## Finding Chrome

`CDP_CHROME`, then `CHROME_PATH`, then the standard install locations for Chrome, Chrome for Testing, Chromium and
Edge on each OS, then `google-chrome`, `chromium` and `chrome` on the `PATH`. When nothing is found, the error lists
every place it looked.

## Packages

| Package | What it is | |
|---|---|---|
| [`Nullean.Cdp`](src/Nullean.Cdp/README.md) | the CDP client: find and launch Chrome, locators, the accessibility tree. No dependencies beyond the BCL | [![NuGet](https://img.shields.io/nuget/v/Nullean.Cdp?style=flat-square)](https://www.nuget.org/packages/Nullean.Cdp) |
| [`Nullean.Cdp.Journeys`](src/Nullean.Cdp.Journeys/README.md) | synthetic journeys: parse, run with Nullean.Cdp, export to agent-browser batch JSON | [![NuGet](https://img.shields.io/nuget/v/Nullean.Cdp.Journeys?style=flat-square)](https://www.nuget.org/packages/Nullean.Cdp.Journeys) |
| [`Nullean.Cdp.Cli`](src/Nullean.Cdp.Cli/README.md) | the `cdp` command: `check`, `run`, `snapshot`, `screenshot`, `batch`, `parse`, `chrome` | [![NuGet](https://img.shields.io/nuget/v/Nullean.Cdp.Cli?style=flat-square)](https://www.nuget.org/packages/Nullean.Cdp.Cli) |
| [`@nullean/journeys`](js/journeys/README.md) (npm) | the same journeys on Playwright Test and Elastic Synthetics | [![npm](https://img.shields.io/npm/v/@nullean/journeys?style=flat-square)](https://www.npmjs.com/package/@nullean/journeys) |

## Learn more

- Documentation: https://cdp.nullean.net
- [ROADMAP.md](ROADMAP.md) and [DECISIONS.md](DECISIONS.md)
- [Releases](https://github.com/nullean/cdp/releases)
- [Contributing](https://github.com/nullean/.github/blob/main/CONTRIBUTING.md)

MIT licensed, see [LICENSE.txt](LICENSE.txt). Copyright Nullean and contributors.
