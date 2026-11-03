# Decisions

Non-obvious choices, newest last. Each says what was chosen, and why when the why is not obvious.

## Fixed at the start

- Name `Nullean.Cdp`, MIT, .NET 10. The CLI's command name is still open.
- **Direct CDP, not a wrapper.** agent-browser and Playwright both drive Chrome over CDP. Talking CDP ourselves means
  no Node, no daemon and no third party's JSON output to depend on, at the cost of owning Chrome discovery and
  waiting. It started as the driver for Nullean.Snapshots' browser journeys and lives on its own because it is useful
  without snapshots.
- **agent-browser's command language.** Journeys use a subset of agent-browser's commands, so agents can explore with
  agent-browser and write down what matters, and a journey exports to agent-browser's batch JSON to replay there.
- **No dependencies beyond the BCL** in `Nullean.Cdp`, and every library is AOT and trim clean.

## Finding Chrome

`CDP_CHROME`, `CHROME_PATH`, the install locations for the OS (Chrome, Chrome for Testing, Chromium, Edge; on macOS
`/Applications` then `~/Applications`), then the `PATH`. The first existing file wins. The locator takes the
environment, a file-exists check and the OS as parameters, so every OS's search is tested on every OS; paths are
joined with the target OS's separator, not the host's.

## Connection

- **One web socket, flattened sessions.** Every page's commands carry its `sessionId` over the browser's one
  connection (`Target.attachToTarget` with `flatten`), so there is one receive loop and no per-page socket.
- **No locks.** Ids come from `Interlocked.Increment`; replies are matched through a
  `ConcurrentDictionary<int, TaskCompletionSource>`; listeners live in a concurrent dictionary. A web socket allows
  one send at a time, so sends go through a channel drained by one writer.
- **Closing fails everything waiting.** When the connection closes, pending commands and event waits fail with
  `CdpDisconnectedException`, and so does every later send. A waiter registers before it checks for a close, and the
  receive loop marks the close before it fails the registered waiters, so a close racing with a new wait is always
  seen by one side.
- **Event waits start listening when called**, so the caller creates the wait before the command that causes the
  event (`Page.navigate` before `Page.loadEventFired`).
- Results are `JsonElement` clones read with `JsonDocument`; commands are written with `Utf8JsonWriter`. No
  serializer, no reflection.
- Tests drive the connection through an in-memory transport (`ICdpTransport`), not a socket server: the socket is a
  thin adapter, and the routing, events and close handling are what need testing.

## Launching Chrome

- `--remote-debugging-port=0` and a temporary `--user-data-dir`; the port and browser path are read from the
  `DevToolsActivePort` file Chrome writes there, so no port is guessed or raced for. A half-written file reads as not
  ready.
- Chrome's output is drained (it blocks on a full pipe) and the last 20 lines are kept for the error when it exits or
  never opens its port.
- Disposing sends `Browser.close`, waits five seconds, then kills the process tree, and deletes the profile with
  retries, since Chrome's helpers hold files for a moment on Windows.
- **One browser, a context per caller.** `Target.createBrowserContext` keeps cookies and storage apart, so tests share
  one Chrome per test session (the `ChromeFixture`) and still run in parallel.

## Navigation

- `GotoAsync` waits for the `load` lifecycle event whose frame and loader are the ones `Page.navigate` returned, not
  for the next load event on the page, so an earlier document's event can never end the wait. Loads are recorded from
  before the command is sent, since the event can arrive before the reply. A fragment navigation has no loader and
  returns at once; a closed connection ends the wait with `CdpDisconnectedException`, the timeout with
  `TimeoutException`.
- The test for it uses a local `HttpListener` whose image answers 800 ms late: on a `file://` page `load` comes too
  fast to tell waiting from not waiting.

## Accessibility tree

