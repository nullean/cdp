# Assertions

Web-first assertions in the library, after [Playwright's](https://playwright.dev/docs/test-assertions): an assertion
checks the page again and again until it holds or the timeout passes. A journey's [`expect`](../journeys/commands.md#expect)
line is the same engine.

```csharp
await page.Css("#count").ExpectAsync(Matcher.Text("2 results"));
await page.Role("button", "Save").ExpectAsync(Matcher.Enabled, new ExpectOptions { Timeout = TimeSpan.FromSeconds(10) });
await page.Css(".spinner").ExpectAsync(Matcher.Visible, new ExpectOptions { Negate = true });
await page.ExpectAsync(Matcher.Url("**/checkout"));
```

`ExpectAsync` throws an `ExpectationException` (a `TimeoutException`) carrying the result. `CheckAsync` takes the same
arguments and returns the `ExpectationResult` without throwing, which is what a test framework adapter reports:

```csharp
ExpectationResult result = await page.Css("#count").CheckAsync(Matcher.Text("3"), new ExpectOptions { Timeout = TimeSpan.FromSeconds(1) });
result.Passed;    // false
result.Message;   // Timed out after 1s waiting for css #count to have text "3": received "2 results".
result.Received;  // "2 results"
```

## Matchers

A `Matcher` is data: what to check, and for text how (`TextMatch.Exact`, `Contains` or `Regex`). The same value is built
by a journey line, by code and by an adapter.

| Matcher | Holds when |
|---|---|
| `Visible`, `Hidden` | the first match is visible; `Hidden` also holds when nothing matches |
| `Enabled`, `Disabled`, `Checked`, `Unchecked` | the state; `Disabled` and `Unchecked` need an element |
| `Focused`, `Editable`, `Attached`, `Empty` | focus, input, in the document, no value or text |
| `Text(text)`, `Value(text)` | the whole text (whitespace collapsed) or value; `Contains`, `Regex` |
| `Attribute(name, [text])` | the attribute is present, or has the value |
| `Class(classes)` | the whole `class` attribute; `Contains`: every class listed |
| `Count(n)` | exactly `n` matches |
| `Title(text)`, `Url(glob)` | on a `Page`: `page.ExpectAsync(...)` |

`ExpectOptions` holds the `Timeout` (the page's `ActionTimeout`, 5 s, by default), `Negate`, `IgnoreCase`, `UseInnerText`
for `Text`, and the `PollInterval`. A negated matcher keeps checking until it stops holding.

## With a test framework

The core package depends on no test framework. Because `CheckAsync` returns a result, an adapter only has to report it:
build a `Matcher`, call `CheckAsync`, and fail the framework's assertion with `result.Message`. The Nullean.Cdp tests
include a small AwesomeAssertions adapter (`await page.Css("#count").Should().HaveTextAsync("2 results")`) as proof that
`because` reasons and `AssertionScope` behave. Pass the message to the framework as an argument, never as its format
string: a selector or page text can hold `{braces}`.
