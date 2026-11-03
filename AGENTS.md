# Nullean.Cdp: contributor and agent conventions

A Chrome DevTools Protocol client for .NET, the journey language on top of it, and a native AOT CLI.
[DECISIONS.md](DECISIONS.md) records the non-obvious choices; [ROADMAP.md](ROADMAP.md) the plan.

## Build and test

```sh
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --project tests/Nullean.Cdp.Tests --treenode-filter "/*/*/ChromeLocatorTests/*"
```

Requires the .NET 10 SDK (pinned in `global.json`). Tests run on Microsoft.Testing.Platform with TUnit. Tests that need
a real Chrome skip with a reason when none is found; CI sets `CDP_BROWSER_REQUIRED=1` so they fail instead.

## Rules

- No dependencies beyond the BCL in `Nullean.Cdp`. Libraries set `IsAotCompatible`: JSON through `Utf8JsonReader` and
  `Utf8JsonWriter`, no reflection.
- No `lock`: concurrent collections, `Interlocked`, `Lazy<T>`.
- Error messages are plain text: no ANSI, readable by people and agents, and they say what was tried.
- Journeys never use agent-browser `@ref`s; they are not stable between runs.
- Test the tests: break the code on purpose and confirm the suite fails.
- Never publish packages or push tags without the owner's approval.
- Record non-obvious choices in DECISIONS.md in the same change.
