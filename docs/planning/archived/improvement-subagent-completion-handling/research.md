# Sub-Agent Completion Handling: Cross-Platform Research

**Date:** 2025-07-09
**Problem:** When a parent agent spawns a background sub-agent and continues working, the completion event arrives later as a follow-up message. The parent agent sometimes misses or ignores it because it has already moved on, or polled status manually.

---

## 1. Platform Analysis

### 1.1 OpenClaw (Predecessor Platform)

**Source:** `C:\Users\jobullen\.openclaw\subagents\runs.json`, config files

**Architecture:**
- Sub-agents tracked in `subagents/runs.json` with full lifecycle metadata
- Fields: `runId`, `childSessionKey`, `controllerSessionKey`, `requesterSessionKey`, `task`, `spawnMode`, `expectsCompletionMessage`, `outcome`, `frozenResultText`, `endedHookEmittedAt`
- The `expectsCompletionMessage: true` flag explicitly signals the parent should receive a completion message

**Execution model:** Non-blocking. Sub-agent runs in its own session with a timeout.

**Completion delivery:**
- Result text is "frozen" at completion (`frozenResultText`)
- An `endedHookEmittedAt` timestamp records when the completion hook was sent to the parent
- The hook is delivered as a message to the parent's `controllerSessionKey`

**Key insight:** OpenClaw had the same problem — the `frozenResultText` field shows results were captured at sub-agent completion, but the `endedHookEmittedAt` could be significantly later (e.g., 200+ seconds after `endedAt`), suggesting queuing delays. The `expectsCompletionMessage` flag was an explicit acknowledgment that completion delivery was a distinct concern.

**Deduplication:** Run IDs served as natural deduplication keys. No explicit ack pattern visible.

---

### 1.2 AutoGen (Microsoft)

