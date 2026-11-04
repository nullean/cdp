# Reading a page

## The accessibility tree

```csharp
Console.Write(await page.SnapshotAsync(new AxSnapshotOptions { Interactive = true, Selector = "main" }));
```

```text
- heading "Fixture shop" [level=1]
- textbox "Search"
- checkbox "Only in stock" [checked=true]
- combobox "Sort" [expanded=false]: Name
  - option "Price"
  - option "Name" [selected]
- button "Go"
- button "Disabled" [disabled]
- heading "Section two" [level=2]
```

The tree prints as agent-browser's `snapshot` does, without refs: `- role "name" [states]: value`, two spaces per
level. Ignored nodes and unnamed `generic` nodes are replaced by their children. States print in a fixed order:
`level`, `checked`, `pressed`, `expanded` as `name=value`; `selected`, `disabled` and `required` when true.

| `AxSnapshotOptions` | Journey | |
|---|---|---|
| `Interactive` | `-i` | controls, headings and named landmarks only |
| `Compact` | `-c` | drop list markers, line breaks, and nodes with no name, value, state or children |
| `Urls` | `-u` | print link targets as `url=…` |
| `Depth` | `-d <n>` | levels below the top |
| `Selector` | `-s <sel>` | only the first matching element's subtree |

`page.AccessibilityTreeAsync()` returns the nodes instead of text.

## Console and errors

A page collects console messages and uncaught errors from the moment it is created.

```csharp
foreach (var message in page.ConsoleMessages) Console.WriteLine(message);   // [warn] Low stock
foreach (var error in page.Errors) Console.WriteLine(error);                // TypeError: x is undefined (http://…/app.js:12:5)
page.ClearConsole();
page.ClearErrors();
```

Chrome reports an unhandled promise rejection a little after the task that caused it, so read errors after a short
wait rather than straight after `open`.

## Screenshots

```csharp
byte[] viewport = await page.ScreenshotAsync();
byte[] whole = await page.ScreenshotAsync(fullPage: true);
byte[] box = await page.Css("#basket").ScreenshotAsync();
```

PNG only. Screenshots are files, not checkpoints: pixels differ with fonts, anti-aliasing and GPU, so a journey's
`screenshot` writes into the artifacts folder and the transcript does not mention it. Headless Chrome's default
viewport is 756×469; set one with `SetViewportAsync` (the CLI uses 1280×720).
