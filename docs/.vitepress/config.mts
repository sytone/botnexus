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
    '**/api/**',
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
              { text: 'Telegram', link: '/user-guide/channels/telegram' },
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
          { text: 'WebSocket Protocol', link: '/websocket-protocol' },
        ],
      },
      {
        text: 'Architecture',
        items: [
          { text: 'Overview', link: '/architecture/overview' },
          { text: 'Domain Model', link: '/architecture/domain-model' },
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
          { text: 'Triggers and Federation', link: '/development/triggers-and-federation' },
          { text: 'WebUI Connection', link: '/development/webui-connection' },
        ],
      },
      {
        text: 'Guides',
        items: [
          { text: 'Audio Recording', link: '/guides/audio-recording' },
          { text: 'Watchdog Setup', link: '/guides/watchdog-setup' },
        ],
      },
      {
        text: 'Features',
        items: [
          { text: 'Sub-Agent Spawning', link: '/features/sub-agent-spawning' },
        ],
      },
      {
        text: 'Extensions',
        items: [
          { text: 'Extension Development', link: '/extension-development' },
          { text: 'Media Handlers', link: '/extensions/media-handlers' },
        ],
      },
      {
        text: 'More',
        items: [
          { text: 'Observability', link: '/observability' },
          { text: 'Skills', link: '/skills' },
          { text: 'Cron & Scheduling', link: '/cron-and-scheduling' },
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
