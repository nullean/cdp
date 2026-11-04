# Locators and actions

Locators are agent-browser's: find elements the way a user would describe them.

```csharp
page.Role("button", "Add to basket")       // role and accessible name
page.Text("3 results")                     // innermost elements holding the text
page.Label("Search")                       // the control a label names
page.Placeholder("Find a product")
page.Alt("A product photo")
page.Title("Close")
page.TestId("go")                          // data-testid, exact
page.Css("form .basket-count")
page.Css("li").Nth(2)                      // also First and Last
```

- Text matching is a case-insensitive substring with whitespace collapsed; pass `exact: true` to match the whole text,
  case-sensitively. A role always ignores case.
- A label locator finds the control of a `<label>`, then elements named by `aria-label` or `aria-labelledby`.
- A selector that does not parse is an error at once, not a timeout.

## Actions wait

```csharp
await page.Label("Search").FillAsync("lamp");
await page.Role("checkbox", "Only in stock").CheckAsync();
await page.Role("button", "Go").ClickAsync();
await page.Text("3 results").WaitAsync();
```

Every action polls until its element is ready, up to `page.ActionTimeout` (5 s): visible and enabled for clicks, fills
and keys, visible for hover, attached for reads. A timeout names the locator and says what it saw: no match, matches
that are not visible, or visible ones that are disabled.

| Locator | |
|---|---|
| `ClickAsync`, `DoubleClickAsync`, `HoverAsync`, `FocusAsync` | real mouse input at the element's centre, scrolled into view |
| `FillAsync(text)`, `TypeAsync(text)`, `PressAsync(key)` | replace the value, type after it, press a key |
| `CheckAsync`, `UncheckAsync` | click only when the state differs; fail if it did not change |
| `SelectAsync(values)` | by value, then label; fires `input` and `change` |
| `UploadAsync(files)`, `DragToAsync(target)`, `ScrollIntoViewAsync`, `ScrollByAsync` | |
| `TextAsync`, `HtmlAsync`, `ValueAsync`, `BoxAsync`, `StylesAsync`, `CountAsync` | reads |
| `IsVisibleAsync`, `IsEnabledAsync`, `IsCheckedAsync` | now, without waiting |
| `WaitAsync(state)` | until visible, or with `LocatorState.Attached` until it is in the document |
| `ScreenshotAsync()` | a PNG of the element |

## Navigation after an action

A click or key press that starts a navigation waits for the new document's `load`; a navigation within the document
returns at once, and a cancelled one or a download stops the wait. Nothing started, nothing waited for.
