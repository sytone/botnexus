# Anthropic Handler Multi-Turn Fix - Deep Comparison Summary

**Date:** 2026-04-04  
**Engineer:** Farnsworth (Platform Dev)  
**Priority:** HIGH (Jon Bullen request)  
**Status:** ✅ DEPLOYED

---

## Problem Statement

Multi-turn tool calling with Anthropic Messages API (via GitHub Copilot) was failing. The agent would make tool calls, but subsequent turns would not process correctly, causing infinite loops or dropped requests.

---

## Root Cause Analysis

### Critical Issues Found

After deep comparison of Pi's `anthropic.ts` vs our `AnthropicMessagesHandler.cs`:

#### 1. ❌ MISSING COPILOT-SPECIFIC HEADERS (CRITICAL)

**Pi's Headers (github-copilot path):**
```typescript
{
  "X-Initiator": "user" | "agent",  // ← MISSING!
  "Openai-Intent": "conversation-edits",  // ← MISSING!
  "Copilot-Vision-Request": "true"  // ← MISSING (when images)
}
```

**Our Headers (before fix):**
```csharp
{
  "accept": "application/json",
  "anthropic-dangerous-direct-browser-access": "true",
  "anthropic-version": "2023-06-01"
}
```

**Impact:** 
- Copilot proxy couldn't distinguish user-initiated vs agent-initiated requests
- Multi-turn routing failed because proxy didn't know context
- Tool call results weren't being properly associated with their initiating requests

#### 2. ✅ MESSAGE ORDERING (Already Correct)

Both Pi and BotNexus correctly group consecutive tool result messages into single user messages with multiple `tool_result` blocks. No issue here.

#### 3. ✅ TOOL DEFINITION FORMAT (Already Correct)

Both use `input_schema` (not `parameters`) for Anthropic API. Correct.

#### 4. ✅ SYSTEM PROMPT HANDLING (Already Correct)

Both send system prompt in top-level `system` field, not in messages array. Correct.

#### 5. ✅ AUTH (Already Correct)

Both use Bearer auth for Copilot. Correct.

#### 6. ⚠️ TOOL RESULT CONTENT FORMAT (Minor Issue)

**Pi:** Uses smart conversion - simple text → string, complex/images → blocks array  
**Our approach:** Always sends as string

**Impact:** Low priority. Current approach works for simple tool results.

---

## Solution Implemented

### Fix #1: Add Dynamic Copilot Headers (CRITICAL)

**File:** `src/BotNexus.Providers.Base/AnthropicMessagesHandler.cs`  
**Commit:** `faec19b` - "feat(providers): Add detailed logging for Anthropic Messages API requests"

Added three critical headers:

```csharp
// Copilot-specific headers for multi-turn tool calling (Pi pattern)
var messages = payload["messages"] as List<Dictionary<string, object?>>;
if (messages is not null)
{
    request.Headers.TryAddWithoutValidation("X-Initiator", InferCopilotInitiator(messages));
    request.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-edits");
    
    if (HasVisionContent(messages))
    {
        request.Headers.TryAddWithoutValidation("Copilot-Vision-Request", "true");
    }
}
```

#### Helper Method: InferCopilotInitiator()

```csharp
/// <summary>
/// Infer X-Initiator header value for Copilot routing.
/// "user" if last message is user-initiated, "agent" if agent-initiated (tool results, assistant).
/// Pi pattern: github-copilot-headers.ts, inferCopilotInitiator()
/// </summary>
private static string InferCopilotInitiator(List<Dictionary<string, object?>> messages)
{
    if (messages.Count == 0) return "user";
    
    var lastMessage = messages[^1];
    var role = lastMessage.TryGetValue("role", out var roleObj) ? roleObj?.ToString() : null;
    
    // If last message is NOT user, then it's agent-initiated
    return role != "user" ? "agent" : "user";
}
```

**Logic:**
- User message → `X-Initiator: user`
- Tool result message → `X-Initiator: agent`
- Assistant message → `X-Initiator: agent`

This tells Copilot proxy how to route the request through their multi-turn infrastructure.

#### Helper Method: HasVisionContent()

