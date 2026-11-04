# Performance

Raw readings in the library and the CLI; budgets in journeys, so transcripts stay stable.

## Timings

```csharp
var t = await page.TimingsAsync();
foreach (var (name, value) in t.Named)
    Console.WriteLine($"{name}: {value}");
```

`ttfb`, `first-paint`, `fcp`, `dom-interactive`, `dom-content-loaded` and `load`, in milliseconds from the start of the
navigation, from the page's navigation and paint entries. Chrome reports a paint once its frame is rendered, which on a
machine without a GPU can come after `load`; `TimingsAsync` waits for it only when the page has loaded and reported no
paint yet.

## Web Vitals

```csharp
await page.RecordInteractionsAsync();   // before the page loads, for INP
await page.GotoAsync(url);
// … interact …
var v = await page.VitalsAsync();
Console.WriteLine($"LCP {v.LargestContentfulPaint} ms on {v.LargestContentfulPaintElement}, CLS {v.CumulativeLayoutShift}, INP {v.InteractionToNextPaint}");
```

| Vital | |
|---|---|
| `ttfb`, `fcp` | as in timings |
| `lcp` | the largest contentful paint, with its element |
| `cls` | cumulative layout shift in session windows: gaps under 1 s, at most 5 s long |
| `inp` | the slowest interaction, or the 98th percentile from 50 interactions on |

Chrome keeps only interactions of about 104 ms or more. `RecordInteractionsAsync` adds a small script before any page
script that records them from 16 ms, as Google's web-vitals library does. It is opt-in; a journey turns it on only
when it uses `vitals`. Vitals wait two frames for entries to arrive, so read them a moment after the change you want
measured.

## Metrics

```csharp
var metrics = await page.MetricsAsync();   // Nodes, JSEventListeners, LayoutCount, ScriptDuration, JSHeapUsedSize, …
```

Chrome's `Performance.getMetrics`, as Chrome names and scales them.

## In journeys

```text
timings                                  # raw: changes every run
vitals --budget lcp=2500 cls=0.1 inp=200
metrics --budget Nodes=1500
```

With budgets the checkpoint prints only `lcp ok (<= 2500)` for each one, and a reading over its limit, or with no value
yet, fails the step with the actual value. agent-browser has `vitals` but not `timings` or `metrics`; the export keeps
`vitals` without budgets.
