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
- **Background pages run at full speed.** Every context opens its own window, so most pages are behind another or
  in the background. Chrome throttles those: they render no frames, and mouse input waits for one. With the safety
  work's popups in the suite, clicks took about five seconds each and a journey test hung past its minute. The
  launch switches `--disable-background-timer-throttling`, `--disable-backgrounding-occluded-windows`,
  `--disable-renderer-backgrounding`, `--disable-features=CalculateNativeWinOcclusion`, `--disable-hang-monitor`,
  `--disable-ipc-flooding-protection` and `--disable-component-extensions-with-background-pages` (a subset of
  Playwright's defaults, from memory) brought the journey test back to 7.4 s in five of five runs. They may also be
  why the macOS runner hung in the test step, where window occlusion is real; not verified.
- Chrome's output is drained (it blocks on a full pipe) and the last 20 lines are kept for the error when it exits or
  never opens its port.
- Disposing sends `Browser.close`, waits five seconds, then kills the process tree, and deletes the profile with
  retries, since Chrome's helpers hold files for a moment on Windows.
- **Chrome never outlives its owner for long.** A process that exits normally (or on Ctrl+C) kills every Chrome it
  launched and deletes its profile (`AppDomain.ProcessExit`). A process that is killed cannot clean up, so every
  profile holds a `nullean-cdp-owner` file with the owner's process id and start time, and each launch first closes
  the Chromes of profiles whose owner is gone and deletes them. They are closed with `Browser.close` through their own
  DevTools port, never by process id, so a reused id cannot hit an unrelated process; an id is the owner only with
  its start time. Profiles are deleted only after Chrome drops its profile lock (`SingletonLock`, or `lockfile` on
  Windows): deleting earlier left a folder Chrome recreated while shutting down. Profiles without an owner file are
  never touched. Measured: a test run killed with `kill -9` left 126 Chrome processes and 6 profiles; the next launch
  left none. The exit handler has no automated test (it would kill the suite's own browser); it was checked by hand.
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

## agent-browser parity

Every agent-browser command that acts on or reads the page in a repeatable way is a journey command:
`dblclick`, `focus`, `scrollintoview` (and `scrollinto`), `scroll [dir] [px] [-s sel]`, `drag`, `upload`,
`keyboard type|inserttext`, `find first|last|nth`, `wait --url <glob>`, `wait --load <state>`, `get box`,
`get styles`, `set viewport w h [scale]`, `set device`, `set geo`, `set offline [on|off]`, `set headers`,
`set credentials`, and `snapshot -u`. `site/journeys/more.journey` uses them; exported and run with agent-browser
0.41.0 all 37 steps succeeded, and `MoreCommandTests` expects the values it read.

- **Still left out, on purpose:** refs; `eval` and `wait --fn` (page script); cookies, storage, `state`, `connect`,
  `auth` (session state and credentials outside the journey); tabs, `screenshot`, `pdf`, `record`, `download` and
  `wait --download` (files and windows); `snapshot --delta`/`--full` (a checkpoint records the whole tree and the
  transcript diff shows the change); `get cdp-url`; `mouse` (raw coordinates); `set headers`'s cleared-for-new-tabs
  nuance.
- **Differences found:** agent-browser's `set geo` left a `file://` page without permission (`geo error 1`); this
  runner grants geolocation to the context, so the page reads the position. `get box` prints the same
  `x:      10` lines; `get styles` the same sorted `property: value` lines.
- **Uploads** are paths relative to the journey's folder and may not leave it (refused when parsed; checked again when
  run, which no parsed journey can reach). The batch export writes them as absolute paths, since agent-browser reads
  them from its working folder.
- **`set credentials`** needs a secret reference for the password; the guard answers HTTP authentication only for
  allowed URLs (a refused URL never reaches a server, so this is a second layer) and cancels challenges otherwise.
- **`set offline`** without on|off toggles, as agent-browser's does; the runner tracks the state from a journey's start.
- **`set device`** knows twelve devices by their Playwright names, with representative sizes and user agents, not
  Playwright's full catalogue.
- **`wait --load`** reads the main frame's lifecycle events for the current document (`load`, `DOMContentLoaded`,
  `networkIdle`); a new document clears them. `wait --url` takes a glob: `**` anything, `*` anything but `/`.
- **HTML drag and drop** needs the mouse moves to report the left button held; Chrome then hands the drag over
  (`Input.setInterceptDrags`) and it is dropped on the target. A plain mouse drag gets the same moves.

