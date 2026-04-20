# Sub-Agent Session Viewing

The BotNexus Blazor UI lets you observe sub-agent sessions in real time. When an agent spawns a sub-agent to handle a task, you can watch its progress, see what tools it calls, and review its complete conversation history.

## What Are Sub-Agent Sessions?

When an agent spawns a sub-agent to handle a task (using the `spawn_subagent` tool), a new session is created for that sub-agent. Sub-agent sessions run independently in the background and appear in the session sidebar beneath the parent agent.

**Common use cases for sub-agents:**
- **Research delegation** — A sub-agent investigates sources while the parent agent stays responsive
- **Cost optimization** — Use a cheaper model (e.g., `gpt-4.1`) for routine sub-tasks
- **Parallel work** — Multiple sub-agents process different parts of a larger task concurrently

For detailed information about how sub-agents work, see [Sub-Agent Spawning](../features/sub-agent-spawning.md).

## Viewing a Sub-Agent Session

### Step 1: Spawn a Sub-Agent

In your agent's chat, use the `spawn_subagent` tool:

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "task": "Research the top 5 vector databases. Compare pricing, performance, and .NET support.",
    "name": "vectordb-research",
    "model": "gpt-4.1",
    "tools": ["web_search", "web_fetch"],
    "maxTurns": 20,
    "timeoutSeconds": 300
  }
}
```

The agent will confirm the sub-agent has been spawned:
```
[Sub-agent "vectordb-research" started]
sessionId: parent-session-id::subagent::abc123def456...
```

### Step 2: Locate the Sub-Agent Session

In the session list on the left sidebar:
1. Sub-agent sessions appear **indented beneath their parent session**
2. A **status icon** shows whether the sub-agent is running (⏳) or completed (✅)
3. The session is labeled with the sub-agent's name (e.g., "vectordb-research")

### Step 3: Click to Open

Click any sub-agent session to open it in a **read-only view**. The full conversation history loads, including:
- All messages exchanged with the sub-agent
- Every tool call the sub-agent made and its results
- Streaming messages if the sub-agent is still running

## Read-Only View

Sub-agent sessions open in **read-only mode** to prevent accidentally interfering with the sub-agent's work.

### Banner

At the top of the conversation canvas, a **read-only banner** displays:
- **Sub-agent name** — The name of the sub-agent (e.g., "vectordb-research")
- **Status** — Current state: Running ⏳, Completed ✅, Failed ❌, or Killed 🔪
- **Read-only label** — "This is a read-only sub-agent session" to make the mode clear

### Message Input Hidden

The message input box is **completely hidden** in read-only mode. You cannot send messages to a sub-agent session — you can only observe and read.

### Real-Time Streaming

If the sub-agent is still running when you open its session:
- New messages stream in **live** as they arrive via SignalR
- Tool calls and responses are rendered in real time
- The banner status updates to reflect the sub-agent's current state

If the sub-agent has already completed:
- The full conversation history loads from the session archive
- The status in the banner shows "Completed" with a ✅ icon

## Session States

Sub-agent sessions display different statuses depending on their current progress:

| Icon | Status | Description |
|------|--------|-------------|
| ⏳ | Running | Sub-agent is actively processing |
| ✅ | Completed | Sub-agent finished successfully and delivered results to the parent session |
| ❌ | Failed | Sub-agent encountered an error or was terminated unexpectedly |
| 🔪 | Killed | Sub-agent was explicitly terminated by the parent session via `manage_subagent` |

## Interacting with Tool Calls

Sub-agent sessions render tool calls with the same fidelity as regular chat sessions:

- **Tool call blocks** are collapsible and show the tool name, parameters, and results
- **Syntax highlighting** is applied to code snippets and structured data
- **Tool responses** are formatted for readability (tables, JSON, code blocks, etc.)

## Example: Observing a Research Sub-Agent

Here's a typical workflow:

1. **Parent agent spawns research sub-agent:**
   ```
   [Sub-agent "market-research" started]
   Task: Analyze the top 5 AI vector database platforms...
   sessionId: 01JM2A...::subagent::abc123...
   ```

2. **You see the sub-agent appear in the sidebar** with a ⏳ icon

3. **You click it to open the read-only view** and see:
   - Banner: "Sub-agent: market-research | Status: Running ⏳"
   - First message: "Researching vector databases: Pinecone, Weaviate, Qdrant, Milvus, ChromaDB..."
   - Tool call: `web_search` for "vector database comparison"
   - Tool response: Search results snippet

4. **You watch in real time** as the sub-agent continues its research:
   - Makes `web_fetch` calls to fetch full articles
   - Extracts pricing, performance, and feature information
   - Continues until it completes or hits its timeout

5. **Sub-agent finishes:**
   - Banner updates to "Status: Completed ✅"
   - Final message appears with the research summary
   - Parent session receives a follow-up message with the results

## Navigation Tips

- **Back to parent session:** Click the parent session in the sidebar (the one above the indented sub-agent)
- **Switch between sub-agents:** If multiple sub-agents are running, click any to view its session
- **Return to active chat:** Click the active/current session (usually at the top of the list)

## Limitations

- Sub-agent sessions are **read-only** — you cannot send messages or interact with the sub-agent
- You **cannot steer or guide** a sub-agent after it has been spawned (planned for a future phase)
- Sessions show the **current session list** from the gateway at load time; if the gateway restarts, some completed sub-agent sessions may not appear (sessions are persisted to disk; refresh to reload)

## Related Documentation

- **[Sub-Agent Spawning](../features/sub-agent-spawning.md)** — Detailed reference for `spawn_subagent`, `list_subagents`, and `manage_subagent` tools
- **[API Reference](../api-reference.md)** — REST endpoints for sub-agent management
- **[WebUI Guide](./index.md)** — Overview of the Blazor UI and its features

## Troubleshooting

**Q: I spawned a sub-agent but I don't see it in the sidebar**

A: Sub-agent sessions appear in the sidebar under their parent session. Make sure:
- You're viewing the parent agent (the one that spawned the sub-agent)
- The parent session is selected in the session list
- Scroll down in the session list to see indented sub-agent sessions below the parent

**Q: The sub-agent session appears but it's empty**

A: The sub-agent may be starting up. If it shows a status of "Running ⏳":
- Wait a moment for the first message to stream in
- Refresh the page if the session appears to be stuck

**Q: Can I send a message to the sub-agent?**

A: No. Sub-agent sessions are **read-only** — you can observe but not interact. This is by design to avoid interfering with the sub-agent's work. To give instructions to a sub-agent while it's running, the parent agent can use the `manage_subagent` tool with a `steer` action (planned for a future phase).
