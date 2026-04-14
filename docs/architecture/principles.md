# Architectural Principles

**Purpose:** Design principles and architectural decisions that guide BotNexus development.

---

## Core Principles

### 1. Domain-Driven Design (DDD)

**Principle:** Core domain primitives are framework-agnostic and dependency-free.

**Application:**

- `BotNexus.Domain` has zero dependencies
- Strongly-typed IDs (`AgentId`, `SessionId`, `ChannelKey`) prevent type confusion
- Smart enums (`SessionType`, `SessionStatus`) encode domain rules
- Value objects are immutable records with structural equality
- Domain events communicate state changes

**Benefits:**

- Domain logic portable across platforms
- Easy to test (no infrastructure dependencies)
- Clear ubiquitous language
- Domain rules enforced at compile time

---

### 2. Clean Architecture (Dependency Inversion)

**Principle:** Dependencies flow inward. High-level modules depend on abstractions, not concrete implementations.

**Layers:**

```
Domain (no deps) ← Providers ← AgentCore ← Gateway ← Presentation
```

**Application:**

- Gateway depends on `ISessionStore`, not `SqliteSessionStore`
- AgentCore depends on `IAgentTool`, not specific tool implementations
- Providers depend on `LlmModel` (domain), not provider-specific types
- Presentation depends on Gateway contracts (`IAgentSupervisor`), not implementations

**Benefits:**

- Swap implementations without changing business logic
- Test with mocks/fakes easily
- Add new providers/channels/tools without modifying core

---

### 3. Single Responsibility Principle (SRP)

**Principle:** Each component has one reason to change.

**Application:**

| Component | Responsibility | Does Not Handle |
|-----------|----------------|-----------------|
| `IAgentRegistry` | Agent metadata lookup | Agent execution |
| `IAgentSupervisor` | Instance lifecycle | Message routing |
| `IMessageRouter` | Target agent resolution | Agent execution |
| `ISessionStore` | Session persistence | Session logic |
| `AgentLoopRunner` | LLM ↔ tool orchestration | Tool implementation |
| `IChannelAdapter` | Channel-specific I/O | Business logic |

**Benefits:**

- Easy to reason about
- Changes localized to one component
- Clear testing boundaries

---

### 4. Open/Closed Principle (OCP)

**Principle:** Open for extension, closed for modification.

**Application:**

- Add tools via `IAgentTool` interface (no AgentCore changes)
- Add channels via `IChannelAdapter` interface (no Gateway changes)
- Add hooks via `IHookHandler` interface (no execution changes)
- Add prompt sections via `IPromptSection` interface (no builder changes)
- Add isolation strategies via `IIsolationStrategy` interface (no supervisor changes)

**Benefits:**

- Core code stable
- Extensions don't risk breaking existing functionality
- Third-party plugins possible

---

### 5. Liskov Substitution Principle (LSP)

**Principle:** All implementations of an interface are interchangeable.

**Application:**

- Any `IApiProvider` can handle model calls (Anthropic, OpenAI, Copilot)
- Any `IChannelAdapter` can dispatch messages (SignalR, Telegram, TUI)
- Any `IIsolationStrategy` can create agents (InProcess, Container, Remote)
- Any `ISessionStore` can persist sessions (File, SQLite, InMemory)

**Benefits:**

- Predictable behavior
- Easy to swap implementations
- Configuration-driven decisions (no code changes)

---

### 6. Interface Segregation Principle (ISP)

**Principle:** Clients should not depend on interfaces they don't use.

**Application:**

- `IAgentTool` has only `Name`, `Definition`, `ExecuteAsync` (no lifecycle methods)
- `IHookHandler` has only `BeforeAsync`, `AfterAsync` (no state management)
- `IPromptSection` has only `Order`, `ShouldInclude`, `Build` (no pipeline logic)
- `IChannelAdapter` has focused methods for send/receive (no business logic)

