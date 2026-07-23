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
    /\/training\//,
    /\/api\//,
    // localhost links (expected in setup guides)
    /localhost/,
  ],
  srcExclude: [
    '**/planning/**',
    '**/training/**',
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
          { text: 'Configuration', link: '/user-guide/configuration' },
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
          { text: 'Data Store', link: '/extensions/data-store' },
          { text: 'Skills', link: '/extensions/skills' },
          { text: 'MCP', link: '/extensions/mcp' },
          { text: 'MCP Invoke', link: '/extensions/mcp-invoke' },
          { text: 'QMD (Knowledge Base)', link: '/extensions/qmd' },
          { text: 'Debug Tool', link: '/extensions/debug-tool' },
          { text: 'Media Handlers', link: '/extensions/media-handlers' },
          { text: 'Agent 365 Channel', link: '/extensions/agent365' },
          { text: 'Extension Telemetry', link: '/extensions/telemetry' },
        ],
      },
      {
        text: 'Architecture',
        items: [
          { text: 'Overview', link: '/architecture/overview' },
          { text: 'Domain Model', link: '/architecture/domain-model' },
          { text: 'Gateway Flow', link: '/architecture/gateway-flow' },
          { text: 'Channel Binding', link: '/architecture/channel-binding' },
          { text: 'Extension Guide', link: '/architecture/extension-guide' },
          { text: 'Principles', link: '/architecture/principles' },
          { text: 'System Flows', link: '/architecture/system-flows' },
        ],
      },
      {
        text: 'Development',
        items: [
          { text: 'Overview', link: '/development/README' },
          { text: 'Agent Execution', link: '/development/agent-execution' },
          { text: 'Message Flow', link: '/development/message-flow' },
          { text: 'LLM Request Lifecycle', link: '/development/llm-request-lifecycle' },
          { text: 'Prompt Pipeline', link: '/development/prompt-pipeline' },
          { text: 'Session Stores', link: '/development/session-stores' },
          { text: 'Workspace and Memory', link: '/development/workspace-and-memory' },
          { text: 'DDD Patterns', link: '/development/ddd-patterns' },
          { text: 'CLI Wizard Framework', link: '/development/cli-wizard' },
          { text: 'Container Integration Testing', link: '/development/container-integration-testing' },
          { text: 'E2E Tests', link: '/development/e2e-tests' },
          { text: 'Triggers and Federation', link: '/development/triggers-and-federation' },
          { text: 'WebUI Connection', link: '/development/webui-connection' },
          { text: 'Security-Sensitive File Guard', link: '/development/security-sensitive-file-guard' },
          { text: 'Git Worktree Config Hardening', link: '/development/git-worktree-config-hardening' },
          { text: 'Gateway Crash Diagnostics', link: '/development/gateway-crash-diagnostics' },
          { text: 'Running Impacted Tests', link: '/development/running-tests' },
          { text: 'Azure Build and Test Runner', link: '/development/azure-build-test-runner' },
          { text: 'Maintenance Orchestration', link: '/development/autonomous-maintenance-orchestration' },
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
          { text: 'Dev-Mode Origin Guard', link: '/features/dev-origin-guard' },
          { text: 'AGENTS.md Conventions', link: '/features/agents-md-conventions' },
          { text: 'Agent 365 Observability', link: '/features/agent365-observability' },
          { text: 'Skills', link: '/skills' },
          { text: 'Cron & Scheduling', link: '/cron-and-scheduling' },
        ],
      },
      {
        text: 'Guides',
        items: [
          { text: 'Audio Recording', link: '/guides/audio-recording' },
          { text: 'Observability', link: '/observability' },
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
