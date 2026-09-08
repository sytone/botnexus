# Documentation map

Use this map when adding or reviewing a page. Audience is based on the reader's task, not its folder name. The actual sidebar is maintained in `docs/.vitepress/config.mts`; this inventory explains the classification rather than defining a second navigation source.

## Three reader routes

- **User:** install, chat, choose/configure providers, use extensions and features, find ideas, manage settings and troubleshoot. No programming knowledge assumed.
- **Developer:** build, test, contribute and implement integrations. Basic first-year computer science knowledge assumed; explain project-specific tools.
- **Architecture:** understand components, runtime/data flows and design decisions. Use plain English even for deep technical material.

Providers, extensions and features are not peer audiences. Their consumer instructions belong under User; authoring and detailed implementation belong under Developer or Architecture. Old URLs remain stable even when sidebar placement changes.

## Important exceptions

- `user-guide/extensions.md` contains developer authoring material. Its original URL and content remain, but the sidebar places it under Developer. The consumer entry is [Use extensions](../user-guide/using-extensions.md).
- Technical feature pages about deadlines, auditing, routing and runtime behavior are placed with Developer or Architecture rather than beginner feature tasks.
- Reference pages can mix audiences. New consumer guides provide a task-first route into them; moving a link does not certify every paragraph as beginner-level.
- Release pages are reached through the release index, not repeated individually in the sidebar.
- `guide-skill/SKILL.md` is embedded agent instruction material, not ordinary site guidance. No new sidebar entry is added. It is not explicitly excluded by the current build filter, so absence from navigation does not imply absence from generated output.
- Historical/internal content excluded by the site build remains in the repository; it is not deleted by this reorganization.
- The in-app Guide uses `user-guide/guide-index.json`, separate from the VitePress sidebar. Its current copy/link behavior needs separate validation before mirroring cross-directory site routes. This change reorganizes the documentation site, not the application Guide.

## Maintain the map

Read the [writing standard](documentation-standards.md). Add a page to the appropriate sidebar group or to an explicit index. Record why excluded or embedded material is not a reader page. Preserve incoming links and update this inventory when adding or moving pages. An entry here is classification evidence, not proof that all of its facts were audited.

## Page inventory

Paths are relative to `docs/`. The inventory covers Markdown sources; assets, schemas and navigation JSON are supporting files, not reader pages.