**Benefits:**

- Small, focused interfaces
- Easy to implement
- Less coupling

---

### 7. Stream-First Design

**Principle:** All LLM interactions stream events. One-shot responses are built on streaming, not vice versa.

**Application:**

- `LlmClient.StreamAsync()` returns `LlmStream` (async channel)
- `StreamAccumulator` converts stream to events and final message
- All channel adapters support streaming (even if buffered for non-streaming channels)
- Clients receive real-time deltas for long responses

**Benefits:**

- Responsive UX (user sees tokens as they arrive)
- Interruptible (cancel in-flight requests)
- Uniform abstraction (streaming is the primitive)

---

### 8. Extension-First Architecture

**Principle:** New capabilities added via plugins, not code changes.

**Application:**

- MCP servers configured in `platform-config.json`, auto-loaded
- Skills discovered from markdown files
- Custom tools registered via `IToolRegistry`
- Hooks registered via DI container
- Channels registered via DI container

**Benefits:**

- Platform code stays lean
- Community can extend without forking
- Configuration drives behavior

---

### 9. Additive Migration

**Principle:** When evolving APIs, add new patterns alongside old ones. Deprecate gradually.

**Application:**

- `SubscribeAll()` + `SendMessage()` added alongside `JoinSession()` + `Prompt()`
- Old APIs marked `[Obsolete]` with guidance
- Both models coexist for backwards compatibility
- Remove only in major version bumps

**Benefits:**

- Smooth upgrades
- No breaking changes in minor versions
- Time for users to migrate

---

### 10. Location-Based Resource Management

**Principle:** Resources scoped by Location tuple: `(WorldId?, AgentId, SessionId?)`.

**Application:**

| Resource | Scope | Path Pattern |
|----------|-------|--------------|
| Workspace | Per-agent | `~/.botnexus/workspaces/{agentId}/` |
| Session | Per (agent, session) | `~/.botnexus/sessions/{sessionId}.jsonl` |
| Memory | Per agent or session | `~/.botnexus/memory/{agentId}/{sessionId?}/` |
| Logs | Per agent/session/world | `~/.botnexus/logs/{agentId}/{sessionId?}/` |

**Benefits:**

- Clear ownership boundaries
- Prevents resource leakage across agents/sessions
- Predictable cleanup (delete agent → delete workspace)

---

### 11. Per-Channel Isolation (WebUI)

**Principle:** Each channel type gets independent session management. WebUI uses subscribe-all for multi-session UI.

**Application:**

- WebUI subscribes to all sessions upfront (`SubscribeAll()`)
- Client-side DOM switching selects active session
- All events broadcast to session groups: `session:{sessionId}`
- Other channels (Telegram, TUI) manage their own session lifecycle

**Benefits:**

- WebUI supports multi-session without manual join/leave
- Real-time updates across all sessions
- Future: multi-client collaboration (multiple users in same session)

---

### 12. Session as Conversation Unit

**Principle:** Sessions are the unit of conversation state. Each (agent, session) pair gets isolated instance.

**Application:**

- `IAgentSupervisor` keys on `(AgentId, SessionId)`
- Each instance has independent message timeline
- Session stores persist full conversation history
- Session sealing prevents further writes

**Benefits:**

- Concurrent conversations with same agent
- Clean state boundaries
- Independent lifecycle per conversation

---

### 13. Workspace-Scoped Agent Isolation

**Principle:** Agents operate in isolated workspace directories. File tools cannot escape workspace without explicit permission.

**Application:**

- Each agent gets workspace: `~/.botnexus/workspaces/{agentId}/`
- File tools (read, write, edit) resolve paths relative to workspace
- `IPathValidator` enforces `FileAccessPolicy`
- `AllowWorkspaceEscape = false` (default) blocks `../` traversal

**Benefits:**

- Security: agents can't access each other's files
- Simplicity: agent sees workspace as root
- Multi-tenancy: safe to run untrusted agents

