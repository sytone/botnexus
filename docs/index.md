# BotNexus

**Run AI agents at scale.** BotNexus is a modular, extensible platform for multi-agent AI orchestration built in C#/.NET. Deploy agents across Discord, Slack, REST APIs, and more — powered by Copilot, OpenAI, Anthropic, or any LLM provider.

## Quick Start

```bash
git clone https://github.com/your-org/botnexus.git
cd botnexus
dotnet build BotNexus.slnx
dotnet run --project src/gateway/BotNexus.Gateway.Api
# Open http://localhost:5005
```

## Choose Your Path

=== "🚀 New to BotNexus?"
    Get up and running in minutes.  
    **[→ Getting Started Guide](getting-started.md)**

=== "⚙️ Setting Up & Configuring?"
    Learn deployment, configuration, and providers.  
    **[→ Configuration Reference](configuration.md)**

=== "🔧 Building Extensions?"
    Create custom providers, channels, and tools.  
    **[→ Extension Development](extension-development.md)**

=== "👨‍💻 Contributing?"
    Build from source, run tests, and contribute.  
    **[→ Developer Guide](dev-guide.md)**

## Key Features

- **Multi-Agent Orchestration** — Run multiple independent agents with separate configs
- **Multi-Provider LLM Support** — Copilot (26 models), OpenAI, Anthropic, Azure OpenAI, and custom endpoints
- **Model-Aware Routing** — Automatic API format detection and request routing per model
- **Multi-Channel Integration** — Discord, Slack, Telegram, REST API, and SignalR streaming
- **Extensible Architecture** — Dynamic assembly loading with folder-based plugin system
- **Skills & MCP** — Modular knowledge packages and Model Context Protocol server support
- **Session Persistence** — Durable conversation history in JSONL format with hot reload

## Explore the Docs

| Section | Purpose |
|---------|---------|
| [User Guide](user-guide/getting-started.md) | Installation, setup, and basic usage |
| [API Reference](api-reference.md) | REST and SignalR endpoint documentation |
| [Architecture](architecture/overview.md) | System design, components, and extension points |
| [CLI Reference](cli-reference.md) | Command-line tool for configuration and management |
| [Observability](observability.md) | Tracing, logging, and monitoring |

---

*BotNexus is a .NET 10 project. For source builds, see the [Developer Guide](dev-guide.md).*
