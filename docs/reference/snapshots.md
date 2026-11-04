# Nullean.Snapshots

[Nullean.Snapshots](https://snapshots.nullean.net) runs journeys in `dotnet test` and keeps one transcript per journey
as a snapshot: every checkpoint's output, compared as a tree, so a failure names what moved.

```csharp
public class ShopJourneys(ShopServer server)
{
    [Test]
    [JourneyFiles("journeys")]                   // a test case per journeys/**/*.journey
    public Task Run(SnapshotFile journey) =>
        JourneyTest.RunAsync(journey, new() { BaseUrl = server.Url });
}
```

Each journey is its own test case, named after its file (`Run(checkout)`, `Run(admin/users)`). A folder that needs
other setup gets its own class with a more specific folder. Screenshots and HAR files go next to the journey's
received transcript.

```sh
snap journey run journeys/checkout.journey --base-url http://localhost:5000/
```

A test and `snap journey run` share the same verified file, keyed by the journey's path. See
[browser journeys](https://snapshots.nullean.net/formats/browser/) in the Nullean.Snapshots docs.

Checks fit snapshots well when their output is stable: `a11y`, `links`, `network requests` and `errors` print
deterministic lists, and `timings` and `vitals` with `--budget` print only whether each reading is within its limit.

The same project snapshots HTTP APIs too: `.http` files run against any `HttpClient`, every response compared by
status, chosen headers and JSON body, secrets redacted. See [HTTP](https://snapshots.nullean.net/formats/http/).
