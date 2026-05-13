---
id: feature-ask-user-tool
title: "Feature: ask_user Tool"
type: feature
priority: high
status: design
created: 2026-04-15
---

# Feature: ask_user Tool

**Status:** design  
**Priority:** high  
**Created:** 2026-04-15  

## Problem

Agents currently have no way to interactively ask the user a question mid-turn and wait for a response. Common needs:
- Free-form text input ("What name should I use?")
- Single selection from choices ("Which option? A, B, or C")
- Multiple selection from choices ("Select all that apply")
- Selection + free-form ("Pick one, or type your own")

Other AI platforms (Claude Code, Cursor, etc.) have this. BotNexus agents currently have to either guess or end the turn and ask in prose, losing tool execution context.

## Architecture Overview

### Core Challenge

The agent tool execution loop (`AgentLoopRunner` → `ToolExecutor`) is synchronous from the LLM's perspective — tool calls block until they return a result. The session message queue is single-reader, so a user's reply can't arrive through normal `GatewayHub.SendMessage()` dispatch while the agent is mid-turn.

### Solution: TCS-based blocking + side-channel response

```
Agent calls ask_user tool
    → ExecuteAsync blocks on TaskCompletionSource
    → Emits UserInputRequired stream event (via onUpdate callback)
        → Gateway broadcasts to all channels
            → Web UI: shows inline form / modal
            → Telegram: sends message with keyboard buttons
            → SignalR/generic: sends formatted text with numbered choices
    → User responds
        → Channel calls GatewayHub.RespondToAskUser(requestId, response)
            → Bypasses session queue
            → Completes TCS directly via AskUserResponseRegistry
    → ExecuteAsync unblocks, returns user's response to LLM
    → Agent loop continues
```

## Design

### 1. AskUserRequest Model

```csharp
namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Describes a question posed to the user by an agent tool.
/// </summary>
public sealed class AskUserRequest
{
    public required string RequestId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string Prompt { get; init; }
    public AskUserInputType InputType { get; init; } = AskUserInputType.FreeForm;
    public IReadOnlyList<AskUserChoice>? Choices { get; init; }
    public bool AllowMultiple { get; init; }
    public bool AllowFreeForm { get; init; }
    public TimeSpan? Timeout { get; init; }
}

public enum AskUserInputType
{
    FreeForm,        // Open text input
    SingleChoice,    // Pick one from list
    MultipleChoice,  // Pick one or more from list
    ChoiceOrFreeForm // Pick from list OR type custom
}

public sealed class AskUserChoice
{
    public required string Value { get; init; }
    public string? Label { get; init; }  // Display text, defaults to Value
    public string? Description { get; init; }  // Optional help text
}
```

### 2. AskUserResponse Model

```csharp
public sealed class AskUserResponse
{
    public required string RequestId { get; init; }
    public string? FreeFormText { get; init; }
    public IReadOnlyList<string>? SelectedValues { get; init; }
    public bool WasCancelled { get; init; }
    public bool WasTimeout { get; init; }
}
```

### 3. AskUserResponseRegistry

Singleton service that maps `requestId` → `TaskCompletionSource<AskUserResponse>`. Thread-safe, auto-cleans expired requests.

```csharp
public sealed class AskUserResponseRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AskUserResponse>> _pending = new();
    
    public (string RequestId, Task<AskUserResponse> Task) Register(TimeSpan? timeout);
    public bool TryComplete(string requestId, AskUserResponse response);
    public void Cancel(string requestId);
}
```

### 4. AskUserTool

Lives in `BotNexus.Tools` alongside other core tools.

```csharp
public sealed class AskUserTool : IAgentTool
{
    public string Name => "ask_user";
    
    // Tool schema:
    // - prompt (string, required): The question to ask
    // - input_type (string, optional): "free_form" | "single_choice" | "multiple_choice" | "choice_or_free_form"
    // - choices (array of {value, label?, description?}, optional): Available choices
    // - allow_multiple (bool, optional): Allow selecting multiple choices
    // - timeout_seconds (int, optional): Max seconds to wait
    
    public async Task<ToolResult> ExecuteAsync(...)
    {
        // 1. Build AskUserRequest
        // 2. Register with AskUserResponseRegistry → get TCS
        // 3. Emit UserInputRequired event via onUpdate callback
        // 4. await TCS (with timeout + cancellation)
        // 5. Format response as tool result
    }
}
```

