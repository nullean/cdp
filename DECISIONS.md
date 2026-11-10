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
- **A read that fails after disposal began is the disposal, whatever it throws.** `DisposeAsync` cancels, then disposes the
  transport, so a pending read can end with the cancellation or with the transport closing under it (a channel completing,
  a socket disposed). Which one depends on a race inside the transport, so `ReceiveLoopAsync` treats any exception as
  "disposed" once the connection is stopping, and reports "Reading from the browser failed" only when it was not. The
  failure showed up as a flaky `DisposingFailsPendingCommands` on a loaded Linux runner; the tests now make the transport
  throw each kind of exception deterministically.

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
- **`set useragent <ua>`** is ours, not agent-browser's (which sets a user agent only at launch, `--user-agent`), so the batch
  export leaves it out like `expect`. It is `Emulation.setUserAgentOverride`, which changes the `User-Agent` header and
  `navigator.userAgent` together; `set headers` alone would leave the second, which is what a page reads to switch features
  (the telemetry SDK turns itself off for `Elastic/Synthetics`). On Playwright it goes through a DevTools session, so
  Chromium only. The value is literal: no secret references.
- **`expect request` and `expect same-document`** (assertions about the page, not an element) are their own commands, since they
  read the network recording and a document counter, not a locator. A request is matched only once it is over (a response or a
  failure), so `--status`, `--failed` and body filters judge the whole exchange; a negated assertion also waits while a request
  that could still match the URL is in flight, or an export that fails late would be missed. Bodies are captured when the request
  starts (from the event, else `Network.getRequestPostData` right away), because a navigation drops them; a failure prints a body's
  size, never its text, so a payload with secrets does not reach a log. `same-document` compares `Page.DocumentLoads` with its
  value when the last acting step began (an `init` lifecycle event is a new document; `pushState` and htmx swaps are not), taken
  after a navigation step instead so `open` then `expect same-document` holds. Page script (`eval`) stays out: this is what the
  `window.__noReload` marker was for.
- **A request started during unload is not recorded, and `about:` URLs are refused.** Found while testing `expect request` against a
  page that flushes telemetry in `pagehide`: the server got the POST, but `Network.requestWillBeSent` never arrived, and Playwright's
  `request` event never fired either (a `fetch` with and without `keepalive`, `sendBeacon`, from `pagehide` and from
  `visibilitychange`). It is Chrome, not the recording, so the docs say to assert on what a page sends while it is open. `open
  about:blank` is refused by the allow list like every scheme but http(s), ws(s) and `file:` (Safety.cs); that stays, since a journey
  that could navigate anywhere would defeat the list, and leaving a page means opening another one.
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
- **INP needs recording from the start, and only when asked.** Chrome keeps only interactions of about 104 ms or
  more. `page.RecordInteractionsAsync()` adds a small script before any page script
  (`Page.addScriptToEvaluateOnNewDocument`) that records interactions from 16 ms, as Google's web-vitals library
  does. It is opt-in: the journey runner turns it on only for journeys with `vitals`; other pages run nothing extra.
- **No waiting unless a reading is asked for, and then only when needed.** The frame waits live in `TimingsAsync`
  and `VitalsAsync` alone; timings skip them when the page has already reported its paints.
- **Reported, not instant.** Paint entries, layout shifts and interactions are reported once their frame is rendered
  (on the GPU-less Linux runner first paint came after `load`). Timings wait (two frames, then up to 2 s) only for a
  loaded page that has reported no paint yet.
  Vitals wait two frames (and up to 200 ms after an interaction), but a change made just before may still be missing;
  a journey waits a moment before reading. A change before the page's first frame is no layout shift at all (nothing
  painted moved): the test page shifts only after two frames, or under load its CLS read 0.
- **Export:** `vitals` is an agent-browser command and exports without our budgets; `timings` and `metrics` are not
  and are left out.

## Network and HAR

- **Opt-in recording.** `page.RecordNetworkAsync()` collects the page's requests from that moment (the `Network`
  domain's request, response, finished and failed events); the journey runner starts it only for journeys with
  `network requests` or `network har`.
