# Your first journey

A journey is a text file with one command per line, written as you would type it to agent-browser.

```text
# about.journey: from the shop to the about page
open /
find label Search fill lamp
find role link click --name "About us"
snapshot -c -s main
links
a11y
```

- Lines that act (`open`, `find … click`, `fill`) run in order and wait for their element.
- Lines that read (`snapshot`, `links`, `a11y`) are **checkpoints**: their output is the journey's transcript.
- `#` followed by a space starts a comment; `#id` is still a CSS selector.

## Check it

```sh
cdp check about.journey
```

`check` parses without starting a browser and reports every error with its line, so a journey written by an agent is
fixed in one pass.

## Run it

```sh
cdp run about.journey --base-url http://localhost:5000/
```

```text
-- about.journey:5: snapshot -c -s main
--- PAGE_CONTENT nonce=1c7f…824d origin=http://localhost:5000 ---
url: http://localhost:5000/about.html
- main
  - heading "About us" [level=1]
  - paragraph
    - StaticText "We sell fixtures."
  - link "Back home"
--- END_PAGE_CONTENT nonce=1c7f…824d ---
-- about.journey:6: links
links: 1 checked, 0 broken
-- about.journey:7: a11y
violations: 0
ok about.journey: 6 steps, 3 checkpoints
```

`open /` resolves against the base URL. For local files, give the folder: `--base-url ./site`. The run may reach only
the base URL's host; see [safety](../journeys/safety.md) for adding others.

## Run it from code

```csharp
using Nullean.Cdp.Journeys;

var journey = Journey.Load("journeys/about.journey");
var result = await JourneyRunner.RunAsync(journey, browser, new JourneyOptions { BaseUrl = new("http://localhost:5000/") });

foreach (var checkpoint in result.Checkpoints)
    Console.WriteLine($"{checkpoint.Command}\n{checkpoint.Output}");
if (result.Failure is { } failure)
    Console.WriteLine(failure.Describe(result.Source));   // about.journey:4: find role link ... and why
```

A failing step is a result, not an exception: the run stops there and keeps the checkpoints that ran. The failure also
carries the page's address (`failure.Url`) and a PNG of the viewport (`failure.Screenshot`, written to `failure.png` in
the artifacts folder when `JourneyOptions.ArtifactsDirectory` is set). `Describe` adds both as `Page:` and `Screenshot:`
lines. Set `ScreenshotOnFailure = false` to skip the capture.

## Replay it in agent-browser

```sh
cdp batch about.journey --base-url http://localhost:5000/ | agent-browser batch --bail
```
