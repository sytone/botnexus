# Getting Started with BotNexus

This guide walks you through installing BotNexus, running it for the first time, and verifying everything works.

## Prerequisites

Before you begin, ensure you have:

- **.NET 10 SDK** — BotNexus targets .NET 10.0
  - Download from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
  - Verify installation: `dotnet --version` should show `10.0.x` or later

- **Operating System** — Windows, macOS, or Linux
  - Windows 10/11 (recommended for full feature set)
  - macOS 11+ (Big Sur or later)
  - Ubuntu 20.04+ or other modern Linux distributions

- **Git** — For cloning the repository
  - Download from [git-scm.com](https://git-scm.com/)

- **LLM Provider Access** (at least one):
  - **GitHub Copilot** subscription (recommended)
  - **Anthropic API** key (Claude models)
  - **OpenAI API** key (GPT models)
  - Any **OpenAI-compatible API** endpoint

## Installation

### 1. Clone the Repository

```bash
git clone https://github.com/your-org/botnexus.git
cd botnexus
```

### 2. Build the Solution

```bash
dotnet build BotNexus.slnx
```

This will:
- Restore NuGet packages
- Build all projects in the solution
- Compile extensions (MCP, Skills, Tools)
- Copy WebUI assets to the Gateway output directory

**Expected output:**
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

If you encounter build errors, see [Troubleshooting](troubleshooting.md#build-failures).

## First Run

### Option 1: Using the Dev Script (Recommended)

The quickest way to get started:

```powershell
# PowerShell (Windows/macOS/Linux)
.\scripts\start-gateway.ps1
```

This script:
- Builds the solution if needed
- Starts the Gateway on port 5005
- Opens the WebUI in your default browser

### Option 2: Direct Command

Run the Gateway API directly:

```bash
dotnet run --project src/gateway/BotNexus.Gateway.Api
```

**Expected output:**
```
info: BotNexus.Gateway[0]
      Starting BotNexus Gateway...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5005
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

## Accessing the WebUI

Once the Gateway is running:

1. **Open your browser** to `http://localhost:5005`
2. You should see the **BotNexus WebUI** — a real-time chat interface
3. The UI shows:
   - Available agents (initially none until configured)
   - Real-time activity stream
   - Command palette (type `/help` for commands)

## Verify Installation

### Health Check Endpoint

Check that the Gateway is healthy:

```bash
curl http://localhost:5005/health
```

**Expected response:**
```json
{
  "status": "Healthy",
  "timestamp": "2025-06-12T10:30:00Z"
}
```

### API Status

Check the API is responding:

```bash
curl http://localhost:5005/api/agents
```

**Expected response (before configuration):**
```json
{
  "agents": []
}
```

This is normal — you haven't configured any agents yet.

## Configuration

On first run, BotNexus creates a home directory at `~/.botnexus/` with a default configuration file.

### Home Directory Structure

```
~/.botnexus/
├── config.json          # Main configuration (agents, providers, channels)
├── extensions/          # Extension binaries (auto-populated)
├── tokens/              # OAuth tokens (GitHub Copilot)
├── workspace/
│   └── sessions/        # Conversation history (JSONL format)
└── logs/                # Application logs
```

### Configure Your First Provider

Edit `~/.botnexus/config.json` to add a provider:

**GitHub Copilot (OAuth):**
```json
{
  "version": 1,
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1",
      "enabled": true
    }
  }
}
```

**Anthropic (API Key):**
```json
{
  "version": 1,
  "providers": {
    "anthropic": {
      "apiKey": "sk-ant-...",
      "baseUrl": "https://api.anthropic.com",
      "defaultModel": "claude-sonnet-4-20250514",
      "enabled": true
    }
  }
}
```

**OpenAI (API Key):**
```json
{
  "version": 1,
  "providers": {
    "openai": {
      "apiKey": "sk-...",
      "baseUrl": "https://api.openai.com/v1",
      "defaultModel": "gpt-4o",
      "enabled": true
    }
  }
}
```

> **Security:** Never commit API keys to version control. BotNexus supports `auth:copilot` for OAuth and environment variable references like `${ANTHROPIC_API_KEY}`.

### Configure Your First Agent

Add an agent to `config.json`:

```json
{
  "version": 1,
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1",
      "enabled": true
    }
  },
  "agents": {
    "assistant": {
      "displayName": "Assistant",
      "description": "General-purpose AI assistant",
      "provider": "copilot",
      "model": "gpt-4.1",
      "systemPromptFiles": [],
      "toolIds": [],
      "isolationStrategy": "in-process",
      "enabled": true
    }
  },
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant"
  }
}
```

### Apply Configuration Changes

BotNexus supports **hot reload** — changes to `config.json` are applied automatically without restarting the Gateway.

Watch the logs for:
```
info: BotNexus.Gateway.Configuration[0]
      Configuration file changed, reloading...
info: BotNexus.Gateway[0]
      Agent 'assistant' registered successfully
```

## Test Your Agent

### Using the WebUI

1. Open `http://localhost:5005` in your browser
2. Select the **assistant** agent from the dropdown
3. Type a message: `Hello, can you help me?`
4. Press **Send**

You should see the agent respond in real-time.

### Using the REST API

Send a message to your agent:

```bash
curl -X POST http://localhost:5005/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "agentId": "assistant",
    "content": "Hello, can you help me?"
  }'
```

**Expected response:**
```json
{
  "sessionId": "01J...",
  "agentId": "assistant",
  "content": "Hello! Yes, I'd be happy to help you. What would you like assistance with?",
  "timestamp": "2025-06-12T10:35:00Z"
}
```

## Next Steps

Now that BotNexus is running:

1. **[Configure providers and agents](configuration.md)** — Learn about all configuration options
2. **[Create custom agents](agents.md)** — Define agents with system prompts and tools
3. **[Add extensions](extensions.md)** — Connect MCP servers, add custom tools, enable skills
4. **[Explore the API](../api-reference.md)** — REST endpoints and SignalR hub documentation

## Quick Reference

| Task | Command |
|------|---------|
| **Build** | `dotnet build BotNexus.slnx` |
| **Run Gateway** | `dotnet run --project src/gateway/BotNexus.Gateway.Api` |
| **Health Check** | `curl http://localhost:5005/health` |
| **List Agents** | `curl http://localhost:5005/api/agents` |
| **WebUI** | `http://localhost:5005` |
| **Config Location** | `~/.botnexus/config.json` |

## Troubleshooting

If you encounter issues:

- **Build Failures** — See [Build Failures](troubleshooting.md#build-failures)
- **Gateway Won't Start** — See [Gateway Startup Issues](troubleshooting.md#gateway-wont-start)
- **Agent Not Responding** — See [Agent Not Responding](troubleshooting.md#agent-not-responding)
- **Provider Authentication Errors** — See [Provider Authentication](troubleshooting.md#provider-authentication-errors)

For detailed troubleshooting, see the [Troubleshooting Guide](troubleshooting.md).
