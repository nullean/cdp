# Roadmap

Planned in the Nullean.Snapshots session that started this repository. Where this and [DECISIONS.md](DECISIONS.md)
disagree, DECISIONS.md wins. Versions stay 0.x until the owner says otherwise.

## Packages

| Package | Contents |
|---|---|
| `Nullean.Cdp` | the CDP connection, Chrome discovery and launch, browser contexts and pages, locators, actions, waiting, the accessibility tree and its printer |
| `Nullean.Cdp.Journeys` | the journey language: parser, the agent-browser subset, `@ref` rejection, URL resolution, the runner, export to agent-browser batch JSON |
| `Nullean.Cdp.Cli` | native AOT tool (name to be decided): `run`, `check`, `batch`, `snapshot`, `chrome` |

## Phases

One PR each; done means CI green on linux, windows and macos.

0. *(done, #1)* **Scaffold** with Chrome discovery.
1. *(#2)* **Connection, launch, pages, accessibility tree.** One `ClientWebSocket`, flattened sessions, replies routed by id
   through a `ConcurrentDictionary<int, TaskCompletionSource>`, events to per-session channels. Launch headless with a
   temporary `--user-data-dir` and `--remote-debugging-port=0`, read `DevToolsActivePort`. Contexts through
   `Target.createBrowserContext`. `Accessibility.getFullAXTree` printed as `- role "name" [attrs]`, with `-c` and
   `-i` filtering.
2. **Locators, actions, waiting.** CSS, `role` + name through `Accessibility.queryAXTree`, text, label, placeholder,
   alt, title, testid. Click and hover through `Input.dispatchMouseEvent` at the content quad's centre, fill through
   `Input.insertText`, keys through `Input.dispatchKeyEvent`. Locators poll until visible and enabled (5 s default);
   an action that starts a navigation waits for `load`.
3. **Journeys.** One command per line, shell quoting, `#` comments. The subset: `open back forward reload`, `click
   fill type press check uncheck select hover`, `find role|text|label|placeholder|alt|title|testid … [--name]
   [--exact]`, `wait`, `set viewport|media`, and the checkpoints `snapshot`, `get`, `is`. `@eN` refs and anything else
   are parse errors with line numbers. The runner returns checkpoints and keeps them when a step fails.
4. **CLI**, packed per RID with native AOT like snap and curb.

## Tests

CsCheck for the journey parser's quoting, golden output for the tree printer on recorded `getFullAXTree` JSON, a fake
WebSocket server for the connection, real Chrome against `file://` fixtures for everything else, and a by-hand parity
check against agent-browser 0.41.0.
