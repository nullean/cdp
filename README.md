# Nullean.Cdp

A small Chrome DevTools Protocol client for .NET: find and launch Chrome, drive pages with agent-browser style
locators, and read the accessibility tree. Native AOT and trim clean, with no dependencies beyond the BCL.

It is the browser underneath *synthetic journeys*: plain text files written in agent-browser's command language that
replay in agent-browser, in `dotnet test` (with [Nullean.Snapshots](https://github.com/nullean/snapshots)) and from
a command line tool.

```
# Get started from the landing page
open /
find role link click --name "Get started"
wait main
snapshot -c -s main
```

Status: early, 0.x. See [ROADMAP.md](ROADMAP.md).

## Use

```csharp
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
var journey = Journey.Load("journeys/checkout.journey");
var result = await JourneyRunner.RunAsync(journey, browser, new JourneyOptions { BaseUrl = new("http://localhost:5000/") });
foreach (var checkpoint in result.Checkpoints)
    Console.WriteLine($"{checkpoint.Command}\n{checkpoint.Output}");
if (result.Failure is { } failure)
    Console.WriteLine(failure.Describe(result.Source));   // checkout.journey:5: find role button ... and why
```

- **The commands** are a subset of agent-browser's, written the same way: `open`, `back`, `forward`, `reload`,
  `click`, `hover`, `fill`, `type`, `check`, `uncheck`, `select`, `press`, `find`, `wait`, `set viewport`,
  `set media`, and the checkpoints `snapshot`, `get` and `is` (and `find … text`). Anything else is an error that
  names the line.
- **No refs.** `@e3` changes from run to run, so journeys use `find role … --name`, `find text`, `find label` or CSS.
- **Quoting** is a shell's: double or single quotes, backslash escapes. `# ` starts a comment, but `#id` is a selector.
- **URLs:** `open /shop` resolves against the base URL like a link in a page would, so a base URL for a folder needs
  its trailing slash.
- **Safe to share:** a run reaches only the base URL's host and the hosts in the journey's `allow` lines, on every
  request including clicks, redirects and popups; `file://` stays in the base folder; downloads are denied. Passwords
  are `$env:NAME` references, typed when the step runs and redacted from everything the run reports.
- **Replay in agent-browser:** `journey.ToAgentBrowserBatch(baseUrl)` gives the batch JSON, for
  `agent-browser batch --bail`. A journey using every supported command runs there unchanged.

## Finding Chrome

`CDP_CHROME`, then `CHROME_PATH`, then the standard install locations for Chrome, Chrome for Testing, Chromium and
Edge on each OS, then `google-chrome`, `chromium` and `chrome` on the `PATH`. When nothing is found, the error lists
every place it looked.
