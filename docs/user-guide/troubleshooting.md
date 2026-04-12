# Troubleshooting Guide

This guide helps you diagnose and resolve common issues with BotNexus.

## Table of Contents

1. [Build Failures](#build-failures)
2. [Gateway Startup Issues](#gateway-startup-issues)
3. [Agent Not Responding](#agent-not-responding)
4. [Provider Authentication Errors](#provider-authentication-errors)
5. [WebUI Connection Issues](#webui-connection-issues)
6. [Tool Execution Failures](#tool-execution-failures)
7. [Extension Loading Errors](#extension-loading-errors)
8. [Performance Issues](#performance-issues)
9. [Logs and Diagnostics](#logs-and-diagnostics)

---

## Build Failures

### Error: SDK Not Found

**Symptom:**
```
error : The SDK 'Microsoft.NET.Sdk' specified could not be found.
```

**Cause:** .NET SDK not installed or wrong version.

**Solution:**
1. Install .NET 10 SDK from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
2. Verify installation:
   ```bash
   dotnet --version
   # Should show: 10.0.x
   ```

### Error: Package Restore Failed

**Symptom:**
```
error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json
```

**Cause:** Network issue or NuGet offline.

**Solution:**
1. Check network connectivity
2. Clear NuGet cache:
   ```bash
   dotnet nuget locals all --clear
   ```
3. Retry build:
   ```bash
   dotnet restore
   dotnet build
   ```

### Error: Missing Project Reference

**Symptom:**
```
error CS0246: The type or namespace name 'BotNexus' could not be found
```

**Cause:** Project reference not restored.

**Solution:**
1. Ensure all projects exist:
   ```bash
   dotnet sln list
   ```
2. Restore and rebuild:
   ```bash
   dotnet restore BotNexus.slnx
   dotnet build BotNexus.slnx
   ```

### Error: Compilation Failed

**Symptom:**
```
error CS1061: 'Type' does not contain a definition for 'Property'
```

**Cause:** Breaking API change or version mismatch.

**Solution:**
1. Pull latest changes:
   ```bash
   git pull origin main
   ```
2. Clean and rebuild:
   ```bash
   dotnet clean
   dotnet build
   ```

---

## Gateway Startup Issues

### Error: Gateway Won't Start

**Symptom:**
```
Application startup exception
Unhandled exception. System.IO.FileNotFoundException: Could not load file or assembly
```

**Cause:** Missing dependencies or corrupted build output.

**Solution:**
1. Clean build output:
   ```bash
   dotnet clean
   rm -rf src/gateway/BotNexus.Gateway.Api/bin/
   rm -rf src/gateway/BotNexus.Gateway.Api/obj/
   ```
2. Rebuild:
   ```bash
   dotnet build BotNexus.slnx
   ```
3. Run again:
   ```bash
   dotnet run --project src/gateway/BotNexus.Gateway.Api
   ```

### Error: Port Already in Use

**Symptom:**
```
System.IO.IOException: Failed to bind to address http://127.0.0.1:5005
Error: address already in use
```

**Cause:** Port 5005 is already in use by another process.

**Solution:**

**Option 1: Use a different port**
Edit `~/.botnexus/config.json`:
```json
{
  "gateway": {
    "listenUrl": "http://localhost:5006"
  }
}
```

**Option 2: Kill the process using the port**

On Windows:
```powershell
netstat -ano | findstr :5005
taskkill /PID <PID> /F
```

On macOS/Linux:
```bash
lsof -ti :5005 | xargs kill -9
```

### Error: Configuration File Not Found

**Symptom:**
```
warn: BotNexus.Gateway.Configuration[0]
      Configuration file not found at ~/.botnexus/config.json
```

**Cause:** First run, config not initialized.

**Solution:**
This is normal on first run. BotNexus creates a default config automatically. To manually initialize:

```bash
mkdir -p ~/.botnexus
cat > ~/.botnexus/config.json <<EOF
{
  "version": 1,
  "gateway": {
    "listenUrl": "http://localhost:5005"
  }
}
EOF
```

### Error: Invalid JSON in config.json

**Symptom:**
```
error: BotNexus.Gateway.Configuration[0]
      Failed to parse configuration file: Unexpected character encountered while parsing value
```

**Cause:** Malformed JSON in `config.json`.

**Solution:**
1. Validate JSON syntax:
   ```bash
   # Using Python
   python -m json.tool ~/.botnexus/config.json

   # Using jq
   jq . ~/.botnexus/config.json
   ```
2. Fix syntax errors (missing commas, quotes, braces)
3. Save and retry

---

## Agent Not Responding

### Agent Not Listed in WebUI

**Symptom:** Agent doesn't appear in the WebUI dropdown.

**Cause:**
1. Agent not enabled
2. Configuration error
3. Gateway hasn't reloaded config

**Solution:**

**Check agent is enabled:**
```json
{
  "agents": {
    "my-agent": {
      "enabled": true  // ← Must be true
    }
  }
}
```

**Verify via API:**
```bash
curl http://localhost:5005/api/agents
```

**Force config reload:**
Save `config.json` (even without changes) to trigger hot reload, or restart Gateway:
```bash
# Restart
Ctrl+C
dotnet run --project src/gateway/BotNexus.Gateway.Api
```

### Agent Returns No Response

**Symptom:** Message sent, but agent doesn't respond (timeout or empty response).

**Cause:**
1. Provider not configured
2. Invalid API key
3. Model not available
4. Network issue

**Solution:**

**Check provider configuration:**
```bash
curl http://localhost:5005/api/providers
```

**Verify API key:**
```json
{
  "providers": {
    "copilot": {
      "apiKey": "auth:copilot",  // ← For OAuth
      "enabled": true
    },
    "anthropic": {
      "apiKey": "${ANTHROPIC_API_KEY}",  // ← Check env var set
      "enabled": true
    }
  }
}
```

**Test provider authentication:**
```bash
# For Copilot (OAuth)
cat ~/.botnexus/tokens/copilot.json

# For Anthropic (API key)
curl https://api.anthropic.com/v1/messages \
  -H "x-api-key: $ANTHROPIC_API_KEY" \
  -H "anthropic-version: 2023-06-01"
```

**Check logs:**
```bash
tail -f ~/.botnexus/logs/gateway.log | grep -i error
```

### Agent Responds Slowly

**Symptom:** Agent takes a long time to respond (>30 seconds).

**Cause:**
1. Model is slow (e.g., large model)
2. Many tools or large system prompt
3. Network latency

**Solution:**

**Use a faster model:**
```json
{
  "model": "gpt-4o-mini"  // Faster than gpt-4.1
}
```

**Reduce system prompt size:**
- Remove unnecessary `systemPromptFiles`
- Shorten prompt content

**Reduce tool count:**
- Only grant tools the agent needs
- Use `toolIds` to limit available tools

---

## Provider Authentication Errors

### GitHub Copilot OAuth Failed

**Symptom:**
```
error: BotNexus.Providers.Copilot[0]
      OAuth authentication failed: invalid_grant
```

**Cause:** OAuth token expired or invalid.

**Solution:**

**Re-authenticate:**
1. Delete stored token:
   ```bash
   rm ~/.botnexus/tokens/copilot.json
   ```
2. Restart Gateway — you'll be prompted to re-authenticate
3. Follow OAuth flow in browser

### Anthropic API Key Invalid

**Symptom:**
```
error: BotNexus.Providers.Anthropic[0]
      API request failed: 401 Unauthorized
```

**Cause:** Invalid or missing API key.

**Solution:**

**Verify API key:**
1. Check environment variable:
   ```bash
   echo $ANTHROPIC_API_KEY
   ```
2. Update in config:
   ```json
   {
     "providers": {
       "anthropic": {
         "apiKey": "${ANTHROPIC_API_KEY}"
       }
     }
   }
   ```
3. Export environment variable:
   ```bash
   export ANTHROPIC_API_KEY="sk-ant-..."
   ```

### OpenAI Rate Limit Exceeded

**Symptom:**
```
error: BotNexus.Providers.OpenAI[0]
      API request failed: 429 Too Many Requests
```

**Cause:** Exceeded OpenAI rate limits.

**Solution:**

**Option 1: Wait for rate limit reset**
OpenAI rate limits reset after a time window (typically 1 minute).

**Option 2: Upgrade OpenAI plan**
Higher-tier plans have higher rate limits.

**Option 3: Use a different provider**
```json
{
  "agents": {
    "assistant": {
      "provider": "copilot",  // ← Switch to Copilot
      "model": "gpt-4.1"
    }
  }
}
```

---

## WebUI Connection Issues

### WebUI Not Loading

**Symptom:** Browser shows "This site can't be reached" or ERR_CONNECTION_REFUSED.

**Cause:**
1. Gateway not running
2. Wrong URL
3. Firewall blocking port

**Solution:**

**Verify Gateway is running:**
```bash
curl http://localhost:5005/health
```

**Expected response:**
```json
{"status":"Healthy"}
```

**Check listen URL in config:**
```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005"
  }
}
```

**Check firewall:**
- Windows: Allow port 5005 in Windows Defender Firewall
- macOS/Linux: Check iptables/firewalld rules

### WebUI Loads But No Agents

**Symptom:** WebUI loads, but agent dropdown is empty.

**Cause:** No agents configured or all agents disabled.

**Solution:**

**Check agents via API:**
```bash
curl http://localhost:5005/api/agents
```

**Enable agents:**
```json
{
  "agents": {
    "assistant": {
      "enabled": true
    }
  }
}
```

### WebUI Disconnects Frequently

**Symptom:** WebUI shows "Disconnected" frequently.

**Cause:**
1. Network instability
2. Gateway restarting
3. SignalR connection timeout

**Solution:**

**Check Gateway logs for restarts:**
```bash
tail -f ~/.botnexus/logs/gateway.log
```

**Increase SignalR timeout** (if applicable):
```json
{
  "channels": {
    "webui": {
      "type": "signalr",
      "settings": {
        "keepAliveIntervalSeconds": 15,
        "clientTimeoutSeconds": 30
      }
    }
  }
}
```

---

## Tool Execution Failures

### Tool Not Found

**Symptom:**
```
error: Tool 'web_search' not found
```

**Cause:**
1. Tool not in agent's `toolIds`
2. Tool extension not loaded
3. Extension disabled

**Solution:**

**Add tool to agent:**
```json
{
  "agents": {
    "assistant": {
      "toolIds": ["web_search"]
    }
  }
}
```

**Verify tool is available:**
```bash
curl http://localhost:5005/api/tools
```

**Check extension is loaded:**
```bash
ls -la ~/.botnexus/extensions/
```

### Tool Execution Timeout

**Symptom:**
```
error: Tool execution timed out after 60 seconds
```

**Cause:** Tool operation taking too long.

**Solution:**

**For MCP tools, increase timeout:**
```json
{
  "extensions": {
    "botnexus-mcp": {
      "servers": {
        "filesystem": {
          "callTimeoutMs": 120000  // 2 minutes
        }
      }
    }
  }
}
```

**For custom tools, optimize logic:**
- Add caching
- Use async/await properly
- Reduce data processing

### Tool Returns Error

**Symptom:**
```
error: Tool 'read_file' failed: File not found
```

**Cause:** Tool-specific error (e.g., file doesn't exist).

**Solution:**

**Check tool input:**
- Verify file paths are correct
- Ensure permissions are set
- Validate input parameters

**Review tool logs:**
```bash
tail -f ~/.botnexus/logs/gateway.log | grep -i tool
```

---

## Extension Loading Errors

### Extension Not Loaded

**Symptom:**
```
warn: BotNexus.Gateway[0]
      Extension 'my-extension' not found
```

**Cause:**
1. Extension not in `~/.botnexus/extensions/`
2. Missing `botnexus-extension.json`
3. Assembly not compatible

**Solution:**

**Check extension directory:**
```bash
ls -la ~/.botnexus/extensions/my-extension/
```

**Verify manifest exists:**
```bash
cat ~/.botnexus/extensions/my-extension/botnexus-extension.json
```

**Ensure assembly targets .NET 10.0:**
Check `.csproj`:
```xml
<TargetFramework>net10.0</TargetFramework>
```

### Extension Assembly Load Failed

**Symptom:**
```
error: BotNexus.Gateway[0]
      Failed to load extension assembly: Could not load file or assembly
```

**Cause:**
1. Missing dependencies
2. Version mismatch
3. Assembly corruption

**Solution:**

**Rebuild extension:**
```bash
cd my-extension
dotnet clean
dotnet build -c Release
```

**Copy all dependencies:**
```bash
cp -r bin/Release/net10.0/* ~/.botnexus/extensions/my-extension/
```

**Check dependencies:**
```bash
dotnet list package --include-transitive
```

---

## Performance Issues

### High Memory Usage

**Symptom:** Gateway using excessive memory (>1 GB).

**Cause:**
1. Many concurrent sessions
2. Large system prompts
3. Memory leak

**Solution:**

**Enable session compaction:**
```json
{
  "gateway": {
    "compaction": {
      "maxMessagesBeforeCompaction": 50,
      "retainLastMessages": 10
    }
  }
}
```

**Limit concurrent sessions:**
```json
{
  "agents": {
    "assistant": {
      "maxConcurrentSessions": 5
    }
  }
}
```

**Reduce system prompt size:**
- Remove unused `systemPromptFiles`
- Shorten skill content

### High CPU Usage

**Symptom:** Gateway using 100% CPU.

**Cause:**
1. Infinite loop in custom tool
2. Heavy concurrent load
3. MCP server spawning repeatedly

**Solution:**

**Check running processes:**
```bash
top -o %CPU
```

**Check MCP server logs:**
```bash
tail -f ~/.botnexus/logs/gateway.log | grep -i mcp
```

**Disable problematic extensions:**
```json
{
  "gateway": {
    "extensions": {
      "enabled": false  // Temporarily disable
    }
  }
}
```

### Slow Response Times

**Symptom:** Agent responses take >10 seconds.

**Cause:**
1. Large context window
2. Many tools
3. Slow model

**Solution:**

**Use faster model:**
```json
{
  "model": "gpt-4o-mini"  // or "claude-haiku-4.5"
}
```

**Reduce tool count:**
```json
{
  "toolIds": ["read_file", "write_file"]  // Only essential tools
}
```

**Enable caching** (if provider supports):
```json
{
  "providers": {
    "anthropic": {
      "enablePromptCaching": true
    }
  }
}
```

---

## Logs and Diagnostics

### Log Locations

**Gateway logs:**
```
~/.botnexus/logs/gateway.log
~/.botnexus/logs/gateway-YYYYMMDD.log
```

**Session logs:**
```
~/.botnexus/workspace/sessions/<agentId>/<sessionId>.jsonl
```

### Viewing Logs

**Real-time:**
```bash
tail -f ~/.botnexus/logs/gateway.log
```

**Filter by level:**
```bash
# Errors only
grep "error:" ~/.botnexus/logs/gateway.log

# Warnings and errors
grep -E "(warn:|error:)" ~/.botnexus/logs/gateway.log
```

**Recent logs via API:**
```bash
curl http://localhost:5005/api/logs/recent?count=100
```

### Increasing Log Verbosity

**Temporary (environment variable):**
```bash
export Logging__LogLevel__Default=Debug
dotnet run --project src/gateway/BotNexus.Gateway.Api
```

**Permanent (config.json):**
```json
{
  "gateway": {
    "logLevel": "Debug"
  }
}
```

**Log Levels:**
- `Trace`: Most verbose (include everything)
- `Debug`: Debugging information
- `Information`: General info (default)
- `Warning`: Warnings only
- `Error`: Errors only
- `Critical`: Critical errors only

### Health Check

**Check Gateway health:**
```bash
curl http://localhost:5005/health
```

**Response:**
```json
{
  "status": "Healthy",
  "timestamp": "2025-06-12T10:00:00Z",
  "checks": {
    "providers": "Healthy",
    "extensions": "Healthy",
    "sessions": "Healthy"
  }
}
```

### Distributed Tracing

BotNexus supports OpenTelemetry for distributed tracing:

**Enable Jaeger (local):**
```bash
# Start Jaeger
docker run -d --name jaeger \
  -p 16686:16686 \
  -p 4318:4318 \
  jaegertracing/all-in-one:latest

# Configure BotNexus
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
dotnet run --project src/gateway/BotNexus.Gateway.Api

# View traces
open http://localhost:16686
```

See [Observability Guide](../observability.md) for details.

---

## Getting Help

If you can't resolve an issue:

1. **Check documentation:**
   - [Getting Started](getting-started.md)
   - [Configuration Reference](configuration.md)
   - [Agent Setup](agents.md)
   - [Extension Development](extensions.md)

2. **Search existing issues:**
   - [GitHub Issues](https://github.com/your-org/botnexus/issues)

3. **Open a new issue:**
   - Include BotNexus version
   - Include .NET version (`dotnet --version`)
   - Include OS and version
   - Include relevant logs
   - Include `config.json` (redact API keys)
   - Describe steps to reproduce

4. **Community support:**
   - [Discussions](https://github.com/your-org/botnexus/discussions)
   - [Discord](https://discord.gg/botnexus) (if available)

---

## Next Steps

- [Configuration Reference](configuration.md) — Complete config options
- [API Reference](../api-reference.md) — REST and SignalR endpoints
- [Observability](../observability.md) — Tracing and monitoring
- [Developer Guide](../dev-guide.md) — Build and test locally
