# Nullean.Cdp

![Nullean.Cdp](https://raw.githubusercontent.com/nullean/cdp/main/docs/images/brand/png/lockup.png)

A small Chrome DevTools Protocol client for .NET: find and launch Chrome, drive pages with agent-browser style
locators, and read the accessibility tree. Native AOT and trim clean, with no dependencies beyond the BCL.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Cdp?style=flat-square)](https://www.nuget.org/packages/Nullean.Cdp)

## Install

```sh
dotnet add package Nullean.Cdp
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

## Finding Chrome

`CDP_CHROME`, then `CHROME_PATH`, then the standard install locations for Chrome, Chrome for Testing, Chromium and
Edge on each OS, then `google-chrome`, `chromium` and `chrome` on the `PATH`. When nothing is found, the error lists
every place it looked.

## Learn more

- Documentation: https://cdp.nullean.net, and [the library](https://cdp.nullean.net/library/)
- Synthetic journeys on top of this client: [Nullean.Cdp.Journeys](https://www.nuget.org/packages/Nullean.Cdp.Journeys)
- Source and issues: https://github.com/nullean/cdp
