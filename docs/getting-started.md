# Getting Started with BotNexus

BotNexus runs AI assistants called **agents**. You can chat with them in a browser and give them tools for tasks such as reading files or keeping a checklist. You choose which tools and information an agent may use.

## Choose Your Path

| What you want to do | Start here |
| --- | --- |
| Install BotNexus and use an agent | [User guide](user-guide/README.md) |
| Choose a model service or connect an account | [Providers and models](user-guide/providers.md) |
| Add tools and other capabilities | [Use extensions](user-guide/using-extensions.md) |
| Find useful things to try | [Usage ideas](user-guide/usage-ideas.md) |
| Change BotNexus code or build an extension | [Developer guide](development/README.md) |
| Understand how BotNexus works inside | [Architecture guide](architecture/README.md) |

## Prerequisites (Both Paths)

The requirements depend on the task. Read the chosen guide before installing software or running commands.

- **To install and use BotNexus:** the current [installation guide](getting-started-release.md) requires Git and a .NET SDK compatible with the downloaded source. The SDK contains the tools that build BotNexus. The runtime alone is not enough for that source-build workflow.
- **To connect a model:** choose a provider you can access. Some require an account, subscription or API key. A GitHub Copilot subscription is needed for the Copilot route, not for every provider.
- **To contribute code:** follow [Developer setup](getting-started-dev.md) for the SDK, PowerShell and Git requirements. Keep development and tests separate from a running installation.

A browser is needed for the web interface. Some setup steps use a **terminal**, an application where you type commands. The [provider guide](user-guide/providers.md#before-you-start) explains how to prepare for those steps after installation.

## What is BotNexus?

Think of an agent as an assistant with instructions and a set of allowed actions. A **provider** supplies the language model that writes its responses. An **extension** adds a capability, such as web access or a connection to another service.

You do not need to write code to use a capability that is already installed and configured. You do need to understand its access requirements and limits. Start with [Use BotNexus features](user-guide/capabilities.md).

## Quick Overview

1. Follow [Install BotNexus](getting-started-release.md). That guide owns the installation commands.
2. Connect a provider and choose an available model.
3. Open the web interface and send a first message.
4. Add only the capabilities you need. Check the result of each change before adding another.

### Using the CLI

The **command-line interface (CLI)** is the `botnexus` program used in setup instructions. Use the exact commands in the relevant task guide. The [CLI reference](cli-reference.md) describes their options.

Do not paste commands from a different shell or another installation without checking their paths and placeholders. Never paste a real API key into a public issue or example.

## Directory Layout

BotNexus source code, installed tools and user data are different things. The [installation guide's location table](getting-started-release.md#2-initialize-botnexus) explains the defaults. Custom home or data locations can change them.

Use the [configuration guide](configuration.md) before changing storage settings. A configuration backup is not a backup of every conversation, memory store or workspace.

## Next Steps

- [User guide](user-guide/README.md): everyday use, settings, troubleshooting and backups.
- [Providers and models](user-guide/providers.md): choose and connect a model service.
- [Use extensions](user-guide/using-extensions.md): find and set up capabilities.
- [Use BotNexus features](user-guide/capabilities.md): understand memory, scheduling, Canvas and other features.
- [Usage ideas](user-guide/usage-ideas.md): try a small, safe task and check its result.
- [Extension Development](extension-development.md): build a capability rather than configure an existing one.
- [Workspace & Memory](development/workspace-and-memory.md) and [Architecture Overview](architecture/overview.md): deeper technical reading.