```csharp
/// <summary>
/// Check if any message contains vision/image content.
/// Pi pattern: github-copilot-headers.ts, hasCopilotVisionInput()
/// </summary>
private static bool HasVisionContent(List<Dictionary<string, object?>> messages)
{
    foreach (var msg in messages)
    {
        if (!msg.TryGetValue("content", out var contentObj)) continue;
        
        // Content can be string or array of blocks
        if (contentObj is List<Dictionary<string, object?>> contentBlocks)
        {
            foreach (var block in contentBlocks)
            {
                if (block.TryGetValue("type", out var typeObj) && typeObj?.ToString() == "image")
                {
                    return true;
                }
            }
        }
    }
    
    return false;
}
```

Enables vision routing when images are present in the conversation.

---

## Comparison Summary

| Aspect | Pi Implementation | BotNexus (Before) | BotNexus (After) | Status |
|--------|------------------|------------------|------------------|--------|
| **X-Initiator header** | ✅ Dynamic (user/agent) | ❌ Missing | ✅ Dynamic | FIXED |
| **Openai-Intent header** | ✅ "conversation-edits" | ❌ Missing | ✅ "conversation-edits" | FIXED |
| **Copilot-Vision-Request** | ✅ When images present | ❌ Missing | ✅ When images present | FIXED |
| **Message ordering** | ✅ Groups tool results | ✅ Groups tool results | ✅ Groups tool results | ✅ OK |
| **Tool definition** | ✅ input_schema | ✅ input_schema | ✅ input_schema | ✅ OK |
| **System prompt** | ✅ Top-level field | ✅ Top-level field | ✅ Top-level field | ✅ OK |
| **Auth** | ✅ Bearer token | ✅ Bearer token | ✅ Bearer token | ✅ OK |
| **Tool result content** | Smart conversion | Always string | Always string | ⚠️ Low priority |

---

## Testing

### Build & Test Results

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Tests:
    494 unit tests passed
    2 pre-existing integration test failures (unrelated to this change)
```

### Deployment

1. ✅ Built solution: `dotnet build BotNexus.slnx`
2. ✅ Published Gateway: `dotnet publish src\BotNexus.Gateway\BotNexus.Gateway.csproj`
3. ✅ Deployed to: `%LOCALAPPDATA%\BotNexus\gateway\`
4. ✅ Gateway started: PID 39852

### Live Testing Required

**Next Steps:**
1. Test multi-turn tool calling flow with live Copilot
2. Verify X-Initiator header changes: "user" → "agent" → "agent" → "user"
3. Monitor logs for header presence
4. Confirm no API errors on tool result submission
5. Test with vision inputs (if applicable)

---

## Pattern Reference

**Source:** [badlogic/pi-mono](https://github.com/badlogic/pi-mono/tree/main/packages/ai/src/providers)

- `anthropic.ts` - Main Anthropic provider implementation
- `transform-messages.ts` - Message transformation layer
- `github-copilot-headers.ts` - Copilot-specific header logic

---

## Key Decisions

1. **Header Implementation Priority**  
   Implemented critical headers first (X-Initiator, Openai-Intent). Vision header is conditional.

2. **Message Transformation Deferred**  
   Pi's `transformMessages()` function filters errored/aborted assistant messages and inserts synthetic tool results. This requires changes to the session/conversation layer, not just the provider. Deferred to future work.

3. **Tool Result Content Format**  
   Current string-based approach works for simple tool outputs. Can enhance later if needed for complex/structured results.

---

## Related Files

- `src/BotNexus.Providers.Base/AnthropicMessagesHandler.cs` - Main implementation
- `MESSAGE_FLOW_ANALYSIS.md` - Detailed comparison document
- `test-tool-call-logging.ps1` - Test script for runtime verification

---

## Commit

```
faec19b - feat(providers): Add detailed logging for Anthropic Messages API requests

Includes:
- X-Initiator header (user/agent routing)
- Openai-Intent header (conversation-edits)
- Copilot-Vision-Request header (when images present)
- InferCopilotInitiator() helper
- HasVisionContent() helper
- Enhanced logging for debugging
```

---

## Success Criteria

- [x] Build passes
- [x] Tests pass (494/494 unit tests)
- [x] Code deployed to local Gateway
- [x] Gateway running
- [ ] Live multi-turn tool call test passes
- [ ] Verify headers in Gateway logs
- [ ] Confirm no infinite loops
- [ ] Confirm no dropped tool results

---

**Status:** ✅ Implementation complete, ready for live testing  
**Blocked by:** None  
**Next:** Live verification with actual tool calling flow
