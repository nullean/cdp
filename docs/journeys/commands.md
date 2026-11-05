# Command reference

Every command is written as for agent-browser. A **checkpoint** records output; the others act. A selector is CSS
unless the command is `find`.

## Navigate

| Command | |
|---|---|
| `open <url>` | go to a URL (also `goto`, `navigate`) and wait for `load`; relative URLs resolve against the base URL |
| `back`, `forward`, `reload` | history and reload, waiting for `load` |

## Act

Every action waits up to the action timeout (5 s) for its element: visible and enabled for input, visible for
`hover`. An action that starts a navigation waits for the new page's `load`.

| Command | |
|---|---|
| `click <sel>`, `dblclick <sel>` | scroll into view and press at the element's centre |
| `hover <sel>`, `focus <sel>`, `scrollintoview <sel>` | |
| `fill <sel> <text>` | replace the value |
| `type <sel> <text>` | type key by key, after what is there |
| `check <sel>`, `uncheck <sel>` | click only when the state differs, and fail if it did not change |
| `select <sel> <value...>` | by option value, then by label; fires `input` and `change` |
| `upload <sel> <files...>` | paths relative to the journey's folder, which they may not leave |
| `drag <source> <target>` | mouse drag, and HTML drag and drop |
| `scroll [up\|down\|left\|right] [pixels] [-s <sel>]` | the page, or a scrollable element |
| `press <key>` | `Enter`, `Tab`, `Control+a`, … |
| `keyboard type <text>`, `keyboard inserttext <text>` | to whatever has focus |

## Find

```text
find role|text|label|placeholder|alt|title|testid <value> [click|fill|check|hover|text] [text] [--name <name>] [--exact]
find first|last <sel> [action] [text]
find nth <n> <sel> [action] [text]
```

- `role` matches the accessibility role; `--name` its accessible name.
- `text`, `label`, `placeholder`, `alt` and `title` compare as a case-insensitive substring with whitespace collapsed,
  or whole and case-sensitive with `--exact`. `testid` matches `data-testid` exactly.
- Without an action, `find` clicks, as agent-browser's does. `find … text` is a checkpoint: the element's text.

## Wait and set

| Command | |
|---|---|
| `wait <sel>` | until the element is visible |
| `wait <ms>` | a fixed pause |
| `wait --text <text>` | until the page shows the text |
| `wait --url <glob>` | until the URL matches: `**` anything, `*` anything but `/` |
| `wait --load load\|domcontentloaded\|networkidle` | the current document's load state |
| `set viewport <w> <h> [scale]` | the viewport size, and device scale factor |
| `set media dark\|light` | `prefers-color-scheme` |
| `set device <name>` | one of twelve devices by their Playwright names (`iPhone 14`, `Pixel 7`, …) |
| `set geo <lat> <lng>` | geolocation, with permission granted |
| `set offline [on\|off]` | without on or off, it toggles |
| `set headers <json>` | extra request headers, as a JSON object |
| `set credentials <user> <password>` | HTTP authentication; the password must be a `$env:` reference |

## Read

All checkpoints.

| Command | Output |
|---|---|
| `snapshot [-i] [-c] [-u] [-d <n>] [-s <sel>]` | the accessibility tree: `-i` interactive only, `-c` compact, `-u` link targets, `-d` depth, `-s` one element; see [reading](../library/reading.md) |
| `get text\|html\|value\|count\|box\|styles <sel>` | the element's text, outer HTML, value, number of matches, box, computed styles |
| `get attr <sel> <name>` | an attribute |
| `get title`, `get url` | |
| `is visible\|enabled\|checked <sel>` | `true` or `false`, without waiting |
| `console [--clear]` | console messages since the page opened, `[log] text`; `--clear` forgets them and prints nothing |
| `errors [--clear]` | uncaught errors, `message (url:line:column)` |

## Check

All checkpoints. See [checks](../library/checks.md) and [performance](../library/performance.md).

