# BotNexus CLI

The `botnexus` CLI installs, configures, and runs the BotNexus AI agent platform.

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| **.NET SDK** | **10.0 or later** | Required. Earlier versions (including .NET 9) are not supported. |
| **GitHub account** | — | Required for the default `github-copilot` provider (active Copilot subscription needed). |

> **Common install error:** If you see `DotnetToolSettings.xml was not found` during
> `dotnet tool install`, your .NET SDK is too old. BotNexus targets `net10.0` and
> requires the .NET 10 SDK. Download it from
> <https://dotnet.microsoft.com/download/dotnet/10.0>.

## Quick Start

```bash
# Install the CLI as a global .NET tool
dotnet tool install -g BotNexus.Cli

# Initialize platform directories and default config
botnexus init

# Set up your first LLM provider (interactive)
botnexus provider setup

# Validate configuration
botnexus validate

# Start the gateway (serves WebUI at http://localhost:5005)
botnexus gateway start
```

## Updating

```bash
dotnet tool update -g BotNexus.Cli
botnexus gateway restart
```

## Documentation

Full documentation: <https://github.com/sytone/botnexus>

## License

MIT
