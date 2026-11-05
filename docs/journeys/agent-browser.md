# agent-browser

Journeys use [agent-browser](https://github.com/vercel-labs/agent-browser)'s command language, so an agent that
explored a site with agent-browser writes its steps down as it ran them, and any journey replays there.

## Export

```sh
cdp batch checkout.journey --base-url http://localhost:5000/ | agent-browser batch --bail
```

```csharp
string json = journey.ToAgentBrowserBatch(new Uri("http://localhost:5000/"));
```

The export writes agent-browser's batch JSON with relative URLs resolved and uploads as absolute paths. Commands that
are ours, not agent-browser's, are translated or left out:

| Journey | Exported as |
|---|---|
| `allow …` | left out; `cdp batch` prints the `--allowed-domains` to pass to agent-browser |
| `timings`, `metrics`, `links`, `expect …` | left out (agent-browser has no assertions) |
| `vitals --budget …` | `vitals` |
| `a11y --strict` | `a11y` (agent-browser runs axe-core) |
| `network requests --failed` | `network requests --status 400-599` |
| `--strict` anywhere | dropped |
| `$env:NAME` | refused, unless secrets are resolved (`--resolve-secrets`) |

A journey that uses every supported command was exported and run with agent-browser 0.41.0: every step succeeded and
read the values this runner reads.

## Left out on purpose

| agent-browser | Why |
|---|---|
| `@e1` refs | they change from snapshot to snapshot |
| `eval`, `wait --fn` | page script |
| cookies, storage, `state`, `connect`, `auth` | session state and credentials from outside the journey |
| tabs, `pdf`, `record`, `download`, `wait --download` | windows and files |
| `snapshot --delta`, `--full` | a checkpoint records the whole tree; a snapshot diff shows the change |
| `mouse` | raw coordinates |
| `network route`, `unroute`, `request` | they change what the page receives |

## Differences

- `snapshot -c` is ours: it drops list markers, line breaks and nodes left with no name, value, state or children.
  agent-browser's `-c` also drops some named nodes; this one keeps anything with a name.
- `set geo` grants the permission, so the page reads the position.
- `errors` prints each error with its location; agent-browser's text output printed an empty `✗` per error.
- `screenshot` writes only into the artifacts folder, and `--annotate` is not supported.
