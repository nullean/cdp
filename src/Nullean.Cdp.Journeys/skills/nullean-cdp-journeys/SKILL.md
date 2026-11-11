---
name: nullean-cdp-journeys
description: >-
  Use when writing, parsing or running synthetic browser journeys from .NET: `.journey` text files with one
  agent-browser command per line, replayed against Chrome as a test or a monitor. Nullean.Cdp.Journeys parses them,
  runs them with Nullean.Cdp and exports them to agent-browser's batch JSON.
---

# Nullean.Cdp.Journeys

`using Nullean.Cdp; using Nullean.Cdp.Journeys;` A journey is a text file in agent-browser's command language; the
runner replays it and records the output of every checkpoint (`snapshot`, `get`, `is`, `console`, ...). For driving a
page from C# directly, use `Nullean.Cdp`'s locators instead.

```
# checkout.journey
open /shop
find label Search fill lamp
press Enter
wait --text "results"
find role button click --name "Add to basket"
snapshot -c -s main
```

## The calls you need

```csharp
var journey = Journey.Load("journeys/checkout.journey");  // throws JourneyParseException with every error and line

await using var browser = await Browser.LaunchAsync();
var result = await JourneyRunner.RunAsync(journey, browser, new JourneyOptions
{
    BaseUrl = new Uri("http://localhost:5000/"),
    ArtifactsDirectory = "artifacts",                     // screenshots and HAR files land here
    ScreenshotOnFailure = true,
});

foreach (var checkpoint in result.Checkpoints)
    Console.WriteLine($"{checkpoint.Line}: {checkpoint.Command}\n{checkpoint.Output}");
if (result.Failure is { } failure)
    Console.WriteLine(failure.Describe(result.Source));  // checkout.journey:5: find role button ... and why
```

Parse from a string, or export for agent-browser (`agent-browser batch --bail`):

```csharp
var parsed = Journey.Parse("open /\nsnapshot -i", "inline.journey");
string batchJson = parsed.ToAgentBrowserBatch(new Uri("http://localhost:5000/"));
```

## Gotchas

- Never use agent-browser `@e3` refs: they change between runs. Use `find role ... --name`, `find text`,
  `find label` or a CSS selector.
- `open /shop` resolves against `BaseUrl` like a link would, so a base URL for a folder needs its trailing slash.
- The run only reaches the base URL's host plus the journey's `allow` lines and `JourneyOptions.AllowedHosts`;
  anything else is refused (`result.Blocked`, or a failed step for a navigation). Do not switch `GuardNetwork` off to
  make a journey pass: add an `allow` line.
- `ReadOnly = true` refuses every request but GET, HEAD and OPTIONS, for runs against a live site.
- Passwords are `$env:NAME` references, resolved through `JourneyOptions.Secrets` and redacted from all output. Never
  write a secret into a journey.
- A run does not throw on a failed step: check `result.Passed`.
- `Journey.SupportedCommands` lists what parses; anything else is an error naming its line.

Docs: https://cdp.nullean.net
