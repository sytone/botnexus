# Development Documentation

**Purpose:** Detailed implementation guides, code-level walkthroughs, and in-depth technical documentation.

---

## Overview

This directory contains detailed documentation for developers working on or extending BotNexus. For high-level architecture, see [`../architecture/overview.md`](../architecture/overview.md).

---

## Contents

### Implementation Details

- **[agent-execution.md](agent-execution.md)** — Agent lifecycle, isolation strategies, instance management
- **[llm-request-lifecycle.md](llm-request-lifecycle.md)** — How user messages become LLM API calls (stateless context)
- **[message-flow.md](message-flow.md)** — Channel dispatch, routing, session lifecycle
- **[prompt-pipeline.md](prompt-pipeline.md)** — System prompt construction and caching
- **[session-stores.md](session-stores.md)** — Session persistence implementations
- **[triggers-and-federation.md](triggers-and-federation.md)** — Cron, soul, and cross-world agent communication
- **[webui-connection.md](webui-connection.md)** — SignalR hub, subscribe-all model, multi-session UI
- **[workspace-and-memory.md](workspace-and-memory.md)** — Workspace isolation, memory management, context files

---

## For Newcomers

**Start here:**

1. **[../architecture/overview.md](../architecture/overview.md)** — High-level architecture
2. **[../architecture/system-flows.md](../architecture/system-flows.md)** — Key runtime flows
3. **[../architecture/domain-model.md](../architecture/domain-model.md)** — Core domain concepts
4. **[agent-execution.md](agent-execution.md)** — How agents are created and executed
5. **[message-flow.md](message-flow.md)** — How messages route through the system

---

## Related Documentation

- **[../architecture/overview.md](../architecture/overview.md)** — High-level architecture reference
- **[../api-reference.md](../api-reference.md)** — REST API and SignalR hub documentation
- **[../extension-development.md](../extension-development.md)** — Building custom tools, channels, providers
- **[../dev-guide.md](../dev-guide.md)** — Building and debugging BotNexus