- **`network requests`** is a checkpoint in agent-browser's line format without its run-specific id:
  `GET url (Type) 200`, filtered by `--filter` (a substring, or a glob with `*`), `--type`, `--method`, `--status`
  (`200`, `2xx`, `400-499`); `--clear` forgets what was recorded. Request lists are deterministic for fixed pages, so
  they suit snapshots (Chrome's late `favicon.ico` request is not: the test pages declare an empty icon).
- **HAR 1.2**, enough for a waterfall in DevTools or any HAR viewer: per-phase timings from Chrome's resource timing
  (blocked, dns, connect, ssl, send, wait, receive, with `-1` for phases a reused connection skipped and `time` the
  sum), status, headers, query strings, MIME types and transfer sizes. Response bodies are not kept.
- **Safe to write.** `network har stop <path>` writes only inside the artifacts folder the host chose
  (`JourneyOptions.ArtifactsDirectory`, the CLI's `--artifacts`; nothing is written without one), and the path may not
  leave it. `authorization`, `proxy-authorization`, `cookie` and `set-cookie` values are `[redacted]`, and secrets
  typed during the run are replaced by their `$env:` reference. `network route`, `unroute` and `request` (mocking and
  bodies) are not journey commands.
- With the network guard on, each request waits for the guard's verdict, which adds a little to `blocked`; record
  with the guard off when the timings themselves are the point.

## Broken links and failed requests

- **`network requests --failed`** lists requests answered with 400 or more and requests that got no answer at all
  (`net::ERR_NAME_NOT_RESOLVED`, a refused connection, …). It leaves out what the allow list refused, which the run
  already reports, and requests a navigation cancelled (`net::ERR_ABORTED`), which are not broken. `--strict` fails
  the step when anything is listed. The export to agent-browser turns `--failed` into `--status 400-599` and drops
  `--strict`.
- **`links`** checks the links on the page, not only what it loaded: every `a[href]` and `area[href]` with an http,
  https or file URL, once each, fragments dropped, in document order. The checkpoint is a summary line and the
  broken links (`--all` for every one), with same-site links printed without their origin, so transcripts don't
  depend on the port.
- **Same-site only by default.** A link to another origin is counted and left alone unless the journey says
  `--external`, and even then only hosts in the allow list are fetched: a page could otherwise make a run send
  requests anywhere.
- **How:** `Network.loadNetworkResource`, the browser fetching on behalf of DevTools. It is a GET outside the page,
  so the page's CORS rules don't apply, page script never sees it, and the response stream is closed unread. Cookies
  go only to the page's own origin. Four at a time. `file://` links are checked on disk. The page's `set
  credentials` are not applied (they live in the request guard), so a link behind Basic auth reports 401.
- **Not yet:** images that load but fail to decode leave no network trace; they would need a DOM check.

## Screenshots

- **`screenshot [selector] <path.png> [--full]`**, agent-browser's syntax: the viewport, the whole page past the
  viewport (`Page.getLayoutMetrics` then a clip with `captureBeyondViewport`), or one element (scrolled into view,
  its box clipped in document coordinates). PNG only.
- **Headless Chrome starts with `--hide-scrollbars`**, as Puppeteer's does: Linux otherwise draws 15px scrollbars that
  narrow the page (a full-page screenshot came out 1265px wide instead of 1280) where macOS overlays them.
- **Files, not checkpoints.** Pixels change with fonts, anti-aliasing and GPU, so comparing them exactly would fail
  across machines; a screenshot goes into the artifacts folder under the same rules as HAR files, and the transcript
  doesn't mention it. Visual comparison with a tolerance would be a separate snapshot format.
- **CLI:** `cdp screenshot <url> -o file.png [-s selector] [--full] [--width 1280 --height 720]`. Headless Chrome's
  own default viewport is 756×469, so the CLI sets one; journeys keep the browser's unless they `set viewport`.

## Accessibility audit

Our own rules, no third-party engine: axe-core (MPL-2.0) is not an option, nuget.org's web checkers all bundle it,
and the permissive engines are either large (IBM Equal Access, Apache-2.0) or not built to inject (Siteimprove Alfa,
MIT). A separate optional package could bundle IBM's engine later.

- **From Chrome's accessibility tree**, which computes accessible names: `image-alt`, `button-name`, `link-name`,
  `label` (text boxes, comboboxes, checkboxes, radios, sliders, …), `empty-heading`, `heading-order` (a level may
  only go down by one), `landmark-one-main`.
- **From the DOM:** `document-title`, `html-has-lang`, `aria-hidden-focus`, `duplicate-id`, and `color-contrast`:
  WCAG AA (4.5:1, 3:1 for text of 24px or bold 18.66px), the text colour composed over the nearest solid background
  through semi-transparent layers; text over a background image or gradient is skipped as undecidable, and disabled
  controls are exempt, as WCAG says.
- **Named and laid out like agent-browser's `a11y`** (axe-core's rule ids, `[impact] rule: help (n nodes)`, elements
  by tag and identifying attributes), so results read alike; the help texts are our own. On the same page axe-core
  4.12 reported the same eleven problems plus `region` (content outside landmarks, left out as noisy); we also report
  `duplicate-id`.
- **In journeys:** `a11y [-s selector] [--strict]` is a checkpoint; `--strict` fails the step on any violation. The
  output is deterministic, so it snapshots well. The export keeps `a11y` (agent-browser runs axe-core on it) without
  `--strict`.

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

## Playwright and Elastic Synthetics

- **`@nullean/journeys` (js/journeys) runs journeys on Playwright**, in Playwright Test and in Elastic Synthetics, so
  one `.journey` file is the source for the .NET snapshot test, a Playwright test and a monitor kept in sync with it.
  Nullean.Cdp stays the reference: nothing here changes what the .NET runner does.
- **A second parser, kept honest.** Node cannot call the .NET parser, and shelling out to `cdp parse` would need the
  tool installed wherever a monitor is pushed, so the TypeScript parser is a port (`parse.ts` follows `Journey.Parse`
  line by line). `cdp parse` and `fixtures/journey-errors.json` are the contract: the conformance test compares the
  port with the JSON of every corpus journey and with the fixture's errors, which the .NET tests also check.
- **Compared where Playwright can match, marked where it cannot.** Text reads print the .NET
  format, and a live test compares them with Nullean.Cdp's own readings (`test/reference/everything.json`);
  `network requests`, `links` and the performance reads use the same formats but have no browser test yet.
  `snapshot` (Playwright's aria tree) and `a11y` (axe, not our rules) come out differently, so their checkpoints are
  `kind: 'aria'` and are never compared with a verified transcript. The table is in the package README.
- **Refuse rather than widen.** `set credentials` fails (an extra header goes to every host), `network har` and
  `screenshot` without an artifacts folder are skipped with a warning (Playwright and Synthetics keep their own), and
  timeouts are Playwright's unless given, so a monitor's own settings are not overridden.
- **Synthetics registers steps synchronously**, so the adapter parses at registration (a bad journey fails before
  anything runs) and starts the run lazily in the first step.
- **Dev dependencies are pinned to exact versions** (`@playwright/test` 1.64.1: 1.55.0 has a high-severity advisory).
  Tests use the installed Chrome (`channel: 'chrome'`), so nothing is downloaded. Not yet published to npm.
- **`expect` on Playwright uses Playwright's own assertions when it is handed `expect`** (the Playwright Test adapter
  always, the Synthetics adapter when its context has `expect`, which `@elastic/synthetics` re-exports from
  `@playwright/test`), and a built-in polling fallback otherwise. The fallback reads the same things as Nullean.Cdp's
  engine and uses its messages, so a bare `runJourney` works and a failure reads the same as in `cdp run`. The two paths are
  held together by `test/parity.test.ts`: every matcher, holding and not, through both, which must agree. It found the
  missing-element rule (see "Assertions"). Under Playwright's `expect` a failure is Playwright's message, colour removed
  and cut to 14 lines. `url` globs become regular expressions, and `--contains` becomes an escaped one where Playwright
  takes a string or RegExp; `toMatchAriaSnapshot` is not mapped (Synthetics does not support snapshot assertions).

## Assertions

- **The engine is library code in `Nullean.Cdp`, framework-free, and journeys are one consumer.** So it can later back
  AwesomeAssertions and Nullean.Snapshots without the core depending on either. A `Matcher` is data (kind, expected text
  or number, how to compare); `Locator.CheckAsync` and `PageExpectations.CheckAsync` poll it and return an
  `ExpectationResult` without throwing; `ExpectAsync` throws `ExpectationException` (a `TimeoutException`, so a journey
  step fails with no special case). Adapters report the result; they do not re-implement matching.
- **Playwright's semantics, where its docs are clear.** Retry until a timeout (the page's action timeout, 5 s);
  `--not` keeps checking until the matcher stops holding; `hidden` holds for a missing element; a string `text` is the
  whole text with whitespace collapsed, `--contains` a substring, both case-sensitive unless `--ignore-case`, read from
  `textContent` (`--inner-text` for rendered text, which is what `get text` reads); `class` is the whole attribute and
  `--contains` every class in any order; `count` is exact. Where the docs are silent (value, title, accessible name,
  whitespace in attributes) we compare as written, and the npm package's parity test against Playwright's own `expect` is
  where a difference would show.
