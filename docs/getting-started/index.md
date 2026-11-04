# Getting started

## Install

```sh
dotnet add package Nullean.Cdp              # the library
dotnet add package Nullean.Cdp.Journeys     # journeys, if you run them from code
dotnet tool install --global Nullean.Cdp.Cli
```

The tool's command is `cdp`. Without installing it, `dnx Nullean.Cdp.Cli <command>` runs it once.

## Chrome

Nullean.Cdp drives a Chrome you already have: Chrome, Chrome for Testing, Chromium or Edge. Check which one it finds:

```sh
cdp chrome
```

Set `CDP_CHROME` (or `CHROME_PATH`) to use a particular one. [Finding Chrome](../reference/chrome.md) lists every
place it looks. GitHub's hosted runners have Chrome installed.

## Drive a page

```csharp
using Nullean.Cdp;

await using var browser = await Browser.LaunchAsync();      // headless, temporary profile
await using var context = await browser.NewContextAsync();  // its own cookies and storage
var page = await context.NewPageAsync();
await page.GotoAsync("http://localhost:5000/");

Console.Write(await page.SnapshotAsync(new AxSnapshotOptions { Interactive = true, Selector = "main" }));
// - heading "Fixture shop" [level=1]
// - textbox "Search"
// - checkbox "Only in stock" [checked=true]
// - combobox "Sort" [expanded=false]: Name
// ...

await page.Label("Search").FillAsync("lamp");
await page.Role("checkbox", "Only in stock").UncheckAsync();
await page.Role("button", "Go").ClickAsync();               // waits for any navigation it starts
```

Disposing the browser closes Chrome and deletes its profile. One browser serves many contexts at once, so parallel
tests can share it.

Next: [your first journey](first-journey.md).
