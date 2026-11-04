# Network and HAR

## Recording requests

```csharp
await using var recording = await page.RecordNetworkAsync();
await page.GotoAsync("http://localhost:5000/");

foreach (var request in recording.Requests)
    Console.WriteLine(request);              // GET http://localhost:5000/ (Document) 200
```

Recording is opt-in and starts when called. Each `NetworkRequest` has its `Url`, `Method`, `ResourceType`, `Status`,
`StatusText`, `MimeType`, `Protocol`, `EncodedBytes`, `FromCache`, and `Error` when there was no answer.
`recording.Clear()` forgets what was recorded.

In a journey, `network requests` lists them in agent-browser's format without its run-specific id, and filters them:

```text
network requests --type Image,Script
network requests --filter /api/ --method POST
network requests --status 4xx
network requests --failed --strict
network requests --clear
```

A list of requests is stable for a fixed page, so it suits snapshots. Give test pages an icon
(`<link rel="icon" href="data:,">`), or Chrome's late `favicon.ico` request makes the list vary.

## HAR files

```csharp
File.WriteAllText("run.har", recording.ToHar());
```

HAR 1.2, enough for a waterfall in DevTools or any HAR viewer: per-phase timings from Chrome's resource timing
(blocked, dns, connect, ssl, send, wait, receive; `-1` for phases a reused connection skipped, `time` their sum),
status, headers, query strings, MIME types and transfer sizes. Response bodies are not kept. `authorization`,
`proxy-authorization`, `cookie` and `set-cookie` values are `[redacted]`; pass `ToHar(redact)` to redact more.

```text
network har start
open /checkout
find role button click --name "Pay"
network har stop checkout.har
```

In a journey the file goes into the artifacts folder (`--artifacts`), and secrets typed during the run are replaced by
their `$env:` references. With the allow list on, each request waits for its verdict, which adds a little to
`blocked`; record with the guard off when the timings themselves are the point.