- **Only visibility, attachment and counts can be asserted about nothing:** `visible`, `hidden`, `attached` and `count`
  (negated or not) hold or fail on a missing element by what they say; every other matcher needs an element, even negated,
  so `--not enabled` on a missing element keeps failing with `received no element matches`. That is Playwright's rule,
  found by the npm package's parity test: the first version of this engine let a negation hold for a missing element, and
  Playwright's own `.not.toBeEnabled()`, `.not.toHaveText()` and the rest failed instead.
- **`url` takes the `wait --url` glob** (`**` anything, `*` anything but `/`), or `--regex`. Playwright's string form is
  not documented as a glob, so the npm package turns the glob into a regular expression instead of relying on it.
- **Regular expressions are .NET's here and JavaScript's on Playwright**, without flags (`--ignore-case` instead), so
  patterns are limited to what both read alike. Quote a pattern with backslashes: unquoted, a backslash escapes the next
  character in a journey line. An invalid pattern is a parse error.
- **An `expect` line is not a checkpoint.** A pass records nothing, so no verified transcript changes; a failure stops the
  journey like any step (`Journey stopped at file:line: expect …`, then `Timed out after 5s waiting for css #count to have
  text "3": received "2".`). The message names the locator as `Locator.ToString` writes it, as the other timeouts do.
  `$env:NAME` works in expected values and is redacted. agent-browser has no assertions, so the export leaves them out.