| Page | Audience / route |
| --- | --- |
| `api/agents.md` | Developer guide (through API index) |
| `api/conversations.md` | Developer guide (through API index) |
| `api/cron.md` | Developer guide (through API index) |
| `api/exchanges.md` | Developer guide (through API index) |
| `api/README.md` | Developer guide |
| `api/satellites.md` | Developer guide (through API index) |
| `api/sessions.md` | Developer guide (through API index) |
| `api/signalr.md` | Developer guide (through API index) |
| `api/tools.md` | Developer guide (through API index) |
| `api/webhooks.md` | Developer guide (through API index) |
| `api-reference.md` | Developer guide |
| `architecture/adr/0000-record-architecture-decisions.md` | Architecture guide (through decision index) |
| `architecture/adr/0001-use-sqlite-for-persistence.md` | Architecture guide (through decision index) |
| `architecture/adr/0002-channel-centric-routing.md` | Architecture guide (through decision index) |
| `architecture/adr/0003-stamp-world-identity-on-sqlite-stores.md` | Architecture guide (through decision index) |
| `architecture/adr/0004-version-sqlite-store-schemas.md` | Architecture guide (through decision index) |
| `architecture/adr/adr-template.md` | Architecture guide (through decision index) |
| `architecture/adr/README.md` | Architecture guide |
| `architecture/c4-diagrams.md` | Architecture guide |
| `architecture/channel-binding.md` | Architecture guide |
| `architecture/conversation-scoped-events.md` | Architecture guide |
| `architecture/domain-model.md` | Architecture guide |
| `architecture/extension-guide.md` | Architecture guide |
| `architecture/gateway-flow.md` | Architecture guide |
| `architecture/overview.md` | Architecture guide |
| `architecture/plugins.md` | Architecture guide |
| `architecture/portal-pwa-caching.md` | Architecture guide |
| `architecture/principles.md` | Architecture guide |
| `architecture/README.md` | Architecture guide |
| `architecture/runtime-view.md` | Architecture guide |
| `architecture/system-flows.md` | Architecture guide |
| `archive/12-port-audit-changelog.md` | Excluded historical/internal content |
| `archive/13-phase5-migration-guide.md` | Excluded historical/internal content |
| `archive/architecture-old/botnexus-domain-model.md` | Excluded historical/internal content |
| `archive/architecture-old/current-overview-old.md` | Excluded historical/internal content |
| `archive/architecture-old/deep-dive.md` | Excluded historical/internal content |
| `archive/architecture-old/overview-old.md` | Excluded historical/internal content |
| `archive/architecture-old/system-layers.md` | Excluded historical/internal content |
| `archive/integration-verification-provider-architecture.md` | Excluded historical/internal content |
| `archive/port-audit-agent-core.md` | Excluded historical/internal content |
| `archive/port-audit-findings.md` | Excluded historical/internal content |
| `archive/port-audit-report.md` | Excluded historical/internal content |
| `cli-reference.md` | User guide |
| `configuration.md` | User guide |
| `cron-and-scheduling.md` | User guide |
| `design-system.md` | Developer guide |
| `development/agent-execution.md` | Architecture guide |
| `development/app-integration-surfaces.md` | Developer guide |
| `development/autonomous-maintenance-orchestration.md` | Developer guide |
| `development/azure-build-test-runner.md` | Developer guide |
| `development/cli-wizard.md` | Developer guide |
| `development/code-standards.md` | Developer guide |
| `development/comment-moderation.md` | Developer guide |
| `development/compat-shim-lifecycle.md` | Developer guide |
| `development/connection-registry-and-secrets.md` | Architecture guide |
| `development/container-integration-testing.md` | Developer guide |
| `development/ddd-patterns.md` | Developer guide |
| `development/debugging.md` | Developer guide |
| `development/documentation-grooming.md` | Developer guide |
| `development/documentation-standards.md` | Developer guide |
| `development/downloaded-payload-verification.md` | Developer guide |
| `development/e2e-tests.md` | Developer guide |
| `development/gateway-crash-diagnostics.md` | Developer guide |
| `development/git-worktree-config-hardening.md` | Developer guide |
| `development/github-write-tool-contracts.md` | Developer guide |
| `development/hub-event-inventory-generator.md` | Developer guide |
| `development/inbound-delivery-modes.md` | Architecture guide |
| `development/issue-conventions.md` | Developer guide |
| `development/llm-request-lifecycle.md` | Architecture guide |
| `development/maintenance-pr-footprints.md` | Developer guide |
| `development/maintenance-pr-freshness.md` | Developer guide |
| `development/message-flow.md` | Architecture guide |
| `development/newline-delta-conformance.md` | Developer guide |
| `development/normalized-llm-event-audit.md` | Developer guide |
| `development/persistence-seam-testing.md` | Developer guide |
| `development/portal-surface-parity.md` | Developer guide |
| `development/powershell-test-isolation.md` | Developer guide |
| `development/pr-and-commit-conventions.md` | Developer guide |
| `development/pre-commit-gate.md` | Developer guide |
| `development/prompt-pipeline.md` | Architecture guide |
| `development/README.md` | Developer guide |
| `development/remote-source-snapshots.md` | Developer guide |
| `development/running-tests.md` | Developer guide |
| `development/scenario-test-framework-decision.md` | Developer guide |
| `development/security-sensitive-file-guard.md` | Developer guide |
| `development/session-stores.md` | Architecture guide |
| `development/source-generator-survey.md` | Developer guide |
| `development/spike-workflow-conversations.md` | Developer guide |
| `development/stale-base-merges.md` | Developer guide |
| `development/static-factory-naming.md` | Developer guide |
| `development/tool-schema-generator-spike.md` | Developer guide |
| `development/triggers-and-federation.md` | Architecture guide |
| `development/validation-receipts.md` | Developer guide |
| `development/web-fetch-destination-policy.md` | Developer guide |
| `development/webui-connection.md` | Architecture guide |
| `development/workspace-and-memory.md` | Architecture guide |
| `extension-development.md` | Developer guide |
| `extensions/agent365.md` | User guide |
| `extensions/browser-tools.md` | User guide |
| `extensions/data-store.md` | User guide |
| `extensions/debug-tool.md` | User guide |
| `extensions/exec-tool.md` | User guide |
| `extensions/github.md` | User guide |
| `extensions/matrix.md` | User guide |
| `extensions/mcp-invoke.md` | User guide |
| `extensions/mcp.md` | User guide |
| `extensions/media-handlers.md` | Developer guide |
| `extensions/process-tool.md` | User guide |
| `extensions/qmd.md` | User guide |
| `extensions/skills.md` | User guide |
| `extensions/tasknexus.md` | User guide |
| `extensions/telemetry.md` | Developer guide |
| `extensions/test-channel.md` | Developer guide |
| `extensions/web-tools.md` | User guide |
| `features/agent-converse-deadline-provenance.md` | Developer guide |
| `features/agent-exchange.md` | User guide |
| `features/agent365-observability.md` | User guide |
| `features/agent365-onboarding.md` | User guide |
| `features/agents-md-conventions.md` | User guide |
| `features/built-in-agents.md` | User guide |
| `features/canvas.md` | User guide |
| `features/conversation-cost.md` | User guide |
| `features/conversation-provenance.md` | Architecture guide |
| `features/cron-cost.md` | User guide |
| `features/cron-session-targets.md` | Architecture guide |
| `features/dev-origin-guard.md` | Architecture guide |
| `features/file-backed-secrets.md` | User guide |
| `features/hybrid-memory-retrieval.md` | User guide |
| `features/memory-taint-quarantine.md` | User guide |
| `features/model-awareness.md` | User guide |
| `features/model-specific-instruction-files.md` | User guide |
| `features/portal-boot-diagnostics.md` | User guide |
| `features/portal-plugins-page.md` | User guide |
| `features/provider-health-events.md` | Architecture guide |
| `features/security-event-diagnostics.md` | User guide |
| `features/session-consistency.md` | Architecture guide |
| `features/session-tool-overrides.md` | User guide |
| `features/shell-execution.md` | User guide |
| `features/sub-agent-spawning.md` | User guide |
| `features/todo.md` | User guide |
| `features/tool-audit-write-ahead.md` | Architecture guide |
| `getting-started-dev.md` | Developer guide |
| `getting-started-release.md` | User guide |
| `getting-started.md` | User guide |
| `guide-skill/SKILL.md` | Embedded agent skill (not a sidebar guide) |
| `guides/audio-recording.md` | User guide |
| `guides/gateway-recovery.md` | User guide |
| `guides/offline-install.md` | User guide |
| `guides/watchdog-setup.md` | User guide |
| `guides/webhooks.md` | User guide |
| `index.md` | Site home |
| `internals/01-providers.md` | Excluded historical/internal content |
| `internals/02-agent-core.md` | Excluded historical/internal content |
| `internals/03-coding-agent.md` | Excluded historical/internal content |
| `internals/04-building-your-own.md` | Excluded historical/internal content |
| `internals/05-glossary.md` | Excluded historical/internal content |
| `internals/06-context-file-discovery.md` | Excluded historical/internal content |
| `internals/07-thinking-levels.md` | Excluded historical/internal content |
| `internals/09-tool-development.md` | Excluded historical/internal content |
| `internals/11-provider-development-guide.md` | Excluded historical/internal content |
| `internals/agent-events.md` | Excluded historical/internal content |
| `internals/README.md` | Excluded historical/internal content |
| `internals/tool-security.md` | Excluded historical/internal content |
| `observability.md` | Architecture guide |
| `providers/anthropic.md` | User guide |
| `providers/github-copilot.md` | User guide |
| `providers/github-models.md` | User guide |
| `providers/ollama.md` | User guide |
| `providers/openai-compatible.md` | User guide |
| `providers/openai.md` | User guide |
| `releases/index.md` | Releases |
| `releases/v0.1.1/index.md` | Releases |
| `releases/v0.1.10/index.md` | Releases |
| `releases/v0.1.11/index.md` | Releases |
| `releases/v0.1.12/index.md` | Releases |
| `releases/v0.1.13/index.md` | Releases |
| `releases/v0.1.14/index.md` | Releases |
| `releases/v0.1.15/index.md` | Releases |
| `releases/v0.1.2/index.md` | Releases |
| `releases/v0.1.3/index.md` | Releases |
| `releases/v0.1.4/index.md` | Releases |
| `releases/v0.1.5/index.md` | Releases |
| `releases/v0.1.6/index.md` | Releases |
| `releases/v0.1.7/index.md` | Releases |
| `releases/v0.1.8/index.md` | Releases |
| `releases/v0.1.9/index.md` | Releases |
| `releases/v0.10.0/index.md` | Releases |
| `releases/v0.10.1/index.md` | Releases |
| `releases/v0.11.0/index.md` | Releases |
| `releases/v0.12.0/index.md` | Releases |
| `releases/v0.12.1/index.md` | Releases |
| `releases/v0.13.0/index.md` | Releases |
| `releases/v0.14.0/index.md` | Releases |
| `releases/v0.15.0/index.md` | Releases |
| `releases/v0.16.0/index.md` | Releases |
| `releases/v0.17.0/index.md` | Releases |
| `releases/v0.18.0/index.md` | Releases |
| `releases/v0.19.0/index.md` | Releases |
| `releases/v0.2.0/index.md` | Releases |
| `releases/v0.2.1/index.md` | Releases |
| `releases/v0.2.2/index.md` | Releases |
| `releases/v0.20.0/index.md` | Releases |
| `releases/v0.21.0/index.md` | Releases |
| `releases/v0.22.0/index.md` | Releases |
| `releases/v0.23.0/index.md` | Releases |
| `releases/v0.24.0/index.md` | Releases |
| `releases/v0.25.0/index.md` | Releases |
| `releases/v0.26.0/index.md` | Releases |
| `releases/v0.27.0/index.md` | Releases |
| `releases/v0.28.0/index.md` | Releases |
| `releases/v0.29.0/index.md` | Releases |
| `releases/v0.3.0/index.md` | Releases |
| `releases/v0.30.0/index.md` | Releases |
| `releases/v0.31.0/index.md` | Releases |
| `releases/v0.32.0/index.md` | Releases |
| `releases/v0.33.0/index.md` | Releases |
| `releases/v0.34.0/index.md` | Releases |
| `releases/v0.35.0/index.md` | Releases |
| `releases/v0.36.0/index.md` | Releases |
| `releases/v0.37.0/index.md` | Releases |
| `releases/v0.38.0/index.md` | Releases |
| `releases/v0.38.1/index.md` | Releases |
| `releases/v0.39.0/index.md` | Releases |
| `releases/v0.4.0/index.md` | Releases |
| `releases/v0.40.0/index.md` | Releases |
| `releases/v0.41.0/index.md` | Releases |
| `releases/v0.42.0/index.md` | Releases |
| `releases/v0.43.0/index.md` | Releases |
| `releases/v0.44.0/index.md` | Releases |
| `releases/v0.45.0/index.md` | Releases |
| `releases/v0.5.0/index.md` | Releases |
| `releases/v0.6.0/index.md` | Releases |
| `releases/v0.7.0/index.md` | Releases |
| `releases/v0.8.0/index.md` | Releases |
| `releases/v0.8.1/index.md` | Releases |
| `releases/v0.9.0/index.md` | Releases |
| `seam-test-reviewer-checklist.md` | Developer guide |
| `signalr-hub-contract.md` | Developer guide |
| `signalr-mobile-keepalive.md` | Developer guide |
| `skills.md` | User guide |
| `tutorials/first-agent.md` | User guide |
| `user-guide/agents.md` | User guide |
| `user-guide/automation.md` | User guide |
| `user-guide/capabilities.md` | User guide |
| `user-guide/channels/service-bus-envelope.md` | User guide |
| `user-guide/channels/service-bus.md` | User guide |
| `user-guide/channels/signalr.md` | User guide |
| `user-guide/channels/telegram.md` | User guide |
| `user-guide/configuration.md` | User guide |
| `user-guide/conversations.md` | User guide |
| `user-guide/extensions.md` | Developer guide |
| `user-guide/getting-started.md` | User guide |
| `user-guide/providers.md` | User guide |
| `user-guide/README.md` | User guide |
| `user-guide/secrets-and-locations.md` | User guide |
| `user-guide/troubleshooting.md` | User guide |
| `user-guide/usage-ideas.md` | User guide |
| `user-guide/using-extensions.md` | User guide |
| `webui/sub-agent-sessions.md` | Excluded historical/internal content |
| `development/documentation-map.md` | Developer guide |
