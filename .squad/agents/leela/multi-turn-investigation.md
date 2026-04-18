# Multi-Turn Investigation Summary
**Date:** 2026-04-03T21:50:00Z  
**Agent:** Leela (Lead)  
**Task:** Debug multi-turn tool calling

## Issues Found

### 1. WebSocket Agent Routing Bug ✅ FIXED
**Problem:** WebSocket connections with `?agent=nova` query parameter were being routed to "assistant" agent instead.

**Root Cause:** `GatewayWebSocketHandler.HandleAsync()` was not reading the `agent` query parameter. The agent name was only extracted from the JSON message body (`message.agent` or `message.agent_name`).

**Fix:** 
- Extract `agent` query parameter in `HandleAsync()`
- Pass it through to `ReadFromClientAsync()` and `ToInboundMessage()`
- Priority order: `message.agent_name` > `message.agent` > query `?agent=`

**Commit:** `2c8bc05`

---

### 2. Multi-Turn Tool Calling Status
**Status:** Multi-turn IS working but exhibits infinite loop behavior

**What's Working:**
- Tools ARE being called (confirmed by logs showing "executing 1 tool calls")
- AgentLoop iterates correctly (iterations 0, 1, 2, 3, 4...)
- Tool results ARE saved to session history
- Tool results ARE converted to `ChatMessage` with `role: "tool"`, `ToolCallId`, and `ToolName`
- Copilot provider correctly serializes tool messages with `tool_call_id` and `name`

**Evidence from Logs:**
```
2026-04-03 14:46:13.812 [INF] Agent nova: executing 1 tool calls (iteration 0)
2026-04-03 14:46:17.043 [INF] Agent nova: executing 1 tool calls (iteration 1)
2026-04-03 14:46:19.840 [INF] Agent nova: executing 1 tool calls (iteration 2)
2026-04-03 14:46:22.724 [INF] Agent nova: executing 1 tool calls (iteration 3)
2026-04-03 14:46:25.336 [INF] Agent nova: executing 1 tool calls (iteration 4)
```

**Problem:** The agent calls the SAME tool repeatedly in an infinite loop, suggesting:
- Either the LLM is not seeing the tool results in the next request
- Or the tool results format is incorrect for the Copilot API
- Or there's a session/history management issue

---

## Investigation Steps Taken

1. **Checked logs** for Nova agent activity — Found WebSocket routing bug
2. **Read system prompt generation** (`AgentContextBuilder`) — Confirmed tool use instructions present
3. **Analyzed Pi agent** (badlogic/pi-mono) — Documented their multi-turn approach
4. **Traced tool result flow:**
   - `AgentLoop.ProcessAsync()` → Executes tools and saves results to session ✅
   - Tool results added with `MessageRole.Tool`, `ToolName`, `ToolCallId` ✅
   - Converted to `ChatMessage` objects with correct role and IDs ✅
   - Serialized by `CopilotProvider.BuildRequestPayload()` with `tool_call_id` and `name` ✅

5. **Added enhanced logging:**
   - CopilotProvider now logs tool counts and names at INFO level
   - Request/response payloads logged at DEBUG level
   - Set `BotNexus.Agent.Providers.Copilot` to Debug level in appsettings

---

## Next Steps

### A. Verify Request Payload
Enable DEBUG logging and capture the actual JSON being sent to Copilot API. Check:
- Are tool result messages included in the `messages` array?
- Is `tool_call_id` correctly referencing the previous assistant call ID?
- Are `tools` definitions present in subsequent requests?

### B. Test with Different Model
Nova uses `claude-opus-4.6` via Copilot proxy. Test with:
- A different Claude model
- GPT-4o (which assistant uses successfully)
- Check if it's a model-specific issue

### C. Inspect Session History
Add logging to show the full session history before each LLM call:
```csharp
_logger.LogDebug("Session history (iteration {Iteration}): {History}", 
    iteration, JsonSerializer.Serialize(history));
```

### D. Compare Against Working Example
The logs show "assistant" agent successfully completes multi-turn (iteration 1, FinishReason="Stop"). Compare:
- assistant config vs nova config
- assistant model (gpt-4o) vs nova model (claude-opus-4.6)
- What's different in their behavior?

### E. Check Pi Implementation Differences
Pi uses:
- Streaming by default even with tools
- Strict tool call ID normalization (40-char limit)
- Empty `tools: []` array required if messages reference past tool calls (Anthropic quirk)

---

## Key Code References

**AgentLoop tool execution:**
- `Q:\repos\botnexus\src\BotNexus.Agent\AgentLoop.cs:317-324`

**ChatMessage conversion:**
- `Q:\repos\botnexus\src\BotNexus.Agent\AgentLoop.cs:122-140`

**Copilot provider tool serialization:**
- `Q:\repos\botnexus\src\BotNexus.Agent.Providers.Copilot\CopilotProvider.cs:424-430`

**WebSocket routing fix:**
- `Q:\repos\botnexus\src\BotNexus.Gateway\GatewayWebSocketHandler.cs:49-77, 112-135, 208-236`

---

## Recommendation

**HIGH PRIORITY:** Capture and analyze the actual HTTP request body being sent to the Copilot API after the first tool execution. This will definitively show whether tool results are making it into the request or if there's a serialization issue.

**Test command:**
```powershell
.\test-nova-simple.ps1
# Then immediately check logs with DEBUG level enabled
Get-Content "$env:USERPROFILE\.botnexus\logs\botnexus-20260403.log" -Tail 200 | Select-String -Pattern "Request payload|tool_call_id"
```
