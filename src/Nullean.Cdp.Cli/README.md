# Nullean.Cdp.Cli

![Nullean.Cdp](https://raw.githubusercontent.com/nullean/cdp/main/docs/images/brand/png/lockup.png)

The `cdp` command: check and run synthetic journeys against Chrome over the DevTools Protocol, export them to
agent-browser, and snapshot pages. Native AOT.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Cdp.Cli?style=flat-square)](https://www.nuget.org/packages/Nullean.Cdp.Cli)

![cdp run on a journey against a local page: each checkpoint's output between nonce-fenced PAGE_CONTENT markers](https://raw.githubusercontent.com/nullean/cdp/main/docs/images/cdp-run.png)

## Install

```sh
dotnet tool install -g Nullean.Cdp.Cli
```

## Quick start

```sh
cdp check journeys/*.journey                             # parse only, every error with its line
cdp run journeys/checkout.journey --base-url http://localhost:5000/
cdp run site/journeys/*.journey --base-url ./site --json
cdp snapshot http://localhost:5000/ --compact            # one page's accessibility tree
cdp batch journeys/checkout.journey --base-url http://localhost:5000/ | agent-browser batch --bail
cdp chrome                                               # which Chrome, or where it looked
```

Everything the page produced is printed between `--- PAGE_CONTENT nonce=… ---` markers with a nonce per run, so an
agent reading the output can tell the page's words from the tool's. Exit codes: 0 ok, 1 failed, 2 usage, 3 no browser.
The command name `cdp` is a working name.

A journey is a text file with one agent-browser command per line:

```
# checkout.journey
open /shop
find label Search fill lamp
press Enter
wait --text "results"
find role button click --name "Add to basket"
snapshot -c -s main          # a checkpoint: its output is recorded
get text .basket-count       # another
```

## Finding Chrome

`CDP_CHROME`, then `CHROME_PATH`, then the standard install locations for Chrome, Chrome for Testing, Chromium and
Edge on each OS, then `google-chrome`, `chromium` and `chrome` on the `PATH`. When nothing is found, the error lists
every place it looked.

## Learn more

- Documentation: https://cdp.nullean.net/cli/, and [journeys](https://cdp.nullean.net/journeys/)
- Run journeys from .NET instead: [Nullean.Cdp.Journeys](https://www.nuget.org/packages/Nullean.Cdp.Journeys)
- Source and issues: https://github.com/nullean/cdp
