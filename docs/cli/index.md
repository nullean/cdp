# The cdp CLI

```sh
dotnet tool install --global Nullean.Cdp.Cli
dnx Nullean.Cdp.Cli <command>        # once, without installing
```

A native AOT binary on Linux, macOS and Windows (x64 and arm64), with a framework-dependent fallback elsewhere. Every
command is one shot: it starts Chrome, does its work and closes it. There is no daemon.

| Command | |
|---|---|
| `cdp run <files...>` | run journeys and print every checkpoint |
| `cdp check <files...>` | parse journeys without a browser; every error with its line |
| `cdp parse <file>` | print a journey's parsed steps as JSON, for runners in other languages |
| `cdp batch <file>` | print a journey as agent-browser batch JSON |
| `cdp snapshot <url>` | print one page's accessibility tree |
| `cdp screenshot <url> -o <file.png>` | save a PNG of one page or element |
| `cdp chrome` | which Chrome would be used, or every place it looked |

`cdp <command> --help` lists every option.

## run

```sh
cdp run journeys/*.journey --base-url http://localhost:5000/
cdp run site/journeys/*.journey --base-url ./site --artifacts .artifacts/journeys --json
```

| Option | |
|---|---|
| `-b, --base-url` | what relative `open` URLs resolve against: a URL, or a folder for `file://` pages |
| `-a, --allow` | another host pattern the run may reach (repeatable) |
| `--feature` | a feature flag that is on (repeatable), for `feature` blocks |
| `--build-type`, `--env` | the build type and the environment's name, for `build-type:` and `env:` conditions |
| `--read-only` | refuse every request that is not GET, HEAD or OPTIONS, so the run cannot change a live site; see [safety](../journeys/safety.md#read-only-runs) |
| `--writable` | a URL glob a read-only run may write to anyway (repeatable), such as a telemetry endpoint |
| `--viewport` | the viewport the run starts with, `1280x800`; without it Chrome's default applies, which a responsive site may treat as a phone |
| `--user-agent` | the user agent of the run |
| `--header` | a request header every page sends (repeatable), `"Name: value"` |
| `--file-root` | the folder `file://` URLs may point into, when the base URL is not a folder |
| `--artifacts` | the folder `screenshot` and `network har stop` write into, and where `failure.png` goes with `--screenshot-on-failure`; off unless given |
| `--screenshot-on-failure` | take a screenshot of the page when a step fails and write it to `failure.png` in the artifacts folder; needs `--artifacts` |
| `--timeout` | seconds an action waits for its element (default 5) |
| `--json` | JSON output; everything from the page is under a `page` key |
| `--max-output` | characters per checkpoint before it is cut (default 20,000; 0 for no limit) |
| `--headed` | show the browser window |

Each journey runs in its own context of one Chrome. Secrets come from the environment.

## snapshot and screenshot

```sh
cdp snapshot http://localhost:5000/ --compact --selector main
cdp snapshot ./site/index.html --interactive
cdp screenshot http://localhost:5000/ -o home.png --full
cdp screenshot http://localhost:5000/ -o basket.png --selector "#basket" --width 390 --height 844
```

Both reach only the page's own host unless `--allow` adds others. `screenshot` sets a 1280×720 viewport unless told
otherwise.

## parse

```sh
cdp parse checkout.journey
```

Prints the parsed journey as JSON: `version`, `source`, `allow`, and a `steps` array with each step's `line`, `args`
and typed `command` (`type`, then its own fields; empty ones are left out; a target is `{kind, value, name, exact,
index}`). It is the contract other runners read instead of parsing journey syntax a second time. Secret references stay
references (`$env:NAME`); a journey that does not parse exits 2 with every error and its line, as `check` does.

## batch

```sh
cdp batch checkout.journey --base-url http://localhost:5000/ | agent-browser batch --bail
```

The `--allowed-domains` to pass to agent-browser are printed on stderr. Secret references are refused unless
`--resolve-secrets` writes their values into the output.

## Output

Everything that came from the page is fenced between `--- PAGE_CONTENT nonce=… ---` markers, with one random nonce per
run announced on the first line, so an agent reading the output can tell the page's words from the tool's. See
[safety](../journeys/safety.md#output-for-agents).

A failing journey prints the page's address and the error inside the fence, and, with `--screenshot-on-failure` and `--artifacts`, a `-- screenshot:` line
with the path of `failure.png`. A `feature` block the run does not meet is one line with the reason and no fence, because
nothing in it came from the page. A refused request that repeats is listed once with its count.

## Exit codes

| Code | |
|---|---|
| 0 | every journey passed, or the command succeeded |
| 1 | a journey step failed, or the page could not be read |
| 2 | usage error, or a journey that does not parse (no browser is started) |
| 3 | no Chrome found, or it failed to start |
