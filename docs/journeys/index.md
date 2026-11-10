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

## The header

Comments before the first step can carry `@` directives that say what the journey is for. They do not change what a
run does; a harness reads them to choose which journeys apply and how to export them. A journey without directives
runs exactly as before.

```text
# @tags navigation smoke
# @requires navigation-preview !env:prod build-type:assembler,codex
# @monitor schedule=10 locations=germany,us_east
# @alert pagerduty
# @mutates
open /docs
```

| Directive | |
|---|---|
| `@tags <name...>` | names for selecting journeys: lower case letters, digits, `.`, `_`, `-` |
| `@requires <condition...>` | all of them must hold for the journey to apply: a feature flag (`navigation-preview`), `build-type:<a,b>` or `env:<a,b>` (any of the values), each optionally with a leading `!` |
| `@monitor [schedule=<minutes>] [locations=<a,b>]` | the journey is exported as a monitor |
| `@alert <channel...>` | where a failing monitor alerts, e.g. `pagerduty`; what a channel does is up to the exporter |
| `@mutates` | the journey changes data on the site, so a live run skips it |

A line may repeat (`@tags`, `@requires` and `@alert` accumulate; `@monitor` may appear once). An unknown directive, one
after the first step, or a bad value is an error with its line, so a typo cannot silently turn a condition off.
A condition about something the run does not know (no build type, no environment) is not matched: a plain condition
fails and a negated one holds.

`Journey.Metadata` (`metadata` in `cdp parse`, and in `@nullean/journeys`) holds what the header said, and
`JourneyMetadata.Unmet(JourneyContext)` (`unmetRequirement` in npm) returns the first `@requires` condition a run does
not meet, to report as the reason for skipping.

## Feature blocks

Some steps apply only to some states of a site, such as a feature flag being on. A `feature` block holds them:

```text
open /docs
feature navigation-preview
  expect visible role navigation --name "Section navigation"
  find role button click --name "Expand all"
end
feature !navigation-preview
  expect visible role navigation --name "Primary"
end
```

- The conditions are those of `@requires` (a flag, `build-type:<a,b>`, `env:<a,b>`, each optionally `!`), and all of
  them must hold. The run is told what it knows with `JourneyOptions.Features`, `BuildType` and `Env` (`features`,
  `buildType` and `env` in `@nullean/journeys`; `--feature`, `--build-type` and `--env` on `cdp run`).
- A block the run does not meet is skipped as a whole, and shows once as a checkpoint that says why:
  `feature !navigation-preview`, output `skipped: !navigation-preview`. Without any context, a block that needs a flag is
  skipped.
- Blocks are one level deep: a `feature` inside a block is an error. A flow that differs more than a few lines is better
  as two journey files with different `@requires`, one per flag state.
- Indentation is for people; it means nothing to the parser.
- agent-browser has no conditions, so a journey with blocks cannot be exported to it (`ToAgentBrowserBatch` says so).
## Run defaults

A harness that runs journeys against a live site sets three things once for the run, so no journey repeats them:

| Option | Does |
|---|---|
| `JourneyOptions.Viewport` (`viewport`, `--viewport 1280x800`) | the size the run starts with. Without it Chrome's default applies, and a responsive site may be in its mobile layout, where a link in a collapsed menu can be clicked with no effect and no error |
| `JourneyOptions.UserAgent` (`userAgent`, `--user-agent`) | the user agent of every request and of `navigator.userAgent`, e.g. one that tells the site the traffic is a monitor |
| `JourneyOptions.Headers` (`headers`, `--header "Name: value"`) | headers every page sends |

They are in place before the first step. A journey's own lines build on them: `set viewport` and `set device` replace the
viewport, `set useragent` replaces the user agent (`set device` does not: the run's identity stays), and `set headers`
adds to the run's headers, winning for a name it repeats, instead of replacing them. The hosts a run may reach are
`JourneyOptions.AllowedHosts` (`--allow`), as before.

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
