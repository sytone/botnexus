# User guide

Use this guide to install BotNexus, chat with an agent, and manage your settings. You need basic computer knowledge, but you do not need to know how to program.

An **agent** is an artificial intelligence (AI) assistant with its own instructions and available tools. The **web interface**, also called the WebUI or portal, is where you use BotNexus in your browser. The **gateway** is the service that runs agents and handles messages.

## Start here

The current installation steps use a terminal, an application where you type commands. The linked guide still assumes some terminal knowledge. If opening a terminal or choosing the right command is unfamiliar, ask for help before running the steps. We are adding more beginner-level explanations as these guides improve.

1. **[Install BotNexus](../getting-started-release.md).** Read the prerequisites before running commands. The current installation guide uses the released command-line interface (CLI), a program you use by typing commands in a terminal. It then builds the gateway from a copy of the source code. It does not install a prebuilt gateway package.
2. **[Set up a provider](../getting-started-release.md#4-configure-your-first-provider-copilot).** A provider is the service that supplies the language model used by your agent. This walkthrough uses GitHub Copilot; check its account requirements and protect your sign-in credentials.
3. **[Send your first message](../getting-started-release.md#send-your-first-message).** After completing setup, open Chat in the web interface, select an agent, and start a conversation.

If you want to change BotNexus's source code, use the [Developer guide](../development/README.md) instead.

## Everyday use

| I want to... | Read |
| --- | --- |
| Find my way around the web interface | [Web interface layout](../getting-started-release.md#the-web-interface-layout) |
| Continue a chat or understand a new session | [Conversations](conversations.md) |
| Learn about agent settings and instructions | [Working with agents](agents.md) |
| Check the gateway or read logs | [Manage your system](../getting-started-release.md#8-manage-your-system) |

A **conversation** is a saved thread of messages. A **session** is the agent's active context within that conversation. They are not the same thing; the Conversations guide explains when to use each.

## Change settings

Start with [Configuration](configuration.md) for agent, provider, and gateway settings. Use the [configuration reference](../configuration.md) when you need details about a setting. The [CLI reference](../cli-reference.md) lists commands and options.

Follow the linked guide's warnings before changing settings. Do not share passwords, access tokens, or files containing credentials in chat examples or public issue reports.

## Solve a problem

Start with [installation troubleshooting](../getting-started-release.md#troubleshooting) if setup failed. For problems after setup, use the [Troubleshooting guide](troubleshooting.md) to find the matching symptom.

## Update and back up

- **[Update BotNexus](../getting-started-release.md#update-to-a-new-version):** read what the update changes before running it. Updating the gateway source and updating the CLI package are separate operations. An update can interrupt active work.
- **[Back up your data](../getting-started-release.md#9-back-up-your-data):** check which data is included and where your installation stores it. Configuration backups are not complete installation backups. Follow the guide's database-safety instructions before copying data.
- **[Review release notes](../releases/):** check changes relevant to your installation.

## Other guides

- [Developer guide](../development/README.md): build from source or contribute changes.
- [Architecture guide](../architecture/README.md): study how the components work together and why they are designed that way.

Existing pages are being improved incrementally using the [documentation standards](../development/documentation-standards.md). Some linked pages still contain more technical detail than this introduction.