## Console and errors

`console` and `errors` are checkpoints, as agent-browser's commands of the same names; `--clear` forgets what was
collected and prints nothing. A page collects from the moment it is created (`Runtime.enable`; `consoleAPICalled` and
`exceptionThrown`). Console lines print as agent-browser prints them, `[type] text`, arguments as agent-browser
prints them (`{a: 2}`, `[1, "x"]`). agent-browser's text `errors` printed only an empty `✗` per error, so errors print one line
each: the first line of the error, then `(url:line:column)`, 1-based, from the top stack frame. For an inline event
handler Chrome gives the column within the compiled handler, not the HTML line. Chrome reports an unhandled promise
rejection a little after the task that caused it, so an `errors` read straight after `open` can miss one; wait first.

## Performance

- **Readings, raw in the library and the CLI.** `page.TimingsAsync()` gives paint and load timing from the
  navigation and paint entries: `ttfb`, `first-paint`, `fcp`, `dom-interactive`, `dom-content-loaded`, `load`, in ms
  from the start of the navigation. `page.VitalsAsync()` gives agent-browser's `vitals` names: `ttfb`, `fcp`, `lcp`
  (with its element), `cls` (session windows: gaps under 1 s, windows at most 5 s), `inp` (the slowest interaction, or
  the 98th percentile from 50 on). `page.MetricsAsync()` is Chrome's `Performance.getMetrics` as Chrome names and
  scales them. People set their own thresholds.
- **In journeys, budgets keep transcripts stable.** `timings`, `vitals` and `metrics` print raw readings, which change
  from run to run; `--budget name=limit` prints only `name ok (<= limit)`, and a reading over its limit, or with no
  value yet, fails the step with the actual value. Snapshot users budget; people and agents reading the CLI see raw
  numbers.
- **INP needs recording from the start.** Chrome buffers only interactions of about 104 ms or more, so every document
  gets a small script before any page script (`Page.addScriptToEvaluateOnNewDocument`) that records interactions
  from 16 ms, as Google's web-vitals library does.
- **Reported, not instant.** Paint entries, layout shifts and interactions are reported once their frame is rendered
  (on the GPU-less Linux runner first paint came after `load`). Timings wait two frames and up to 500 ms for a first
  paint.
  Vitals wait two frames (and up to 200 ms after an interaction), but a change made just before may still be missing;
  a journey waits a moment before reading. A change before the page's first frame is no layout shift at all (nothing
  painted moved): the test page shifts only after two frames, or under load its CLS read 0.
- **Export:** `vitals` is an agent-browser command and exports without our budgets; `timings` and `metrics` are not
  and are left out.

## Safety for agents

Journeys are written and shared by agents, and their output is read by agents, so a page is untrusted input in both
directions. Researched against agent-browser 0.41.0 (its `--content-boundaries`, `--allowed-domains`,
`--action-policy`, `--confirm-actions` and auth vault, all off by default) and current guidance (spotlighting, Anthropic's
prompt-injection research, Playwright MCP, Browser Use): marking content only mitigates; limiting what a run can reach
and where data can go is what holds.

- **Allow list, on by default.** A run may reach the base URL's host, the hosts in the journey's `allow` lines
  (`shop.test`, `*.shop.test` which also matches `shop.test`, or `*`) and hosts the runner adds. Ports are not part of
  it. `allow` is not an agent-browser command; the batch export leaves it out (pass `--allowed-domains` there).
- **Enforced on every request**, not only `open`: the `Fetch` domain holds each request of the page until the list
  allows it. A refused navigation of the page or of a popup fails the step, naming the host and the `allow` line that
  would permit it (agent-browser reports a blocked click as success). Refused sub-resources and frames are reported
  with the result and do not fail it, so a third-party pixel does not break a journey.
- **Popups.** `--block-new-web-contents` did not stop `window.open` under `--headless=new` (it returned a window and a
  new target loaded). Instead the browser auto-attaches every new target paused (`Target.setAutoAttach` with
  `waitForDebuggerOnStart`): pages this library creates are set up and then resumed, a popup is handed to the guard of
  the page that opened it before it runs, and anything else is resumed at once. A page nobody claims within five
  seconds is resumed, so a browser this library connected to never keeps another client's tab paused. Frames that run
  in their own process are attached the same way by the page.
- **`file://` only under one folder**, the base URL's folder when it is a file URL or `JourneyOptions.FileRoot`. A
  journey cannot widen it: `open ../x` that leaves the folder is refused before navigating, and the guard refuses it too.
