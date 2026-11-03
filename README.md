# Nullean.Cdp

A small Chrome DevTools Protocol client for .NET: find and launch Chrome, drive pages with agent-browser style
locators, and read the accessibility tree. Native AOT and trim clean, with no dependencies beyond the BCL.

It is the browser underneath *synthetic journeys*: plain text files written in agent-browser's command language that
replay in agent-browser, in `dotnet test` (with [Nullean.Snapshots](https://github.com/nullean/snapshots)) and from
a command line tool.

```
# Get started from the landing page
open /
find role link click --name "Get started"
wait main
snapshot -c -s main
```

Status: early, 0.x. See [ROADMAP.md](ROADMAP.md).

## Finding Chrome

`CDP_CHROME`, then `CHROME_PATH`, then the standard install locations for Chrome, Chrome for Testing, Chromium and
Edge on each OS, then `google-chrome`, `chromium` and `chrome` on the `PATH`. When nothing is found, the error lists
every place it looked.
