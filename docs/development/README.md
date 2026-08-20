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
- **[compat-shim-lifecycle.md](compat-shim-lifecycle.md)** — Migrate forward, then delete: convention for schema/model compatibility shims
- **[gateway-crash-diagnostics.md](gateway-crash-diagnostics.md)** — Minidump-on-crash, last-chance fault breadcrumb, and unclean-shutdown detection
- **[triggers-and-federation.md](triggers-and-federation.md)** — Cron, soul, and cross-world agent communication
- **[webui-connection.md](webui-connection.md)** — SignalR hub, subscribe-all model, multi-session UI
- **[portal-surface-parity.md](portal-surface-parity.md)** — Desktop vs mobile portal inventory, deliberate-difference register, and alignment plan
- **[debugging.md](debugging.md)** - Debugging the Gateway, extensions, and WebUI
- **[workspace-and-memory.md](workspace-and-memory.md)** — Workspace isolation, memory management, context files

### CLI

- **[cli-wizard.md](cli-wizard.md)** — Reusable step-based wizard framework for interactive CLI commands

### Tooling & repo

- **[code-standards.md](code-standards.md)** - XML comment standard, naming, dependency boundaries, and testing conventions
- **[pr-and-commit-conventions.md](pr-and-commit-conventions.md)** — Required PR body and squash-commit format, and the reviewer inspection order for agent-authored changes
- **[running-tests.md](running-tests.md)** — Impacted-test selection and Windows testhost firewall pre-authorization
- **[persistence-seam-testing.md](persistence-seam-testing.md)** — Write classification and deterministic lost-update seam tests for aggregate stores
- **[azure-build-test-runner.md](azure-build-test-runner.md)** — Selectable strict validation and optional Azure Container Apps execution
- **[stale-base-merges.md](stale-base-merges.md)** — #3173 base-freshness gate: why a green PR on a stale base can still redden `main`, and how inherited red is told apart from introduced red
- **[git-worktree-config-hardening.md](git-worktree-config-hardening.md)** — #1602 core.bare guard, hooks, and worktree config hygiene

### Security

- **[security-sensitive-file-guard.md](security-sensitive-file-guard.md)** — Guard rails around edits to security-sensitive files
- **[comment-moderation.md](comment-moderation.md)** — #3224 who may comment on an issue or PR, and the two-part control that enforces it
- **[downloaded-payload-verification.md](downloaded-payload-verification.md)** — #2372 verify-before-execute rule for any downloaded install/update payload

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
- **[../getting-started-dev.md](../getting-started-dev.md)** — Building and debugging BotNexus