- **Secrets by reference.** `fill #pw $env:SHOP_PASSWORD` reads the value when the step runs (the environment, or the
  host's resolver). Every resolved value is replaced by its reference in checkpoints, URLs and failure messages, longest
  first. Values shorter than four characters are refused: redacting `abc` would mangle ordinary text. Filling a password
  field with a literal fails the step. The batch export refuses secret references unless given a resolver.
- **Downloads are denied** in every context (`Browser.setDownloadBehavior deny`); a test sees the download cancelled.
- **Page content fence.** `PageContent.Fence` wraps text from a page in `--- PAGE_CONTENT nonce=<128-bit> ---` …
  `--- END_PAGE_CONTENT nonce=<same> ---`, escapes lines inside that imitate a marker, and truncates with a visible note.
  The CLI will fence every page-derived output, titles and attributes included (agent-browser leaves those bare). It is
  a label, not a defence.
- **Hidden text:** the accessibility tree already drops `display:none` and `aria-hidden` content; off-screen, zero-size
  and low-contrast text still reaches it, as it does agent-browser's. Not claimed as a defence.
- **Not done:** confirmations before side effects (an interactive agent mode, later); a second network layer below the
  browser (`--host-resolver-rules`); dedicated and service workers' requests are not shown to be covered by the guard.

## CLI

- **The command is `cdp` for now**, a working name to be settled with the owner. It is set once, as the assembly name
  and `ToolCommandName` in `src/Nullean.Cdp.Cli/Nullean.Cdp.Cli.csproj`; the smoke script and CI name it too.
- **One-shot commands, no daemon:** `run`, `check`, `batch`, `snapshot`, `chrome`. A run launches Chrome, runs every
  journey given in its own context, and closes it. Built on Nullean.Argh, packed as a native AOT tool per RID like
  snap and curb, with a framework-dependent `any` fallback. There is no release job yet: publishing waits for the
  owner.
- **Exit codes:** 0 ok, 1 a journey (or the snapshot) failed, 2 usage or a journey that does not parse (no browser is
  started then), 3 no browser.
- **Every page-derived output is fenced**, checkpoints, refused URLs and failure messages alike, with one nonce per
  run announced in a header line; the structural lines around them (`== file`, `-- file:line: command`, `ok`,
  `FAILED`) come from the journey, never the page. Output is cut at 20,000 characters per checkpoint (`--max-output`,
  0 for none), inside the fence.
- **JSON** puts everything that came from the page under a `page` key and says so in a top-level `note`.
- **Safety defaults carry over:** `run` reaches only the base URL's host and `--allow`/`allow` hosts; `snapshot`
  only the page's host. `--base-url` takes a folder for `file://` pages (made into a URL with its trailing slash), and
  file access stays inside it. `batch` refuses secrets unless `--resolve-secrets`, and prints the
  `--allowed-domains` to pass to agent-browser on stderr.
- **Measured:** the native binary is 8.6 MB on osx-arm64 and runs `everything.journey` (40 steps) in 1.2 s including
  Chrome's start.

## Tests

- Every test has a 60 second timeout (assembly level): mutating reply routing showed a lost reply hangs the run.
- **PR checks are quick.** The browser suite runs on Linux (where a missing Chrome is a failure); macOS and Windows
  build and run only the tests that need no browser (`CDP_SKIP_BROWSER=1` skips the rest). On the small macOS runners
  the full browser suite with browser-level auto-attach made the runner unresponsive, losing even the job's log; a
  bisect pinned it to auto-attach under the full suite's load (the safety tests alone passed). It does not happen on
  a developer Mac. Not chased further: speed of development wins.
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

## Releasing

As nullean/snapshots does it: push a tag (`0.1.0`; MinVer takes the version from it), then start CI by hand on that tag
(`gh workflow run ci.yml --ref 0.1.0`). The `release` job runs only for such a manual run, needs every other job
including the tool smoke tests, checks that every package carries the tag's version, and publishes through nuget.org
trusted publishing (`NuGet/login` with the organisation's `NUGET_USER`; no API key is stored). The `release`
environment only lets tags deploy. The root tool package is pushed last, after the per-RID and `any` packages it
points at. A tag push alone never publishes. Versions stay 0.x until the owner says otherwise.

## Repository

- Laid out like nullean/snapshots: central package management, MinVer from tags (`0.1` minimum, `alpha.0`
  prereleases), TUnit on Microsoft.Testing.Platform, CI on linux, windows and macos.
