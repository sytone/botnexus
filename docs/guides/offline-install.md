# Installing BotNexus Without nuget.org Access

> For users whose network blocks `https://api.nuget.org` — air-gapped environments, corporate proxies, or policy-restricted feeds. If you can reach nuget.org, use [Install from Release](../getting-started-release.md) instead; that remains the supported default.

The BotNexus CLI is packaged as a .NET global tool (`PackAsTool` in `src/gateway/BotNexus.Cli/BotNexus.Cli.csproj`), so the default install pulls `BotNexus.Cli` from nuget.org:

```bash
dotnet tool install -g BotNexus.Cli
```

When that feed is unreachable you will see:

```text
error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json
```

This page covers four independent ways around that, plus the separate problem of restoring the *platform* build behind a blocked feed.

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 10+ SDK** | Required for the source-build paths on this page (the SDK, not just the runtime). Verify with `dotnet --version`. |
| **Git** | To clone the repository. |
| **A way to obtain the source** | Either network access to GitHub, or a repository mirror, or a source archive copied in by other means. |

---

## Option 1 — Build the tool package from source (recommended)

This produces a `.nupkg` locally and installs the CLI from a directory on disk. No package feed is contacted for the tool itself.

```bash
# 1. Get the source
git clone https://github.com/sytone/botnexus.git
cd botnexus

# 2. Build the tool package into a local directory
dotnet pack src/gateway/BotNexus.Cli -c Release -o ./artifacts

# 3. Install the tool from that directory
dotnet tool install -g BotNexus.Cli --add-source ./artifacts
```

`dotnet pack` writes a versioned package, for example `./artifacts/BotNexus.Cli.0.34.0.nupkg`. Verify the install:

```bash
botnexus --version
```

!!! note "`--add-source` still consults your configured feeds"
    `--add-source` *adds* a source rather than replacing your configured ones, so NuGet may still try nuget.org and log a warning. If a blocked feed causes a hard failure rather than a warning, add `--ignore-failed-sources`, or use the `nuget.config` in [Platform restore behind a blocked feed](#platform-restore-behind-a-blocked-feed) below, which clears inherited sources entirely.

!!! warning "`dotnet pack` itself needs to restore"
    Building the package restores BotNexus's own dependencies. On a machine that has never restored this repository and cannot reach any feed, complete [Platform restore behind a blocked feed](#platform-restore-behind-a-blocked-feed) first, or run `dotnet pack` once on a connected machine and copy the resulting `.nupkg` across.

---

## Option 2 — Install from an internal mirror

If your organisation runs an internal NuGet feed (Azure Artifacts, Nexus, ProGet, a file share) that hosts `BotNexus.Cli`, point the install at it directly:

```bash
dotnet tool install -g BotNexus.Cli --source https://nuget.internal.example.com/v3/index.json
```

`--source` **replaces** the feed list for that command, so nuget.org is never contacted. A local directory or UNC path is a valid source too:

```bash
dotnet tool install -g BotNexus.Cli --source \\fileserver\packages\botnexus
```

For an authenticated feed, configure credentials once with a `nuget.config` (see below) rather than passing them on the command line.

---

## Option 3 — Install to a private tool path

If you cannot write to the global tool store (`~/.dotnet/tools`), install to a directory you control with `--tool-path`:

```bash
dotnet pack src/gateway/BotNexus.Cli -c Release -o ./artifacts
dotnet tool install BotNexus.Cli --tool-path ~/bin/botnexus --add-source ./artifacts
```

Note there is no `-g` when using `--tool-path` — the two are mutually exclusive. The tool is then invoked by its full path, or add the directory to `PATH`:

```bash
~/bin/botnexus/botnexus --version
```

---

## Option 4 — Run from source without installing a tool

You do not have to install a tool at all. Every CLI command is available by running the project directly from a clone:

```bash
dotnet run --project src/gateway/BotNexus.Cli -- init
dotnet run --project src/gateway/BotNexus.Cli -- provider setup
dotnet run --project src/gateway/BotNexus.Cli -- gateway start
```

This is the same invocation used in [Install from Release](../getting-started-release.md) for the initial platform build, and it is the lowest-friction option for a one-off or a locked-down machine. The trade-off is that every command carries a build check, so it is slower than an installed tool.

---

## Platform restore behind a blocked feed

Installing the CLI is only half the job. `botnexus install --build` clones and builds the BotNexus platform, and `dotnet build BotNexus.slnx` restores its dependencies — both need package access of their own. A CLI installed from a local `.nupkg` will still fail at that step if restore cannot reach a feed.

Create a `nuget.config` at the root of the cloned repository:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <!-- clear drops every inherited source, including nuget.org -->
    <clear />
    <add key="internal" value="https://nuget.internal.example.com/v3/index.json" />
    <!-- or a local mirror directory:
    <add key="local" value="C:\packages\mirror" /> -->
  </packageSources>
</configuration>
```

The `<clear />` element is the important part: without it, sources inherited from the machine-level and user-level `nuget.config` files (which include nuget.org) are still probed and will fail or stall.

Verify restore succeeds before going further:

```bash
dotnet restore BotNexus.slnx
dotnet build BotNexus.slnx
```

To discover which sources are actually in effect:

```bash
dotnet nuget list source
```

---

## Updating and uninstalling

An offline-installed tool does not know about any remote feed, so updates must name the source again.

**Update from a freshly packed local directory:**

```bash
git pull
dotnet pack src/gateway/BotNexus.Cli -c Release -o ./artifacts
dotnet tool update -g BotNexus.Cli --add-source ./artifacts
```

**Update from an internal mirror:**

```bash
dotnet tool update -g BotNexus.Cli --source https://nuget.internal.example.com/v3/index.json
```

**Uninstall:**

```bash
dotnet tool uninstall -g BotNexus.Cli

# or, for a --tool-path install
dotnet tool uninstall BotNexus.Cli --tool-path ~/bin/botnexus
```

**List what is installed:**

```bash
dotnet tool list -g
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `NU1301: Unable to load the service index` | A configured feed is unreachable. | If it is permanently blocked, use `--source` (Option 2) or a `nuget.config` with `<clear />` — clearing the cache will not help. |
| `NU1101: Unable to find package BotNexus.Cli` | The source was reachable but does not contain the package. | Confirm the `.nupkg` is in the `--add-source` directory (`ls ./artifacts`) and that the directory path is correct. |
| `--tool-path` and `-g` rejected together | They are mutually exclusive. | Drop `-g` when using `--tool-path`. |
| `botnexus: command not found` after a `--tool-path` install | The directory is not on `PATH`. | Add it to `PATH`, or invoke the executable by full path. |
| Install succeeds but `botnexus install --build` fails to restore | The CLI is installed; the *platform build* still cannot reach a feed. | See [Platform restore behind a blocked feed](#platform-restore-behind-a-blocked-feed). |

---

## See also

- [Install from Release](../getting-started-release.md) — the standard nuget.org install
- [Developer Setup](../getting-started-dev.md) — building from source for development
- [Troubleshooting](../user-guide/troubleshooting.md) — general build and runtime problems
