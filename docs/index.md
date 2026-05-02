---
layout: home

hero:
  name: BotNexus
  text: Run AI agents at scale.
  tagline: A modular, extensible platform for multi-agent AI orchestration built in C#/.NET. Deploy agents across Discord, Slack, REST APIs, and more — powered by Copilot, OpenAI, Anthropic, or any LLM provider.
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/sytone/botnexus

features:
  - icon: 🤖
    title: Multi-Agent Orchestration
    details: Run multiple independent agents with separate configs and personalities.
  - icon: 🔌
    title: Multi-Provider LLM Support
    details: Copilot (26 models), OpenAI, Anthropic, Azure OpenAI, and custom endpoints.
  - icon: 🗺️
    title: Model-Aware Routing
    details: Automatic API format detection and request routing per model.
  - icon: 📡
    title: Multi-Channel Integration
    details: Discord, Slack, Telegram, REST API, and SignalR streaming.
  - icon: 🧩
    title: Extensible Architecture
    details: Dynamic assembly loading with folder-based plugin system.
  - icon: 💾
    title: Session Persistence
    details: Durable conversation history in JSONL format with hot reload.
---

## Quick Start

```bash
git clone https://github.com/sytone/botnexus.git
cd botnexus
dotnet build BotNexus.slnx
dotnet run --project src/gateway/BotNexus.Gateway.Api
# Open http://localhost:5005
```

## Choose Your Path

| Goal | Where to go |
|------|-------------|
| 🚀 New to BotNexus? | **[Getting Started Guide](getting-started)** |
| ⚙️ Setting Up & Configuring? | **[Configuration Reference](configuration)** |
| 🔧 Building Extensions? | **[Extension Development](extension-development)** |
| 👨‍💻 Contributing? | **[Developer Guide](getting-started-dev)** |

## Explore the Docs

| Section | Purpose |
|---------|---------|
| [User Guide](user-guide/getting-started) | Installation, setup, and basic usage |
| [API Reference](api-reference) | REST and SignalR endpoint documentation |
| [Architecture](architecture/overview) | System design, components, and extension points |
| [CLI Reference](cli-reference) | Command-line tool for configuration and management |
| [Observability](observability) | Tracing, logging, and monitoring |
| [Releases](releases/) | Version history and release notes |

---

*BotNexus is a .NET 10 project. For source builds, see the [Developer Guide](getting-started-dev).*
