# The library

`Nullean.Cdp` talks the DevTools Protocol to Chrome over one web socket. It has no dependencies beyond the BCL and is
native AOT and trim clean.

## Browser

```csharp
await using var browser = await Browser.LaunchAsync(new BrowserLaunchOptions
{
    Headless = true,                       // the default
    ExecutablePath = null,                 // or a Chrome to use instead of the one found
    Arguments = ["--lang=nl"],             // added to the launch switches
    StartTimeout = TimeSpan.FromSeconds(30),
});
Console.WriteLine(await browser.VersionAsync());
```

- **Launching** uses a temporary profile and `--remote-debugging-port=0`, and reads the port Chrome chose from the
  profile, so no port is guessed. Background pages run at full speed, and headless scrollbars are hidden so pages lay
  out alike on every OS. If Chrome exits or never opens its port, `BrowserLaunchException` carries its last output.
- **Disposing** closes Chrome, kills what is left after five seconds, and deletes the profile. A process that exits
  normally closes every Chrome it launched; the next launch cleans up after one that was killed. See
  [Chrome](../reference/chrome.md).
- **Connecting** to a Chrome that is already running: `Browser.ConnectAsync(new Uri("ws://127.0.0.1:9222/devtools/browser/…"))`.

## Contexts and pages

```csharp
await using var context = await browser.NewContextAsync();   // its own cookies, storage and cache
var page = await context.NewPageAsync();
await page.GotoAsync("http://localhost:5000/");               // waits for this document's load event
```

Contexts are isolated from each other, so tests share one browser and still run in parallel. Disposing a context closes
its pages. Downloads are denied in every context.

| Page | |
|---|---|
| `GotoAsync`, `BackAsync`, `ForwardAsync`, `ReloadAsync` | navigate, waiting for `load` |
| `WaitForUrlAsync(glob)`, `WaitForLoadStateAsync(state)` | |
| `SetViewportAsync`, `SetDeviceAsync`, `SetColorSchemeAsync`, `SetGeolocationAsync`, `SetOfflineAsync`, `SetExtraHeadersAsync` | emulation |
| `PressAsync(keys)`, `ScrollAsync(dx, dy)`, `Mouse`, `Keyboard` | input not tied to an element |
| `UrlAsync`, `TitleAsync`, `EvaluateAsync(expression)` | |
| `ActionTimeout` (5 s), `NavigationTimeout` (30 s) | how long actions and navigations wait |
| `GuardNetworkAsync(allow)` | refuse requests the predicate does not allow; see [safety](../journeys/safety.md) |

## Anything else

`page.SendAsync(method, parameters)` sends any DevTools command in the page's session, and `browser.Connection` reaches
the browser itself. Parameters are written with a `Utf8JsonWriter`; results come back as `JsonElement`.

```csharp
var metrics = await page.SendAsync("Page.getLayoutMetrics");
await page.SendAsync("Emulation.setCPUThrottlingRate", w => w.WriteNumber("rate", 4));
```
