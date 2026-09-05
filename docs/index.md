---
layout: home

hero:
  name: BotNexus
  text: Run AI agents at scale.
  tagline: A modular, extensible platform for multi-agent AI orchestration built in C#/.NET. Deploy agents across Telegram, Azure Service Bus, SignalR, REST APIs, and more — powered by Copilot, OpenAI, Anthropic, or compatible LLM endpoints.
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
    details: GitHub Copilot, GitHub Models, OpenAI, Anthropic, Ollama, and OpenAI-compatible endpoints.
  - icon: 🗺️
    title: Model-Aware Routing
    details: Automatic API format detection and request routing per model.
  - icon: 📡
    title: Multi-Channel Integration
    details: Telegram, Azure Service Bus, Agent 365, Matrix, REST API, and SignalR streaming.
  - icon: 🧩
    title: Extensible Architecture
    details: Dynamic assembly loading with folder-based plugin system.
  - icon: 💾
    title: Session Persistence
    details: Durable conversation history in JSONL format with hot reload.
  - icon: 🪝
    title: Inbound Webhooks
    details: Trigger agents over signed HTTP with async, sync, or callback responses.
---

## Quick Start

```bash
git clone https://github.com/sytone/botnexus.git
cd botnexus
dotnet build dirs.proj
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