Printed the way agent-browser's `snapshot` prints it, without refs: `- role "name" [level=1, checked=true,
selected]: value`, two spaces per level. Checked against agent-browser 0.41.0 on `site/index.html`: the full tree,
`-i` and `-d` match line for line once refs are removed.

- **Lifted:** ignored nodes, the root, and unnamed `generic` and `none` nodes are replaced by their children.
  Inline text boxes are dropped. A named node's static text children are dropped, since the name already says them.
  (agent-browser keeps one: the text under a `<summary>`'s disclosure triangle.)
- **States** print in a fixed order: `level`, `checked`, `pressed`, `expanded` as `name=value`; `selected`,
  `disabled`, `required` bare when true and left out when false. Other states, such as `focusable`, are not printed.
- **Compact (`-c`) is ours, not agent-browser's.** It drops list markers, line breaks and nodes left with no name,
  value, state or children. agent-browser's `-c` also drops some named nodes (an image with alt text, a `details`
  group) by rules it does not document; we keep anything with a name.
- **Interactive (`-i`)** keeps controls, headings and named landmarks, lifting everything else, as agent-browser does.
- **Golden tests run on recorded trees** (`fixtures/*.axtree.json`, recorded by the explicit `RecordFixtures` test),
  so a new Chrome cannot change them. Tests against a live Chrome assert individual lines.

## Locators and actions

- **agent-browser's locators:** CSS, role (with an optional accessible name), text, label, placeholder, alt, title,
  test id. The role itself always ignores case, as in agent-browser. Text compares with whitespace collapsed, as a case-insensitive substring, or whole and case-sensitive with
  `exact`; test ids always match exactly. A text locator matches the innermost elements holding the text. A label
  locator finds the control of a `<label>`, then elements named by `aria-label` or `aria-labelledby`, in document
  order.
- **Role lookups read the whole accessibility tree** (`getFullAXTree`) and filter it, instead of
  `Accessibility.queryAXTree`. With about 20 pages open at once in one Chrome 156, `queryAXTree` on some pages never
  answered (traced locally: the command was sent and no reply came within ten seconds); run one at a time it always
  answered. `getFullAXTree` stayed reliable under the same load, and is already what snapshots use. The cost is one
  full tree per poll, which is fine at test-page sizes.
- **Every action waits** up to `Page.ActionTimeout` (5 s) by polling every 50 ms: clicks, fills and key presses for a
  visible, enabled match, hover for a visible one, reads for an attached one. A timeout names the locator as
  agent-browser writes it and says what it saw: no match, matches that are not visible, or visible ones that are
  disabled. A selector that does not parse is an error at once, not a timeout.
- **Real input.** Clicks scroll the element into view and press at the centre of its first content quad with area;
  fill selects the content and uses `Input.insertText`; typing and keys go through `Input.dispatchKeyEvent`. With
  Control, Alt or Meta held a key types nothing; with Shift a letter is upper case in both `key` and text. `check`
  clicks only when the state differs and fails if the click did not change it. `select` matches option values first,
  then labels, and fires `input` and `change`.
- **Navigation after an action.** Clicks and key presses watch the main frame (whose id is the target id) from before
  they act. If a navigation was requested, they wait for the committed document's `load`, return at once for a
  navigation within the document, and stop waiting if loading stops without a new document (a cancelled navigation or
  a download). Nothing requested, nothing waited for.
- **Not missing a navigation.** Chrome can acknowledge the mouse release before announcing the navigation the click
  caused: under parallel load, clicking a link sometimes returned while the next page was still `interactive` (2 of 3
  full runs). Two things guard against it: `Page.frameScheduledNavigation` counts as a start (a link click sends it
  first), and when no start has been seen, one `Runtime.evaluate` round trip to the page runs before deciding there
  was none. Without either the test failed 1 run in 4; with either alone, 0 in 4; with both, 0 in 6. Both are kept.
- **Element handles** are released after every operation, in an object group per locator, so locators acting on one
  page at the same time never release each other's handles.

## Journeys

- **agent-browser's commands, a subset.** A journey is one command per line, written as for the agent-browser CLI,
  so an agent that explored with agent-browser writes the steps down as they were. The subset is what a repeatable
  test needs: navigation, actions, `find` with click, fill, check, hover and text, waits, viewport and colour scheme,
  and the reads `snapshot`, `get`, `is`. Everything else (screenshots, tabs, cookies, `connect`, `eval`, `find nth`,
  `wait --url`, ...) is an error naming the line, so a shared journey cannot reach outside the page it tests.
- **Refs are rejected.** `@eN` comes from a snapshot and changes between runs.
- **Quoting is a shell's**, since journeys are copied from shell commands. One change: `#` starts a comment only when
  whitespace or the line end follows it, because CSS id selectors start with `#`; a shell would need them quoted.
- **Every parse error at once**, each with its line, so an agent fixes a journey in one pass.
- **Checkpoints** are the reads. Each records the line, the command as one normalised line (no comment, single
  spaces, quoted only where needed), the URL, and the output. The normalised command is what a transcript shows, so
  re-spacing or re-commenting a journey changes nothing downstream.
- **A failing step is a result, not an exception**: the run stops, and the result keeps the checkpoints that ran and
  the failure (`file:line: command`, then the error). Cancelling the run is still thrown.
- **URLs** are absolute only when they start with a scheme: on Linux and macOS `Uri` also reads `/shop` as
  `file:///shop`. Relative ones resolve against the base URL as in a browser, so a folder needs its trailing slash.
- **Batch export** writes agent-browser's batch JSON with relative URLs resolved, and `\"` rather than `\u0022`.
- **Checked against agent-browser 0.41.0 by hand:** `site/journeys/everything.journey`, which uses every supported
  command, was exported and run with `agent-browser batch --bail`: all 40 steps succeeded, and its reads are the
  values `JourneyRunnerTests` expects from this runner.

## Tests

- Every test has a 60 second timeout (assembly level): mutating reply routing showed a lost reply hangs the run.
- Navigation tests that need `load` to come late use a local `HttpListener` with an image that answers after 800 ms:
  mutation showed `file://` pages load so fast that not waiting at all still passed.
- Fixtures that tests read live under `site/`, which is copied to the output. A separate `journeys/` folder clashed
  with the `Journeys/` test folder on macOS's case-insensitive file system, and would not have been copied on Linux.
- A journey that reads a page must not race it: `everything.journey` waits for the late button before counting.
- Tests do not bound how long an action takes; under parallel load that is not a property of the code. They assert
  instead that no navigation wait started, by lowering `NavigationTimeout` (to 10 s: 2 s was too tight on the macOS
  runner) so a wrong wait fails with its message.
- Two mutants survive and are equivalent: Chrome types nothing for a Control chord even when text is sent, and a
  same-document navigation is settled by `navigatedWithinDocument` and again by `frameStoppedLoading` (removing both
  fails the suite).

## Repository

- Laid out like nullean/snapshots: central package management, MinVer from tags (`0.1` minimum, `alpha.0`
  prereleases), TUnit on Microsoft.Testing.Platform, CI on linux, windows and macos.