### 5. Stream Event: UserInputRequired

New `AgentStreamEventType`:

```csharp
UserInputRequired  // Payload: AskUserRequest JSON
```

Flows through: `InProcessAgentHandle` → `StreamingSessionHelper` → channel adapters.

### 6. GatewayHub Extension

New hub method:

```csharp
public async Task RespondToAskUser(string requestId, string? freeFormText, string[]? selectedValues, bool cancelled)
{
    // Build AskUserResponse
    // Call _askUserRegistry.TryComplete(requestId, response)
    // No session queue involvement
}
```

### 7. Channel Adapter Contract

Add to `IChannelAdapter` or create an optional interface:

```csharp
public interface IAskUserCapableChannel
{
    /// <summary>
    /// Present an ask_user request to the user. The channel is responsible for
    /// collecting the response and calling the response callback.
    /// </summary>
    Task PresentAskUserAsync(AskUserRequest request, CancellationToken ct);
}
```

Channels that don't implement this get a **fallback**: the gateway sends a formatted text message with numbered choices and interprets the next user message as the response.

### 8. Channel Implementations

#### Web UI (SignalR)
- Receives `UserInputRequired` event
- Renders inline form below the current assistant message:
  - Free-form: text input + submit
  - Choices: button group (single) or checkbox group (multiple)  
  - Choice + free-form: buttons + text input
- Calls `hub.invoke('RespondToAskUser', requestId, ...)` on submit
- Shows cancel button
- Timeout countdown indicator

#### Telegram
- Uses inline keyboard buttons for choices
- Free-form: sends message, waits for text reply
- Callback query handler calls `RespondToAskUser`

#### Generic / Fallback
- Sends formatted message:
  ```
  🤔 [Agent Question]
  
  What environment should I deploy to?
  
  1. Development
  2. Staging  
  3. Production
  
  Reply with a number, or type a custom answer.
  ```
- Next inbound message on the session is intercepted and routed to `RespondToAskUser`

### 9. Timeout & Cancellation

- Default timeout: 5 minutes (configurable per-call)
- On timeout: tool returns `{"timed_out": true, "message": "User did not respond within 5 minutes"}`
- On session abort: CancellationToken cancels the TCS
- On user cancel: `{"cancelled": true}`
- Agent decides how to handle each case

## Implementation Plan

### Phase 1: Core infrastructure
1. `AskUserRequest`, `AskUserResponse`, `AskUserChoice` models (Abstractions)
2. `AskUserResponseRegistry` (Gateway)
3. `AskUserTool` (Tools)
4. `UserInputRequired` stream event type
5. Tool registration in `InProcessIsolationStrategy`

### Phase 2: Gateway wiring
6. `GatewayHub.RespondToAskUser()` method
7. `StreamingSessionHelper` handling of `UserInputRequired` events
8. Fallback text-based handler for channels without native support

### Phase 3: Web UI
9. `UserInputRequired` event handler in `app.legacy.js`
10. Inline form rendering (free-form, choices, mixed)
11. Submit → `RespondToAskUser` hub call
12. Cancel button, timeout countdown

### Phase 4: Other channels
13. Telegram inline keyboard support
14. Any other channel adapters

## Open Questions

1. **Should sub-agents be able to use ask_user?** Probably not by default — they don't have direct user access. Could proxy through the parent agent.
2. **Multiple pending asks?** Keep it simple — one active ask_user per session. Queue or reject additional calls.
3. **Retry on timeout?** Let the agent decide. The tool returns timeout info, agent can call ask_user again.
4. **Prompt templates / saved prompts?** Related but separate feature — prompts that agents can reference by name, with pre-defined choices. Design separately and have ask_user be the execution mechanism.

## Relationship to Prompts Feature

The `ask_user` tool is the **execution primitive**. A future "prompts" feature would be a **library of named, parameterized prompt templates** that agents can invoke. When a prompt template needs user input, it would use `ask_user` under the hood. Design them independently but ensure compatibility.
