import { defineConfig } from 'vitepress'

// https://vitepress.dev/reference/site-config
export default defineConfig({
  title: 'BotNexus',
  description: 'A modular AI agent execution platform',
  base: '/botnexus/',

  // Ignore dead links to source files and localhost URLs (expected in docs)
  ignoreDeadLinks: [
    // Source code links (referenced in dev docs but not part of docs build)
    /\/src\//,
    // Repo scripts referenced from dev docs but outside the docs source tree
    /\/scripts\//,
    // srcExclude'd content directories: referenced as related reading but not
    // part of the deployed docs build (kept in repo, see srcExclude below)
    /\/internals\//,
    /\/api\//,
    // Repo-root and nested AGENTS.md convention files referenced from dev/architecture
    // docs but outside the docs source tree
    /AGENTS(\.md)?$/,
    // localhost links (expected in setup guides)
    /localhost/,
  ],
  srcExclude: [
    '**/planning/**',
    '**/internals/**',
    '**/archive/**',
    '**/archived/**',
    '**/sample-config.json',
    '**/botnexus-config.schema.json',
    '**/webui/**',
    // NOTE: docs/api/ is NOT excluded -- api/webhooks.md is an intended,
    // linked reference page (added in #1791). Only openapi.json lives
    // alongside it and is not a build input. Excluding '**/api/**' made
    // every link to api/webhooks.md a dead link and broke the docs build (#1816).
  ],

  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: '/botnexus/logo.svg' }],
  ],

  themeConfig: {
    // https://vitepress.dev/reference/default-theme-config
    logo: { svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path fill="currentColor" d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Zm1 17.93V18a1 1 0 0 0-2 0v1.93A8 8 0 0 1 4.07 13H6a1 1 0 0 0 0-2H4.07A8 8 0 0 1 11 4.07V6a1 1 0 0 0 2 0V4.07A8 8 0 0 1 19.93 11H18a1 1 0 0 0 0 2h1.93A8 8 0 0 1 13 19.93Z"/></svg>' },

    nav: [
      { text: 'Home', link: '/' },
      { text: 'Getting Started', link: '/getting-started' },
      { text: 'User Guide', link: '/user-guide/getting-started' },
      { text: 'Reference', link: '/cli-reference' },
      { text: 'Architecture', link: '/architecture/overview' },
      { text: 'Development', link: '/development/README' },
      { text: 'Releases', link: '/releases/' },
    ],

    sidebar: [
      {
        text: 'Getting Started',
        items: [
          { text: 'Overview', link: '/getting-started' },
          { text: 'Install from Release', link: '/getting-started-release' },
          { text: 'Developer Setup', link: '/getting-started-dev' },
        ],
      },
      {
        text: 'Tutorials',
        items: [
          { text: 'Your First AI Agent', link: '/tutorials/first-agent' },
        ],
      },
      {
        text: 'User Guide',
        items: [
          { text: 'Getting Started', link: '/user-guide/getting-started' },
          { text: 'Agents', link: '/user-guide/agents' },
          { text: 'Conversations', link: '/user-guide/conversations' },
          { text: 'Automation', link: '/user-guide/automation' },
          { text: 'Configuration', link: '/user-guide/configuration' },
          { text: 'Servers, Credentials and Agents', link: '/user-guide/secrets-and-locations' },
          { text: 'Extensions', link: '/user-guide/extensions' },
          {
            text: 'Channels',
            items: [
              { text: 'SignalR (Web Portal)', link: '/user-guide/channels/signalr' },
              { text: 'Telegram', link: '/user-guide/channels/telegram' },
              { text: 'Azure Service Bus', link: '/user-guide/channels/service-bus' },
              { text: 'Service Bus Envelope', link: '/user-guide/channels/service-bus-envelope' },
            ],
          },
          { text: 'Troubleshooting', link: '/user-guide/troubleshooting' },
        ],
      },
      {
        text: 'Reference',
        items: [
          { text: 'Configuration', link: '/configuration' },
          { text: 'CLI Reference', link: '/cli-reference' },
          { text: 'API Reference', link: '/api-reference' },
          { text: 'REST API Reference', link: '/api/README' },
          { text: 'SignalR Hub Contract', link: '/signalr-hub-contract' },
          { text: 'SignalR Mobile Keepalive', link: '/signalr-mobile-keepalive' },
        ],
      },
      {
        text: 'Providers',
        items: [
          { text: 'Anthropic', link: '/providers/anthropic' },
          { text: 'OpenAI', link: '/providers/openai' },
          { text: 'OpenAI-Compatible', link: '/providers/openai-compatible' },
          { text: 'GitHub Copilot', link: '/providers/github-copilot' },
          { text: 'GitHub Models', link: '/providers/github-models' },
          { text: 'Ollama', link: '/providers/ollama' },
        ],
      },
      {
        text: 'Extensions',
        items: [
          { text: 'Extension Development', link: '/extension-development' },
          { text: 'Exec Tool', link: '/extensions/exec-tool' },
          { text: 'Process Tool', link: '/extensions/process-tool' },
          { text: 'Web Tools', link: '/extensions/web-tools' },
          { text: 'Browser Tools', link: '/extensions/browser-tools' },
          { text: 'Data Store', link: '/extensions/data-store' },
          { text: 'Skills', link: '/extensions/skills' },
          { text: 'MCP', link: '/extensions/mcp' },
          { text: 'MCP Invoke', link: '/extensions/mcp-invoke' },
          { text: 'QMD (Knowledge Base)', link: '/extensions/qmd' },
          { text: 'Debug Tool', link: '/extensions/debug-tool' },
          { text: 'GitHub', link: '/extensions/github' },
          { text: 'Media Handlers', link: '/extensions/media-handlers' },
          { text: 'Agent 365 Channel', link: '/extensions/agent365' },
          { text: 'Matrix Channel', link: '/extensions/matrix' },
          { text: 'Test Channel', link: '/extensions/test-channel' },
          { text: 'Extension Telemetry', link: '/extensions/telemetry' },
        ],
      },
      {
        text: 'Architecture',
        items: [
          { text: 'Overview', link: '/architecture/overview' },
          { text: 'arc42-lite Overview', link: '/architecture/README' },
          { text: 'C4 Diagrams', link: '/architecture/c4-diagrams' },
          { text: 'Runtime View', link: '/architecture/runtime-view' },
          { text: 'Domain Model', link: '/architecture/domain-model' },
          { text: 'Gateway Flow', link: '/architecture/gateway-flow' },
          { text: 'Channel Binding', link: '/architecture/channel-binding' },
          { text: 'Conversation-Scoped Events', link: '/architecture/conversation-scoped-events' },
          { text: 'Portal PWA Caching', link: '/architecture/portal-pwa-caching' },
          { text: 'Extension Guide', link: '/architecture/extension-guide' },
          { text: 'Plugin Architecture', link: '/architecture/plugins' },
          { text: 'Principles', link: '/architecture/principles' },
          { text: 'System Flows', link: '/architecture/system-flows' },
          { text: 'Decision Records (ADRs)', link: '/architecture/adr/README' },
        ],
      },
      {
        text: 'Development',
        items: [
          { text: 'Overview', link: '/development/README' },
          { text: 'Agent Execution', link: '/development/agent-execution' },
          { text: 'Message Flow', link: '/development/message-flow' },
          { text: 'Inbound Delivery Modes', link: '/development/inbound-delivery-modes' },
          { text: 'LLM Request Lifecycle', link: '/development/llm-request-lifecycle' },
          { text: 'Normalized LLM Event Audit', link: '/development/normalized-llm-event-audit' },
          { text: 'Prompt Pipeline', link: '/development/prompt-pipeline' },
          { text: 'Connection Registry and Secrets', link: '/development/connection-registry-and-secrets' },
          { text: 'Session Stores', link: '/development/session-stores' },
          { text: 'Workspace and Memory', link: '/development/workspace-and-memory' },
          { text: 'DDD Patterns', link: '/development/ddd-patterns' },
          { text: 'Code Standards', link: '/development/code-standards' },
          { text: 'Issue Conventions', link: '/development/issue-conventions' },
          { text: 'CLI Wizard Framework', link: '/development/cli-wizard' },
          { text: 'Container Integration Testing', link: '/development/container-integration-testing' },
          { text: 'Scenario Test Framework Decision', link: '/development/scenario-test-framework-decision' },
          { text: 'E2E Tests', link: '/development/e2e-tests' },
          { text: 'Triggers and Federation', link: '/development/triggers-and-federation' },
          { text: 'WebUI Connection', link: '/development/webui-connection' },
          { text: 'App Integration Surfaces', link: '/development/app-integration-surfaces' },
          { text: 'Security-Sensitive File Guard', link: '/development/security-sensitive-file-guard' },
          { text: 'Comment Moderation', link: '/development/comment-moderation' },
          { text: 'Downloaded Payload Verification', link: '/development/downloaded-payload-verification' },
          { text: 'Git Worktree Config Hardening', link: '/development/git-worktree-config-hardening' },
          { text: 'Gateway Crash Diagnostics', link: '/development/gateway-crash-diagnostics' },
          { text: 'Running Impacted Tests', link: '/development/running-tests' },
          { text: 'Persistence Seam Testing', link: '/development/persistence-seam-testing' },
          { text: 'Pre-Commit Gate', link: '/development/pre-commit-gate' },
          { text: 'Stale-Base Merges', link: '/development/stale-base-merges' },
          { text: 'Documentation Grooming', link: '/development/documentation-grooming' },
          { text: 'Azure Build and Test Runner', link: '/development/azure-build-test-runner' },
          { text: 'Maintenance Orchestration', link: '/development/autonomous-maintenance-orchestration' },
          { text: 'Validation Receipts', link: '/development/validation-receipts' },
          { text: 'Spike: Workflow Conversations', link: '/development/spike-workflow-conversations' },
          { text: 'Debugging', link: '/development/debugging' },
          { text: 'Compat Shim Lifecycle', link: '/development/compat-shim-lifecycle' },
          { text: 'PR and Commit Conventions', link: '/development/pr-and-commit-conventions' },
          { text: 'Source Generator Survey', link: '/development/source-generator-survey' },
          { text: 'Hub Event Inventory Generator', link: '/development/hub-event-inventory-generator' },
          { text: 'Tool Schema Generator Spike', link: '/development/tool-schema-generator-spike' },
          { text: 'Portal Surface Parity', link: '/development/portal-surface-parity' },
          { text: 'Seam-Test Reviewer Checklist', link: '/seam-test-reviewer-checklist' },
        ],
      },
      {
        text: 'Features',
        items: [
          { text: 'Sub-Agent Spawning', link: '/features/sub-agent-spawning' },
          { text: 'Built-in Agents', link: '/features/built-in-agents' },
          { text: 'Shell Execution', link: '/features/shell-execution' },
          { text: 'Canvas', link: '/features/canvas' },
          { text: 'Per-Conversation Todo', link: '/features/todo' },
          { text: 'Agent Exchange', link: '/features/agent-exchange' },
          { text: 'Security-Event Diagnostics', link: '/features/security-event-diagnostics' },
          { text: 'File-Backed Secrets', link: '/features/file-backed-secrets' },
          { text: 'Tool-Audit Write-Ahead', link: '/features/tool-audit-write-ahead' },
          { text: 'Per-Session Tool Overrides', link: '/features/session-tool-overrides' },
          { text: 'Cron Session Targets', link: '/features/cron-session-targets' },
          { text: 'Dev-Mode Origin Guard', link: '/features/dev-origin-guard' },
          { text: 'Portal Boot Diagnostics', link: '/features/portal-boot-diagnostics' },
          { text: 'Portal Plugins Page', link: '/features/portal-plugins-page' },
          { text: 'AGENTS.md Conventions', link: '/features/agents-md-conventions' },
          { text: 'Model-Specific Instruction Files', link: '/features/model-specific-instruction-files' },
          { text: 'Model Awareness', link: '/features/model-awareness' },
          { text: 'Agent 365 Observability', link: '/features/agent365-observability' },
          { text: 'Agent 365 Admin & Onboarding', link: '/features/agent365-onboarding' },
          { text: 'Session Consistency', link: '/features/session-consistency' },
          { text: 'Conversation Provenance', link: '/features/conversation-provenance' },
          { text: 'Conversation Cost', link: '/features/conversation-cost' },
          { text: 'Cron Cost', link: '/features/cron-cost' },
          { text: 'Hybrid Memory Retrieval', link: '/features/hybrid-memory-retrieval' },
          { text: 'Memory Taint Quarantine', link: '/features/memory-taint-quarantine' },
          { text: 'Provider Health Events', link: '/features/provider-health-events' },
          { text: 'Skills', link: '/skills' },
          { text: 'Cron & Scheduling', link: '/cron-and-scheduling' },
        ],
      },
      {
        text: 'Guides',
        items: [
          { text: 'Audio Recording', link: '/guides/audio-recording' },
          { text: 'Gateway Recovery', link: '/guides/gateway-recovery' },
          { text: 'Observability', link: '/observability' },
          { text: 'Offline Install', link: '/guides/offline-install' },
          { text: 'Watchdog Setup', link: '/guides/watchdog-setup' },
          { text: 'Webhooks', link: '/guides/webhooks' },
        ],
      },
      {
        text: 'Releases',
        link: '/releases/',
      },
    ],

    socialLinks: [
      { icon: 'github', link: 'https://github.com/sytone/botnexus' },
    ],

    search: {
      provider: 'local',
    },

    editLink: {
      pattern: 'https://github.com/sytone/botnexus/edit/main/docs/:path',
      text: 'Edit this page on GitHub',
    },

    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © BotNexus Contributors',
    },
  },
})