- **No soft assertions, no `toPass`/`poll`, no `toHaveScreenshot`/`toHaveJSProperty`.** A journey stops at the first
  failure everywhere; the others need page script or a different model.
- **`aria` (a tree template, `toMatchAriaSnapshot`) is deferred.** Cdp and Playwright print different trees, so one
  template would not mean the same in both; Elastic Synthetics does not support snapshot assertions at all; and it overlaps
  the whole-tree snapshot Nullean.Snapshots already compares. The accessible-name matcher (`toHaveAccessibleName`) waits
  for the same parity check.
- **AwesomeAssertions: proven with a test-only adapter** (`tests/Nullean.Cdp.Tests/Live/LocatorAssertions.cs`):
  `await locator.Should().HaveTextAsync("2 results")`, with `because` and `AssertionScope`. Two things it taught. The
  message must be an argument, never the format string (selectors and page text hold braces), and the format string must
  contain `{reason}` or a `because` is silently dropped: `SnapshotAssertions.FailUnless` in Nullean.Snapshots passes the
  message verbatim but has no `{reason}`, so it cannot carry a reason yet (a small change there when an adapter ships). Where
  a real adapter lives is open: a package of its own in this repository (live-page assertions, nothing to do with
  snapshots) or `Nullean.Snapshots.Browser.Assertions` (already AwesomeAssertions-facing). The core API does not depend on
  the answer.

## CLI

- **The command is `cdp` for now**, a working name to be settled with the owner. It is set once, as the assembly name
  and `ToolCommandName` in `src/Nullean.Cdp.Cli/Nullean.Cdp.Cli.csproj`; the smoke script and CI name it too.
- **One-shot commands, no daemon:** `run`, `check`, `parse`, `batch`, `snapshot`, `screenshot`, `chrome`. A run launches Chrome, runs every
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
- **`cdp parse` and `Journey.ToJson`** are the contract for runners outside .NET (a Playwright runner reads the
  steps, rather than parsing the syntax a second time and drifting). Written by hand with `Utf8JsonWriter` (AOT, no
  reflection); `version` is `Journey.JsonVersion`; empty parts are left out; secrets stay `$env:` references, never
  values. A command with no case in `Journey.WriteCommand` throws, so a new command cannot be added without its JSON.
  `tests/Nullean.Cdp.Tests/site/journeys/syntax.journey` holds the commands the two run journeys do not (parsed and
  exported, never run), so the three snapshots cover every command type.
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

