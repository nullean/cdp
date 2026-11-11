---
name: nullean-cdp
description: >-
  Use when .NET code needs to drive a real Chrome: open pages, click and fill like a user, assert on what the page
  shows, read its accessibility tree or take screenshots. Nullean.Cdp is a small, AOT-clean DevTools Protocol client
  with agent-browser style locators and no dependencies.
---

# Nullean.Cdp

`using Nullean.Cdp;` (plus `Nullean.Cdp.Accessibility` for snapshot options). One `Browser` (a Chrome process), a
`BrowserContext` per test (its own cookies and storage), and `Page`s in it. Not a Playwright port: one engine (Chrome
or Edge), no cross-browser API.

## The calls you need

```csharp
await using var browser = await Browser.LaunchAsync();      // headless, temporary profile; finds Chrome itself
await using var context = await browser.NewContextAsync();
var page = await context.NewPageAsync();
await page.GotoAsync("http://localhost:5000/");             // waits for the load event

await page.Label("Search").FillAsync("lamp");
await page.Role("checkbox", "Only in stock").CheckAsync();
await page.Role("button", "Go").ClickAsync();               // waits for any navigation the click starts
await page.Text("3 results").WaitAsync();
```

Assertions poll until they hold or time out, and throw `ExpectationException` with what they last saw:

```csharp
await page.Css("#count").ExpectAsync(Matcher.Text("3"));
await page.Role("alert").ExpectAsync(Matcher.Visible, new ExpectOptions { Negate = true });
await page.ExpectAsync(Matcher.Url("**/results*"));
var check = await page.Css("h1").CheckAsync(Matcher.Text("Shop", TextMatch.Contains)); // no throw: check.Passed
```

Reading the page:

```csharp
string tree = await page.SnapshotAsync(new AxSnapshotOptions { Compact = true, Selector = "main" });
string title = await page.Css("h1").TextAsync();
byte[] png = await page.ScreenshotAsync(fullPage: true);
var metrics = await page.SendAsync("Page.getLayoutMetrics");  // any CDP command the library does not wrap
```

## Gotchas

- Every locator call waits up to `page.ActionTimeout` (5 s) and throws `LocatorTimeoutException` saying how many
  elements matched and why none was usable. Raise the timeout on the page; do not add `Task.Delay`.
- Locators are not strict: an action uses the first match that is visible and enabled. Narrow with `Role(role, name)`,
  `.First`, `.Last` or `.Nth(i)` rather than relying on document order.
- `Text`, `Label` and the other text locators match a case-insensitive substring by default; pass `exact: true` for
  the whole, case-sensitive text.
- Dispose the browser (`await using`): it closes Chrome and deletes the temporary profile.
- No Chrome found throws `BrowserLaunchException` listing every place it looked. Point `CDP_CHROME` at an executable.
- Contexts are isolated, so tests can share one `Browser` and still run in parallel: one context per test.

Docs: https://cdp.nullean.net
