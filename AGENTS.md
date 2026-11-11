# Nullean.Cdp: contributor and agent conventions

A Chrome DevTools Protocol client for .NET, the journey language on top of it, and a native AOT CLI.
[DECISIONS.md](DECISIONS.md) records the non-obvious choices; [ROADMAP.md](ROADMAP.md) the plan.

## Build and test

`mise install` provides the toolchain: the .NET 11 SDK pinned in `global.json`, the net10.0 runtime the projects and
tests target, and the Node CI builds `js/journeys` with. The everyday tasks have the same names as in every nullean
repository, and run the commands CI runs:

```sh
mise run build            # build the solution
mise run test             # build, then run the tests
mise run pack             # build and pack the libraries (CI packs the native AOT CLI per RID)
mise run ci               # what CI runs on every push: build, test, pack, and the js/journeys job
mise run docs             # build the docs and serve them (needs docs-builder, see scripts/docs.sh)
mise run check            # workflow security checks (zizmor)
mise run release          # show the release draft; `mise run release publish` releases
```

Without mise:

```sh
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --project tests/Nullean.Cdp.Tests --treenode-filter "/*/*/ChromeLocatorTests/*"
```

Requires the .NET 11 SDK (pinned in `global.json`). Tests run on Microsoft.Testing.Platform with TUnit. Tests that need
a real Chrome skip with a reason when none is found; CI sets `CDP_BROWSER_REQUIRED=1` so they fail instead.

The CLI: `dotnet run --project src/Nullean.Cdp.Cli -- run <journey> --base-url <url or folder>`.
`build/smoke-cli.sh <command that runs cdp>` smoke-tests a built or installed tool against the fixture site.

## Releasing

release-drafter keeps a draft release current from the labels of merged pull requests; every pull request carries
exactly one release label (`pr-labels.yml` enforces it). **Publishing the draft** releases: it creates the tag and runs
`ci.yml` on it, whose `release` and `publish-npm` jobs push the NuGet packages and `@nullean/journeys` with trusted
publishing once every other job has passed, then attach the packages to the release. Pushing a tag on its own does
nothing. In 0.x a `breaking` label bumps the minor version; 1.0 is the maintainer's call. See the
[releasing](.claude/skills/releasing/SKILL.md) and [breaking-changes](.claude/skills/breaking-changes/SKILL.md)
skills, and `mise run release` for the current draft.

## Rules

- No dependencies beyond the BCL in `Nullean.Cdp`. Libraries set `IsAotCompatible`: JSON through `Utf8JsonReader` and
  `Utf8JsonWriter`, no reflection.
- No `lock`: concurrent collections, `Interlocked`, `Lazy<T>`.
- Error messages are plain text: no ANSI, readable by people and agents, and they say what was tried.
- Anything the CLI prints that came from a page goes inside `PageContent.Fence`; nothing from a page reaches a
  structural line.
- Journeys never use agent-browser `@ref`s; they are not stable between runs.
- Test the tests: break the code on purpose and confirm the suite fails. After restoring a mutated file, `touch` it
  before the next build: a restored copy keeps an older timestamp and MSBuild keeps the mutated binary.
- Never publish packages or push tags without the owner's approval.
- Record non-obvious choices in DECISIONS.md in the same change.