As nullean/entities and nullean/curb do it: push a tag (`0.1.0`; MinVer takes the version from it). The `release`
job runs for a pushed tag, needs every other job including the tool smoke tests, checks that every package carries
the tag's version, and publishes through nuget.org trusted publishing (`NuGet/login` with the organisation's
`NUGET_USER`; no API key is stored). Pushing the tag is the release decision, so tag only what is meant to ship; a
failed run is fixed and re-run (the pushes skip what is already there). It was a manual run on the tag, as in
nullean/snapshots, until the repositories were aligned. The `release` environment only lets tags deploy. The root
tool package is pushed last, after the per-RID and `any` packages it points at. Once the packages are on nuget.org the job creates the GitHub release
for the tag (`gh release create --generate-notes`, notes from the pull requests since the previous tag, the
packages attached, a `-` version marked as a prerelease); the job needs `contents: write` for it. Versions stay 0.x
until the owner says otherwise.

`@nullean/journeys` (js/journeys) goes out in the same tag run, as the `publish-npm` job: same `release` environment,
no stored token, npm trusted publishing (`id-token: write`; npm 11.5.1 or later, checked; provenance is automatic for a
public repository). The tag is the version (`npm pkg set version`; package.json keeps a placeholder), a `-` version
publishes under the `next` dist-tag, and a version already on npm is skipped so a re-run is safe. The trusted publisher
(organisation `nullean`, repository `cdp`, workflow `ci.yml`, environment `release`, direct `npm publish` allowed) is
configured on npmjs.com, and only exists for a package that already has a first version, so that first version is
published by hand by the owner.

## Documentation

- **Built like nullean/snapshots':** a `docs/` docset built by the `elastic/docs-builder` action with the
  design-system layout (`FEATURE_NAVIGATION_PREVIEW`), then `scripts/docs-finish.sh` appends the theme
  (`docs/cdp-docs.css`) to the emitted `ds.css`, inlines the header lockup (`scripts/docs-pages.py`), copies the fonts
  and replaces `index.html` with the hand-written landing page (`docs/cdp-landing.html`). Published to GitHub Pages
  at `cdp.nullean.net` (Pages from Actions, custom domain on the repository, DNS and TLS at Cloudflare). Pull requests
  build the site and upload it as an artifact; only pushes to main deploy. `scripts/docs.sh` builds and serves it
  locally with a docs-builder from `DOCS_BUILDER` or the `PATH`.
- **The brand, "Signal"** (`docs/images/brand`): the Nullean frame and tile shared with snapshots' Aperture, around a
  browser window with a pointer clicking in it. Teal `#7ED6C8` is the wire, coral `#FF9F7A` the click and the
  wordmark's period. In the light theme they are 1.6:1 and 1.9:1 on paper, so text uses `#0F6E61` (5.8:1) and
  `#B5482A` (5.1:1). The animated mark has the pointer press and the click ripple out, still under
  `prefers-reduced-motion`.
- **PNGs are rendered by Chrome through this library** (favicons, touch icon, lockups, social card): each SVG on a
  transparent canvas with the brand fonts loaded, captured with a clip at the target scale.
- **The pages are checked against the code:** API names, option names and sample output (trees, `cdp run` output,
  the audit report) come from running it or from the tests' recorded output.

## Repository

- Laid out like nullean/snapshots: central package management, MinVer from tags (`0.1` minimum, `alpha.0`
  prereleases), TUnit on Microsoft.Testing.Platform, CI on linux, windows and macos.

## The journey header

- **Directives are comments**, `# @name …` before the first step, so a file with them still runs in agent-browser and
  in older versions of this library, which ignore comments.
- **An unknown or misplaced directive is an error**, not a comment: `@requries` silently turning a condition off would
  run a journey where it should not run. The cost is that a header comment that starts with `@` now has to be reworded.
  Directives after the first step are errors for the same reason.