---

### 14. Hook-Based Extension

**Principle:** Extend behavior via hooks, not inheritance.

**Application:**

- `BeforeToolCall` / `AfterToolCall` delegates intercept tool execution
- Hooks can:
  - Block tools (path validation)
  - Transform arguments (sanitization)
  - Transform results (redaction)
  - Audit (logging, telemetry)
- Multiple hooks compose (chain-of-responsibility pattern)

**Benefits:**

- No subclassing required
- Compose multiple concerns (validation + audit + rate limit)
- Cross-cutting concerns separated from business logic

---

### 15. Fail Gracefully

**Principle:** Errors should not crash the platform. Extensions that fail should log and continue.

**Application:**

- Extension load failures logged, not thrown
- Tool execution failures returned as `AgentToolResult` with error
- LLM call failures logged, optionally retried
- Context overflow detected and handled via compaction

**Benefits:**

- Resilient to bad extensions
- LLM can reason about tool errors
- Automatic recovery from transient failures

---

## Architectural Decisions

### ADR-001: StatelessLLM APIs

**Decision:** Treat all LLM providers as completely stateless. Send full context every call.

**Rationale:**

- OpenAI, Anthropic, Copilot APIs have zero conversation memory
- Every request must include system prompt + full message history + tools
- Statefulness managed gateway-side (in-memory timeline + session store)

**Implications:**

- Context grows with every turn → need compaction
- Early messages sent repeatedly → use prompt caching
- Tool-heavy turns expensive → multiple full-context calls

---

### ADR-002: Subscribe-All Model (WebUI)

**Decision:** WebUI subscribes to all sessions upfront, switches client-side.

**Rationale:**

- Simpler client code (no manual join/leave)
- Supports multi-session UI (tabs, split view)
- Enables real-time updates across all sessions

**Implications:**

- SignalR group management in hub
- Client-side session filtering
- Future: multi-client collaboration

---

### ADR-003: Gateway Replaces CodingAgent

**Decision:** Gateway is the primary orchestrator. CodingAgent is a legacy CLI wrapper.

**Rationale:**

- Gateway supports multiple agents, channels, sessions
- CodingAgent single-agent, single-session model too limited
- Gateway enables WebUI, Telegram, federation, etc.

**Implications:**

- CodingAgent deprecated (but still functional)
- New features go in Gateway
- Migration path: CodingAgent → Gateway agent

---

### ADR-004: Per-Session Agent Instances

**Decision:** Each (agent, session) pair gets its own `Agent` instance.

**Rationale:**

- Session isolation: independent state per conversation
- Concurrency: multiple users can talk to same agent simultaneously
- Clean lifecycle: dispose instance when session ends

**Implications:**

- Higher memory usage (one instance per session)
- Startup cost amortized across session lifetime
- Concurrency limits via `MaxConcurrentSessions`

---

### ADR-005: InProcess Default Isolation

**Decision:** Default isolation strategy is in-process (shared memory).

**Rationale:**

- Fastest (<10ms startup)
- Simplest deployment (no Docker, no network)
- Suitable for trusted agents

**Implications:**

- Security: agents share memory (not suitable for untrusted code)
- Scaling: limited by single-process resources
- Future: container/remote isolation for multi-tenancy

---

## Summary

These principles form the foundation of BotNexus's architecture:

- **DDD + Clean Architecture**: Clean separation of concerns
- **SOLID**: Extension via interfaces, not inheritance
- **Stream-First**: Real-time UX
- **Location-Based Resources**: Clear ownership boundaries
- **Session Isolation**: Independent conversation state
- **Fail Gracefully**: Resilient to failures

**For implementation:**

- **[Domain Model](domain-model.md)** — Domain rules enforcing these principles
- **[System Flows](system-flows.md)** — Principles in action
- **[Development Guide](../development/principles.md)** — Code-level principle application
