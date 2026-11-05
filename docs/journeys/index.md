# Journeys

A journey is a text file of agent-browser commands, one per line. Agents explore a site with agent-browser and write
down the steps that matter; the file then runs unchanged in agent-browser, in `dotnet test` and with `cdp run`, and on [Playwright and Elastic
Synthetics](playwright.md) through the `@nullean/journeys` npm package.

## The file

- **One command per line**, split the way a shell would: double or single quotes, backslash escapes.
- **Comments:** `#` followed by whitespace, or at the end of a line, starts a comment. `#id` is a CSS selector, so
  id selectors need no quoting.
- **Every error at once.** Parsing reports every bad line with its number and the reason, before a browser starts.
- **No refs.** agent-browser's `@e3` refs change from snapshot to snapshot, so they are rejected with a hint to use
  `find role … --name`, `find text`, `find label` or a CSS selector.
- **Only the supported subset.** Anything else is an error naming the line. The [command reference](commands.md) lists
  what is supported; [agent-browser](agent-browser.md) lists what is left out and why.

## URLs

`open /shop` resolves against the base URL as a link in a page would, so a base URL for a folder needs its trailing
slash (`http://localhost:5000/app/`). A URL is absolute only when it starts with a scheme. Without a base URL,
relative URLs are an error.

For local files, the base URL is the folder: `--base-url ./site`, or `new Uri("file:///…/site/")`. File URLs then stay
inside that folder.

## Checkpoints

The reads (`snapshot`, `get`, `is`, `console`, `errors`, `network requests`, `links`, `a11y`, `timings`, `vitals`,
`metrics`) are checkpoints. Each records:

| Field | |
|---|---|
| `Line` | the journey line |
| `Command` | the command as one normalised line: no comment, single spaces, quoted only where needed |
| `Url` | the page's URL when it ran |
| `Output` | what it read: an accessibility tree, a value, `true` or `false`, a list |

Because the command is normalised, re-spacing or re-commenting a journey changes nothing downstream.

## When a step fails

The run stops. The result keeps every checkpoint that ran and the failure: `file:line: command`, then the error in
plain text. A locator that never matched names itself as agent-browser writes it and says what it saw: no match,
matches that are not visible, or visible ones that are disabled.

## Lists or failures

Some checks report or fail, as you choose:

- `a11y`, `links` and `network requests` list what they found; with `--strict` anything found fails the step.
- `expect` asserts on page state and fails the step when it does not hold in time; it records nothing when it passes.
- `timings`, `vitals` and `metrics` print raw numbers; with `--budget lcp=2500 cls=0.1` they print only
  `lcp ok (<= 2500)` and fail over the limit. Raw numbers change from run to run; budgets keep a transcript stable.
