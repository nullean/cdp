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

## Tests

- Every test has a 60 second timeout (assembly level): mutating reply routing showed a lost reply hangs the run.

## Repository

- Laid out like nullean/snapshots: central package management, MinVer from tags (`0.1` minimum, `alpha.0`
  prereleases), TUnit on Microsoft.Testing.Platform, CI on linux, windows and macos.
