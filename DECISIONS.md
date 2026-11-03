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

## Repository

- Laid out like nullean/snapshots: central package management, MinVer from tags (`0.1` minimum, `alpha.0`
  prereleases), TUnit on Microsoft.Testing.Platform, CI on linux, windows and macos.
