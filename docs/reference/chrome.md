# Chrome

## Finding it

In order, the first file that exists wins:

1. `CDP_CHROME`
2. `CHROME_PATH`
3. The standard install locations for Chrome, Chrome for Testing, Chromium and Edge on the OS (on macOS
   `/Applications`, then `~/Applications`).
4. `google-chrome`, `chromium` and `chrome` on the `PATH`.

When nothing is found, the error lists every place it looked; `cdp chrome` prints the same.

## Launching it

- Headless (`--headless=new`), with a temporary profile and `--remote-debugging-port=0`. The port comes from the
  `DevToolsActivePort` file Chrome writes into the profile, so no port is guessed or raced for.
- Background pages run at full speed: each context has its own window, and throttled windows render no frames, so
  input would wait for one.
- Headless scrollbars are hidden (`--hide-scrollbars`), so pages lay out at the same width on every OS.
- Chrome's output is drained and its last lines kept for the error when it exits or never opens its port.

## Cleaning up

- Disposing a browser sends `Browser.close`, kills the process tree after five seconds, and deletes the profile.
- A process that exits normally, or on Ctrl+C, closes every Chrome it launched.
- A process that is killed cannot clean up, so each profile holds a file naming its owner's process id and start time.
  The next launch closes the Chromes whose owner is gone, through their own DevTools port (never by process id, which
  may have been reused), and deletes their profiles once Chrome has let go of them. Profiles without that file are
  never touched.