- **`@requires` is a conjunction of simple conditions** (flag, `build-type:`, `env:`, each negatable), with a comma for
  any-of inside `build-type:` and `env:`. There is no `or` and no nesting: a journey that needs one has two files.
  Unknown context is not matched, so `!env:prod` still holds when the environment is not known.
- **`ToJson` keeps `version` 1 and adds an optional `metadata` object**, left out when the header has no directives, so
  every existing journey prints byte for byte what it printed before. Readers ignore keys they do not know. The
  version moves when a change would make a reader do the wrong thing, which a new optional key does not.
- **Metadata is parsed and evaluated by the library, applied by the harness.** The runner does not skip journeys:
  deciding to is the caller's, with `JourneyContext` and `Unmet`.

## Feature blocks

- **Blocks, not per-step conditions.** A condition on every step makes a journey hard to follow; a block reads as the
  flow it belongs to, and a big difference is two files with `@requires`. Blocks are one level deep so a reader never
  holds more than one condition. Nesting is an error naming the open block's line.
- **The runner skips, and says so.** A skipped block is one checkpoint (`feature …`, `skipped: <unmet condition>`), so a
  transcript or a CI log shows what did not run; a block that skips silently looks the same as one that passed.
  Without context a block that needs a flag is skipped: unknown is not matched, as in `@requires`.
- **Steps stay a flat list.** Each step in a block points at it (`JourneyStep.Block`, `block` in the JSON), and the blocks
  are listed once (`Journey.Blocks`, `blocks`), so `OnStep`, line numbers and every runner that walks steps keep working.
- **`ToJson` is version 2 only for a journey with blocks.** A reader that ignores `blocks` would run steps that should be
  skipped, so it must refuse them; a journey without blocks still prints version 1 byte for byte as before.
- **Not exported to agent-browser**, which runs every line.

## Read-only runs

- **The guard enforces it, a journey doesn't declare it.** `NetworkGuardOptions.ReadOnly` refuses on the request's
  method in `Fetch.requestPaused`, where the page cannot get around it; a journey marked as read-only could still
  write by mistake. The TS runner does the same in `page.route`.
- **Writes are judged after the allow list**, so a refused third-party beacon stays a reported request and only a
  write to an allowed host fails the step. A page that writes on its own (telemetry) needs `WritableUrls`.
- **`BlockedRequest.RefusedWrite` is `null` for allow-list refusals**, so existing `BlockedRequest` equality is
  unchanged.
## Run defaults

- **A harness sets the viewport, user agent and headers once per run.** A live site tells monitors from people by user
  agent and headers (the docs site turns its telemetry off for a monitor), and a responsive site is in its phone layout
  at Chrome's default viewport. Repeating `set viewport`, `set useragent` and `set headers` in every journey is how one
  gets forgotten.
- **The journey builds on the run, it does not undo it.** `set headers` merges (a header it repeats wins) because
  Chrome's own call replaces the whole set and a journey adding one header should not drop the monitor header;
  `set device` leaves the run's user agent in place for the same reason. `set viewport`, `set device` and `set useragent`
  still replace what they name.
- **No allow-list option was added**: `AllowedHosts` already is the run-level default.
- **A click that hits nothing is not this change**: at the phone viewport `find role link click` completed with no effect.
  The default viewport is the fix for that site; a click that reports it missed its target is separate.
## A click that cannot land fails

- **Pointer actions hit-test their point.** Chrome delivers `Input.dispatchMouseEvent` to whatever is at the point, so a
  click on a covered or off-screen element "succeeds" and does nothing, and the failure shows at a later step that
  times out far from the cause. `click`, `dblclick`, `hover`, `check`, `uncheck` and the source of `drag` check
  `elementFromPoint` at the point first (into shadow roots, like a click). Scrolling and the target of a drag press
  nothing, so they do not.
- **It waits, as Playwright's "receives events" does**, because covers are often transient (a banner fading out); the
  step fails with the element that would receive the click, or "nothing is there" off the screen or clipped.
- **Accepted as a hit:** the element, anything inside it, or a `<label>` whose control is the element (a custom switch
  hides its input under the label). `pointer-events: none` is skipped by the browser, so the element under it counts.
- **No `force` option.** A journey that must click a covered element is describing something a user cannot do.

