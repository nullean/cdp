# Nullean.Cdp

A small [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/) client for .NET, and
*synthetic journeys*: plain text files in [agent-browser](https://github.com/vercel-labs/agent-browser)'s command
language that replay in agent-browser, in `dotnet test` and from the `cdp` command line.

```text
# checkout.journey
open /shop
find label Search fill lamp
press Enter
wait --text "results"
find role button click --name "Add to basket"
snapshot -c -s main          # a checkpoint: its output is recorded
get text .basket-count       # another
a11y --strict                # fail the step on any accessibility problem
```

```sh
cdp run checkout.journey --base-url http://localhost:5000/
```

## What it is for

- **Direct, small.** One web socket to Chrome from your own process: no Node, no daemon, no dependencies beyond the
  BCL, and every package is native AOT and trim clean.
- **Waits like a user.** Locators find elements by role, text, label, placeholder, alt text, title, test id or CSS,
  wait until they are visible and enabled, and act with real mouse and keyboard input.
- **Journeys you can share.** Explore a site with an agent in agent-browser, write the steps down, and the same file
  runs in tests and CI. A journey reaches only the hosts it is allowed to, and its secrets stay `$env:` references.
- **Checks built in.** Accessibility rules, broken links, failed requests, console errors, paint timings, Web Vitals
  and HAR recording are each a journey command and a library method.

## Packages

| Package | For |
|---|---|
| `Nullean.Cdp` | finding and launching Chrome, contexts and pages, locators and actions, the accessibility tree, checks |
| `Nullean.Cdp.Journeys` | the journey language: parsing, running, export to agent-browser batch JSON, the safety rules |
| `Nullean.Cdp.Cli` | `cdp`: run, lint and export journeys, snapshot or screenshot a page |

Start with [getting started](getting-started/index.md). To turn journeys into snapshot tests, see
[Nullean.Snapshots](reference/snapshots.md).