| Command | |
|---|---|
| `a11y [-s <sel>] [--strict]` | our own accessibility rules; `--strict` fails on any violation |
| `links [-s <sel>] [--external] [--all] [--strict]` | checks the page's links: same-site by default, `--external` for allowed other hosts, `--all` lists working ones too |
| `network requests [--filter <url>] [--type <types>] [--method <m>] [--status <code>] [--failed] [--strict]` | requests since the journey started, `GET url (Type) 200`; `--status` takes `200`, `2xx` or `400-499`; `--failed` is 4xx, 5xx and no answer |
| `network requests --clear` | forget the requests so far (not a checkpoint) |
| `timings [--budget <name>=<ms>...]` | `ttfb`, `first-paint`, `fcp`, `dom-interactive`, `dom-content-loaded`, `load` |
| `vitals [--budget <name>=<limit>...]` | `ttfb`, `fcp`, `lcp`, `cls`, `inp` |
| `metrics [--budget <name>=<limit>...]` | Chrome's runtime counters: `Nodes`, `JSHeapUsedSize`, `LayoutCount`, … |

## Expect

Assertions, after [Playwright's](https://playwright.dev/docs/test-assertions). An `expect` line checks again and again
until it holds or the timeout passes (the action timeout, 5 s, unless `--timeout <ms>`), then fails the step. A passing
assertion records nothing, so it changes no verified transcript. The target is a CSS selector, or `find` as above:
`expect visible find role button --name Save --exact`. The matcher comes first.

| Command | Holds when |
|---|---|
| `expect visible\|hidden <target>` | the first match is visible; `hidden` also holds when nothing matches |
| `expect enabled\|disabled <target>` | a match is not disabled (`disabled` attribute, `aria-disabled`, a disabled `fieldset`) or is |
| `expect checked\|unchecked <target>` | a checked checkbox, radio or `aria-checked` element, or a match that is not |
| `expect focused\|editable\|attached\|empty <target>` | it has focus; takes input; is in the document; has no value or text |
| `expect text <target> <text>` | the whole text, whitespace collapsed, case-sensitive; `--contains` for part of it; `--regex` for a pattern; `--ignore-case`; `--inner-text` reads what is rendered |
| `expect value <target> <text>` | an input's value, as it is |
| `expect attr <target> <name> [<text>]` | the attribute is there, or has that value |
| `expect class <target> <classes>` | the whole `class` attribute; with `--contains`, every class listed, in any order |
| `expect count <target> <n>` | exactly `n` matches (`0` when none) |
| `expect title <text>` | the page title |
| `expect url <glob>` | the URL, as `wait --url`: `**` is anything, `*` anything but `/`; `--regex` for a pattern |

`--not` inverts any of them, and keeps checking until the matcher stops holding. `--contains` and `--regex` apply to
`text`, `value`, `attr`, `class`, `title` and `url`; `--ignore-case` to `text`, `attr` and `url`. A negated matcher
about an element (`--not enabled`) holds when nothing matches; the positive `disabled` needs an element.

```text
expect visible find role heading --name "Your basket"
expect text #count "2 items"
expect --not visible .spinner
expect --timeout 10000 text "#status" "Done"
expect url "**/checkout"
```

A failure names what was expected and what the last check saw:

```text
Journey stopped at journeys/shop.journey:9: expect text #count "3 items"
Timed out after 5s waiting for css #count to have text "3 items": received "2 items".
```

A regular expression is .NET's in `cdp run` and JavaScript's on [Playwright](playwright.md); keep to the syntax they share,
and quote it: unquoted, a backslash escapes the next character (`'^\d+ items$'`). There are no flags: use `--ignore-case`.
`$env:NAME` works in an expected value and is redacted from messages. `expect` is ours: agent-browser has no
assertions, so [the export](agent-browser.md) leaves these lines out.

## Record

These write files, only inside the artifacts folder the host chose (`--artifacts`, or `JourneyOptions.ArtifactsDirectory`);
paths are relative to it and may not leave it. Without a folder they fail the step.

| Command | |
|---|---|
| `screenshot [sel] <path.png> [--full]` | the viewport, the whole page, or one element, as PNG |
| `network har start` | start a HAR recording |
| `network har stop <path>` | write it, with credentials redacted |

## Allow

| Command | |
|---|---|
| `allow <host pattern...>` | hosts this journey may reach: `shop.test`, `*.shop.test` (which also matches `shop.test`), or `*`; see [safety](safety.md) |