**Source:** [AutoGen documentation](https://microsoft.github.io/autogen/)

**Architecture:** Team-based orchestration with preset patterns (RoundRobinGroupChat, SelectorGroupChat, Swarm, MagenticOne).

**Execution model:** **Blocking/synchronous within a team run.** `team.run(task=...)` returns a `TaskResult` containing all messages from all agents. The team orchestrator controls turn order; agents don't run independently.

**Completion delivery:** Not applicable in the same way — the orchestrator awaits each agent's turn synchronously. Results flow through a shared message list that all agents can see.

**How parent distinguishes new vs. handled:** All messages are in a single `TaskResult.messages` list with sequential ordering. No async completion events.

**Key patterns:**
- **Termination conditions** (TextMentionTermination, MaxMessageTermination) control when the group stops
- **Shared context** — all agents see all messages, eliminating the "missed completion" problem
- **No background sub-agents** — agents take turns in a coordinated loop

**Relevance to BotNexus:** AutoGen avoids the problem entirely by not supporting async sub-agents. The shared-context model is the simplest solution but sacrifices parallelism.

---

### 1.3 CrewAI

**Source:** [CrewAI Tasks documentation](https://docs.crewai.com/concepts/tasks)

**Architecture:** Crews of agents executing task pipelines (sequential or hierarchical).

**Execution model:** 
- **Sequential tasks:** Blocking. Each task completes before the next starts. Output of task N becomes context for task N+1.
- **Hierarchical tasks:** A manager agent delegates tasks to crew members. Still synchronous within the crew's execution loop.
- **Async execution:** Tasks can set `async_execution=True`, which runs them in parallel. However, completion is handled via `context` dependencies — a downstream task that lists async tasks in its `context` field will **block until all context tasks complete**.

**Completion delivery:**
- `TaskOutput` is a structured object (raw text, JSON, Pydantic model) returned when a task finishes
- For async tasks, the framework handles the await internally when a dependent task needs the result
- Optional `callback` function executes after task completion — this is the closest analog to BotNexus's follow-up

**Deduplication:** Task outputs are immutable once produced. The framework manages the dependency graph.

**Key patterns:**
- **Callback on completion:** `callback=my_function` runs arbitrary code when a task finishes
- **Context-based dependency:** Async tasks are "collected" when a downstream task needs their output
- **Guardrails:** Output validation before passing to next task
- **No fire-and-forget:** Even async tasks are eventually awaited by the framework

**Relevance to BotNexus:** The callback + context dependency model is instructive. CrewAI never has the "orphaned completion" problem because async tasks are always awaited by something.

---

### 1.4 LangGraph

**Source:** [LangGraph multi-agent docs](https://langchain-ai.github.io/langgraph/concepts/multi_agent/)

**Architecture:** Graph-based state machine. Agents are nodes; edges define transitions. Sub-agents can be invoked as subgraphs.

**Execution model:** **Blocking within graph execution.** Each node runs to completion before edges are evaluated. A sub-agent invoked as a subgraph blocks the parent node until it returns.

**Completion delivery:**
- Sub-agent results are returned as the **output state** of the subgraph node
- The parent graph's state is updated with the subgraph's output state
- Results flow through typed state channels (e.g., `messages` list, custom state keys)

**How parent distinguishes new vs. handled:** State transitions are deterministic. Once a node's output is produced, the graph engine routes it to the next node(s) via edges. No async message delivery.

**Key patterns:**
- **State-based routing:** Graph edges can conditionally route based on sub-agent output
- **Checkpointing:** State is persisted at each node, enabling replay and recovery
- **Map-reduce:** Can fan out to multiple sub-agents and collect results
- **No async notification needed:** The graph engine manages the execution flow

**Relevance to BotNexus:** LangGraph's model is fundamentally different — it's a state machine, not a message-passing system. The key takeaway is that **making completion a state transition rather than a message** eliminates the delivery problem.

---

### 1.5 OpenAI Agents SDK

**Source:** [OpenAI Agents SDK docs](https://openai.github.io/openai-agents-python/multi_agent/)

**Architecture:** Two patterns: **Handoffs** (specialist takes over conversation) and **Agents-as-tools** (manager calls specialist as a tool).

**Execution model:**
- **Handoffs:** Synchronous transfer of conversation control. The new agent becomes the active agent.
- **Agents-as-tools (`Agent.as_tool()`):** The specialist agent is invoked as a **tool call**. The manager agent's LLM calls the specialist, gets the result back as a tool result, and continues.

**Completion delivery:**
- For agents-as-tools: Result is returned as a **tool result** in the manager's conversation. This is the most robust pattern — the LLM framework guarantees the manager sees tool results before generating its next response.
- For handoffs: No completion notification needed — control transfers fully.

**Deduplication:** Tool call IDs provide natural deduplication. Each tool invocation has a unique ID.

**Key patterns:**
- **Tool-result injection:** Sub-agent result appears as a tool result, which the LLM protocol guarantees will be processed
- **No background execution:** `Agent.as_tool()` is synchronous from the caller's perspective
- **Structured output:** Results can be typed (Pydantic models)

**Relevance to BotNexus:** The **agents-as-tools** pattern is the gold standard for reliable completion handling. By making the sub-agent invocation a tool call, the result is guaranteed to be processed because the LLM protocol requires tool results to be provided before the next assistant turn.

---

### 1.6 Semantic Kernel (Microsoft)

**Source:** [Semantic Kernel Agent Chat docs](https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/agent-chat)

**Architecture:** `AgentGroupChat` with strategy-based turn management. Newer `GroupChatOrchestration` pattern replacing the experimental `AgentChat`.

**Execution model:** **Blocking/synchronous.** Agents take turns in a managed conversation. `InvokeAsync()` returns an async stream of responses.

**Completion delivery:**
- Single-turn: Caller invokes a specific agent and gets response directly
- Multi-turn: Orchestrator manages turns via `SelectionStrategy` and `TerminationStrategy`
- All responses flow through a shared `ChatHistory`

**Key patterns:**
- **Selection strategy:** Determines which agent responds next (round-robin, function-based)
- **Termination strategy:** Determines when conversation ends
- **Shared history:** All agents see all messages
- **No async sub-agents:** Like AutoGen, avoids the problem by design

**Relevance to BotNexus:** Confirms the industry pattern — synchronous orchestration with shared context avoids the completion delivery problem entirely. But BotNexus intentionally supports async sub-agents for parallelism.

---

### 1.7 BotNexus (Current Platform)

**Source:** `Q:\repos\botnexus\src\`

**Architecture:** Gateway-based. `DefaultSubAgentManager` spawns sub-agents as background tasks. Completion is delivered via `IAgentHandle.FollowUpAsync()`.

**Execution model:** **Non-blocking.** `SpawnAsync` returns immediately. Sub-agent runs on `Task.Run()` with timeout. Parent continues working.

**Completion delivery flow (detailed):**

1. `SubAgentSpawnTool` calls `ISubAgentManager.SpawnAsync()` → returns `SubAgentInfo` immediately
2. `DefaultSubAgentManager.RunSubAgentAsync()` runs on a background task:
   - Calls `handle.PromptAsync(task)` on the child agent
   - On completion, calls `OnCompletedAsync(subAgentId, response.Content)`
3. `OnCompletedAsync()`:
   - Updates status to `Completed`/`Failed`/`TimedOut`
   - Publishes lifecycle activity via `IActivityBroadcaster`
   - Calls `parentHandle.FollowUpAsync(followUp)` with the completion message
4. `FollowUpAsync()` (in `InProcessAgentHandle`):
   - Calls `_agent.FollowUp(new UserMessage(message))` which enqueues to `_followUpQueue`
5. `AgentLoopRunner.RunLoopAsync()`:
   - After all tool calls resolve and no more steering messages, drains `followUpQueue`
   - If follow-ups exist, injects them as new user messages and continues the loop

**The Critical Race Condition:**

The problem occurs when:
1. Parent spawns sub-agent (tool call returns)
2. Parent LLM generates a response (e.g., "I've spawned a sub-agent, will update you when done")
3. Parent's `AgentLoopRunner` finishes — **drains follow-up queue, which is empty** (sub-agent still running)
4. Parent run ends, agent goes `Idle`
5. Sub-agent completes minutes later
6. `OnCompletedAsync` calls `parentHandle.FollowUpAsync()`
7. `FollowUpAsync` enqueues to `_followUpQueue` — **but no one is draining it**
8. The follow-up sits in the queue until the next user message triggers a new parent run
9. When the next run starts, it drains follow-ups... but now it's mixed with the new user message context

**The second problem (agent ignoring completion):**
- Even when the follow-up is delivered, it arrives as a plain user message
- The parent LLM has no structural indication this is a "sub-agent completion that requires action"
- It may treat it as informational and respond with "OK" or NO_REPLY
- There's no acknowledgment mechanism — the platform can't tell if the parent acted on the result

**What works well:**
- Lifecycle activities via `IActivityBroadcaster` provide observability
- `SubAgentInfo` tracks full state (status, result summary, timestamps)
- The `PendingMessageQueue` with `QueueMode.All` ensures all follow-ups are delivered when drained

**What's broken:**
- Follow-up delivery depends on the parent being idle AND receiving a new message
- No mechanism to wake up an idle parent when a follow-up arrives
- No structural distinction between sub-agent completions and regular messages
- No ack/retry for completion delivery

---

## 2. Comparative Summary

| Platform | Execution | Completion Delivery | Injection Type | Dedup/Ack | Async Sub-Agents |
|---|---|---|---|---|---|
| OpenClaw | Non-blocking | Hook to parent session | Message | Run ID | ✅ |
| AutoGen | Blocking (turn-based) | Shared message list | N/A (synchronous) | N/A | ❌ |
| CrewAI | Blocking + async_execution | Callback + context deps | TaskOutput | Task identity | Partial (awaited) |
| LangGraph | Blocking (graph nodes) | State transition | State update | Checkpoint | ❌ |
| OpenAI Agents SDK | Blocking (tool call) | Tool result | Tool result message | Tool call ID | ❌ |
| Semantic Kernel | Blocking (turn-based) | Shared history | N/A (synchronous) | N/A | ❌ |
| **BotNexus** | **Non-blocking** | **FollowUp queue** | **User message** | **None** | **✅** |

**Key finding:** BotNexus is the only platform that supports true non-blocking sub-agents AND has the completion delivery problem. Every other platform either (a) blocks until sub-agent completes, or (b) uses a structural mechanism (tool results, state transitions) that guarantees processing.

---

## 3. Root Cause Analysis

The core issue is a **delivery-processing gap**: the platform can deliver the completion message (via `FollowUpAsync`), but cannot guarantee the parent will process it, because:

1. **No wake-up mechanism:** An idle agent has no listener draining the follow-up queue
2. **Wrong message type:** Completion arrives as a user message, not a tool result — the LLM has no obligation to act on it
3. **No ack pattern:** The platform cannot distinguish "parent processed completion" from "parent ignored completion"
4. **Timing dependency:** Follow-up only works if the parent is mid-run when it arrives (steering-like injection)

---

## 4. Recommendations

### 4.1 Platform-Level Fix: Completion Wake-Up (Priority 1)

**Problem:** When `FollowUpAsync` is called on an idle agent, the message sits in the queue until the next user interaction.

**Solution:** Add a wake-up mechanism that triggers a new agent run when a follow-up arrives on an idle agent.

```
// In InProcessAgentHandle.FollowUpAsync:
public async Task FollowUpAsync(string message, CancellationToken ct = default)
{
    _agent.FollowUp(new UserMessage(message));
    
    // If agent is idle, trigger a new run to process the follow-up
    if (!IsRunning && _agent.HasQueuedMessages)
    {
        await _wakeUpCallback?.Invoke(ct);
    }
}
```

The wake-up callback would:
1. Get or create the parent session
2. Drain follow-ups and inject them as the prompt
3. Process the response through the normal streaming/session pipeline
4. Deliver output to the parent's channel

This mirrors how `GatewayHost.ProcessInboundMessageAsync` works for user messages, but triggered by sub-agent completion instead of channel input.

**Implementation path:**
- Add `OnFollowUpReceived` event/callback to `IAgentHandle`
- Wire it in `DefaultSubAgentManager.OnCompletedAsync` or `GatewayHost`
- The callback dispatches a synthetic `InboundMessage` through the existing `GatewayHost.DispatchAsync` pipeline with a metadata flag like `"trigger": "subagent_completion"`

### 4.2 Platform-Level Fix: Typed Completion Events (Priority 2)

**Problem:** Sub-agent completions arrive as plain user messages, indistinguishable from human messages.

**Solution:** Introduce a distinct message type or structured envelope for sub-agent completions.

```
// Instead of:
var followUp = $"Sub-agent {subAgentId} completed. Summary:\n{normalizedSummary}";

// Use a structured completion message:
var followUp = new SubAgentCompletionMessage(
    SubAgentId: subAgentId,
    Status: updated.Status,
    ResultSummary: normalizedSummary,
    TaskDescription: updated.Task,
    StartedAt: updated.StartedAt,
    CompletedAt: updated.CompletedAt
);
```

At the LLM boundary, this would be rendered as a clearly-marked system or user message:

```
[SUBAGENT_COMPLETION id={subAgentId} status=completed]
Task: {taskDescription}
Result: {resultSummary}
[/SUBAGENT_COMPLETION]

You MUST acknowledge this result and take appropriate action (forward to user, integrate into your work, etc.)
```

### 4.3 Platform-Level Fix: Completion Acknowledgment (Priority 3)

**Problem:** No way to verify the parent agent actually processed the completion.

**Solution:** Track completion delivery state with retry:

```csharp
enum CompletionDeliveryState { Pending, Delivered, Acknowledged, Failed }
```

- `Delivered`: FollowUp was enqueued
- `Acknowledged`: Parent's next response referenced the sub-agent ID (detectable via response text or tool calls)
- After delivery, if not acknowledged within N seconds, re-deliver with escalation
- After M retries, mark as `Failed` and alert via activity broadcast

### 4.4 Agent-Prompt-Level Fix: System Prompt Instructions (Priority 1, Quick Win)

**Problem:** The agent's system prompt doesn't instruct it to prioritize sub-agent completions.

**Solution:** Add to the system prompt builder:

```markdown
## Sub-Agent Completions
When you receive a sub-agent completion message (prefixed with "Sub-agent ... completed/failed"):
- This is a HIGH-PRIORITY event that requires immediate action
- Review the result and forward relevant findings to the user
- If the result indicates failure, inform the user and suggest next steps
- Do NOT respond with NO_REPLY to completion events
- Do NOT ignore completions even if they arrive during unrelated work
```

### 4.5 Aspirational: Tool-Result Pattern (Longer Term)

The most robust pattern (inspired by OpenAI Agents SDK) would make sub-agent invocation a **blocking tool call** that returns the result as a tool result:

```
// Option A: Synchronous spawn (blocks until complete)
spawn_subagent(task="...", wait=true)  → returns result as tool output

// Option B: Async spawn with deferred tool result
spawn_subagent(task="...")  → returns run_id
// Later, platform injects a synthetic tool result:
[tool_result for spawn_subagent]: Sub-agent completed. Result: ...
```

Option B is the most interesting: the platform would hold the tool call "open" and inject the result when the sub-agent completes. This guarantees the LLM processes it because tool results are mandatory in the conversation protocol. However, this requires significant changes to the agent loop (keeping a tool call pending across runs).

---

## 5. Recommended Implementation Order

| Priority | Fix | Type | Effort | Impact |
|---|---|---|---|---|
| 1 | System prompt instructions | Agent-prompt | Small | Medium — reduces ignore rate |
| 2 | Completion wake-up mechanism | Platform | Medium | **High** — fixes idle agent gap |
| 3 | Structured completion message format | Platform | Small | Medium — improves LLM compliance |
| 4 | Completion ack + retry | Platform | Medium | Medium — ensures delivery |
| 5 | Tool-result injection pattern | Platform | Large | **High** — eliminates problem structurally |

The quick wins (#1 and #3) can ship immediately. The wake-up mechanism (#2) is the critical platform fix that addresses the most common failure mode. The tool-result pattern (#5) is the ideal long-term solution but requires agent loop changes.

---

## 6. Appendix: BotNexus Code References

| Component | Path | Role |
|---|---|---|
| `DefaultSubAgentManager` | `src/gateway/BotNexus.Gateway/Agents/DefaultSubAgentManager.cs` | Orchestrates sub-agent lifecycle, delivers completion |
| `ISubAgentManager` | `src/gateway/BotNexus.Gateway.Contracts/Agents/ISubAgentManager.cs` | Contract for sub-agent management |
| `InProcessAgentHandle` | `src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs` | `FollowUpAsync` implementation |
| `Agent` | `src/agent/BotNexus.Agent.Core/Agent.cs` | Follow-up queue, run lifecycle |
| `AgentLoopRunner` | `src/agent/BotNexus.Agent.Core/Loop/AgentLoopRunner.cs` | Follow-up drain at end of loop |
| `PendingMessageQueue` | `src/agent/BotNexus.Agent.Core/PendingMessageQueue.cs` | Thread-safe message queue |
| `GatewayHost` | `src/gateway/BotNexus.Gateway/GatewayHost.cs` | Message dispatch pipeline |
| `StreamingSessionHelper` | `src/gateway/BotNexus.Gateway/Streaming/StreamingSessionHelper.cs` | Stream processing + session save |
| `IActivityBroadcaster` | `src/gateway/BotNexus.Gateway.Contracts/Activity/IActivityBroadcaster.cs` | Lifecycle event broadcasting |
| `SubAgentSpawnTool` | `src/gateway/BotNexus.Gateway/Tools/SubAgentSpawnTool.cs` | Tool exposed to agents for spawning |
