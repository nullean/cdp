# Nullean.Snapshots

[Nullean.Snapshots](https://snapshots.nullean.net) runs journeys in `dotnet test` and keeps one transcript per journey
as a snapshot: every checkpoint's output, compared as a tree, so a failure names what moved.

```csharp
[Test]
public async Task Checkout() =>
    (await JourneySnapshot.RunAsync("journeys/checkout.journey", new() { BaseUrl = new("http://localhost:5000/") }))
        .Should().MatchJourneySnapshot();
```

```sh
snap journey run journeys/checkout.journey --base-url http://localhost:5000/
```

A test and `snap journey run` share the same verified file, keyed by the journey's path. See
[browser journeys](https://snapshots.nullean.net/formats/browser/) in the Nullean.Snapshots docs.

Checks fit snapshots well when their output is stable: `a11y`, `links`, `network requests` and `errors` print
deterministic lists, and `timings` and `vitals` with `--budget` print only whether each reading is within its limit.
