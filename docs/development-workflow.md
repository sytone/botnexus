# Development Workflow Guide

Quick reference for building, testing, and deploying BotNexus during development.

## Table of Contents

1. [Dev-Loop Script](#dev-loop-script)
2. [Build Process](#build-process)
3. [Testing](#testing)
4. [Installation](#installation)
5. [Common Tasks](#common-tasks)

---

## Dev-Loop Script

The `scripts/dev-loop.ps1` script automates build + test + Gateway startup for rapid local development.

### Usage

```powershell
# Build + test + start gateway on default port 5005
.\scripts\dev-loop.ps1

# Use a custom port
.\scripts\dev-loop.ps1 -Port 5100

# Watch source changes and restart gateway automatically
.\scripts\dev-loop.ps1 -Watch
```

### What It Does

**3-Step Development Loop:**

1. **Build solution** — runs `dotnet build BotNexus.slnx`
2. **Run Gateway tests** — runs `dotnet test tests/BotNexus.Gateway.Tests`
3. **Start Gateway** — runs `scripts/start-gateway.ps1` (or `dotnet watch ... run` with `-Watch`)

### Output

```
🔧 Building full solution...
✅ Build succeeded
🧪 Running Gateway tests...
✅ Tests passed
🚀 Starting Gateway API
   URL: http://localhost:5005
   WebUI: http://localhost:5005/webui

Press Ctrl+C to stop.
```

### Configuration

- **Port:** Defaults to `5005`, configurable via `-Port`
- **Environment:** Sets `ASPNETCORE_ENVIRONMENT=Development`
- **Gateway URL:** Sets `ASPNETCORE_URLS=http://localhost:<port>`

### Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `BOTNEXUS_HOME` | BotNexus home directory | `~/.botnexus` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET environment | `Development` (set by script) |
| `ASPNETCORE_URLS` | Gateway listen URL | `http://localhost:5005` (set by script) |

---

## Build Process

### Manual Build Steps

**1. Build the entire solution:**
```powershell
dotnet build BotNexus.slnx
```

**2. Run tests:**
```powershell
dotnet test BotNexus.slnx
```

**3. Pack components:**
```powershell
.\scripts\pack.ps1
```

**4. Install extensions:**
```powershell
.\scripts\install.ps1 -InstallPath ~/.botnexus/extensions
```

**5. Start the Gateway:**
```powershell
dotnet run --project src\gateway\BotNexus.Gateway.Api\BotNexus.Gateway.Api.csproj
```

### Using dev-loop.ps1 (Recommended)

For a single command that does all of the above:

```powershell
.\scripts\dev-loop.ps1
```

---

## Testing

### Run All Tests

```powershell
dotnet test BotNexus.slnx
```

### Run Tests for a Specific Project

```powershell
dotnet test tests/BotNexus.Agent.Tests/
```

### Run a Specific Test

```powershell
dotnet test BotNexus.slnx --filter "FullyQualifiedName=BotNexus.Agent.Tests.AgentLoopTests.TestName"
```

### Watch Mode (Auto-Rerun on File Changes)

```powershell
dotnet watch test --project tests/BotNexus.Agent.Tests/
```

---

## Installation

### Install CLI Tool Globally

```powershell
dotnet tool install --global --add-source ./src/BotNexus.Cli/bin/Release/net10.0 botnexus
```

### Upgrade CLI Tool

```powershell
dotnet tool update --global --add-source ./src/BotNexus.Cli/bin/Release/net10.0 botnexus
```

### Verify Installation

```powershell
botnexus --version
botnexus --help
```

### Install Extensions to Home Directory

```powershell
.\scripts\install.ps1
```

Or specify a custom path:

```powershell
.\scripts\install.ps1 -InstallPath "C:\MyPath\botnexus\extensions"
```

---

## Common Tasks

### Start Development Environment

```powershell
# Full build, pack, and start (recommended)
.\scripts\dev-loop.ps1

# Then in another terminal, open WebUI
start http://localhost:18790
```

### Add a New Agent

```powershell
# Create agent via API
$body = @{
    name = "my-agent"
    systemPrompt = "You are helpful"
    model = "gpt-4o"
    provider = "openai"
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:18790/api/agents" `
  -Method Post `
  -Body $body `
  -Headers @{ "X-Api-Key" = "your-key"; "Content-Type" = "application/json" }
```

Or use CLI (if available):

```powershell
botnexus agent add --name my-agent --model gpt-4o --provider openai
```

### View Gateway Logs

```powershell
# View recent logs
Get-Content ~/.botnexus/logs/gateway.log -Tail 50

# Follow logs in real-time (requires PowerShell 7+)
Get-Content ~/.botnexus/logs/gateway.log -Tail 50 -Wait
```

### Stop the Gateway

```powershell
# Kill by name
Stop-Process -Name "dotnet" -ProcessName "*BotNexus.Gateway*"

# Or by PID (from dev-loop output)
Stop-Process -Id 12345
```

### Clean Build (Remove All Artifacts)

```powershell
# Remove bin/ and obj/ folders
Remove-Item -Path "src/*/bin", "src/*/obj", "tests/*/bin", "tests/*/obj" -Recurse -Force

# Full rebuild
dotnet clean BotNexus.slnx
dotnet build BotNexus.slnx
```

### Run Doctor Diagnostics

```powershell
# Run health checks
botnexus doctor

# Check specific category
botnexus doctor --category startup
```

### Reset Configuration

```powershell
# Backup current config
Copy-Item ~/.botnexus/config.json ~/.botnexus/config.json.backup

# Reinitialize
botnexus config init
```

---

## Troubleshooting

### Port Already in Use

If port 18790 is already in use:

```powershell
# Find process using port 18790
netstat -ano | findstr :18790

# Kill the process
taskkill /PID <PID> /F

# Or use a different port in config.json
```

### CLI Tool Not Found

```powershell
# Ensure it's installed
dotnet tool list --global | findstr botnexus

# If missing, reinstall
dotnet tool install --global --add-source ./src/BotNexus.Cli/bin/Release/net10.0 botnexus
```

### Extensions Not Loading

```powershell
# Check extension path
botnexus config show | findstr ExtensionsPath

# Verify extensions are in correct location
Get-ChildItem ~/.botnexus/extensions/providers/
Get-ChildItem ~/.botnexus/extensions/channels/
Get-ChildItem ~/.botnexus/extensions/tools/
```

### OAuth Token Expired

```powershell
# Remove expired token
Remove-Item ~/.botnexus/tokens/copilot.json

# Next OAuth provider use will re-authenticate
botnexus login --provider copilot
```

---

## Next Steps

- **[API Reference](api-reference.md)** — REST API endpoints for agents and sessions
- **[Configuration Guide](configuration.md)** — Detailed configuration options
- **[Extension Development](extension-development.md)** — Build custom tools and providers
