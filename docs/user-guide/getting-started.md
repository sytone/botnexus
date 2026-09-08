# Getting Started with BotNexus

Install and use BotNexus through its command-line interface (CLI), the `botnexus` program. You do not need Git commands, repository knowledge or programming experience.

For a complete step-by-step exercise, follow [Your First AI Agent](../tutorials/first-agent.md). This page provides a shorter route and explains the web interface.

## Prerequisites

- Install the .NET SDK and Git required by the [installation guide](../getting-started-release.md#prerequisites). The CLI uses them internally to prepare BotNexus; Git knowledge is not required.
- Choose a [provider](providers.md) you can access. Prepare its account or API key and check its costs.
- Have a browser and a terminal: PowerShell on Windows or a Bash terminal on Linux.

Use the same operating-system account and BotNexus home throughout. Commands below assume the default installation and can run from any folder. If your installation uses a custom home, follow its existing settings rather than creating another one.

## Installation

<a id="_1-clone-the-repository"></a>
<a id="_2-build-the-solution"></a>

Install the CLI if it is not already installed:

```powershell
dotnet tool install -g BotNexus.Cli
```

Then let it download and prepare BotNexus:

```powershell
botnexus install --build
botnexus init
```

Run one command at a time and stop if it fails. The runtime alone cannot perform the installation's internal build; the SDK and Git remain required tools. Do not use `init --force` to fix an error: it can overwrite configuration.

If `botnexus` is not recognized, reopen the terminal and follow the tool-path instructions from installation. For restricted networks or custom locations, see [installation details](../getting-started-release.md).

## Configuration

### Configure Your First Provider

```powershell
botnexus provider setup
botnexus provider list
```

Use the setup wizard's sign-in or secret-key prompt. Do not paste real credentials into configuration examples, chats or public issues. The [provider guide](providers.md) explains which services the wizard supports and how to check model access.

### Configure Your First Agent

A fresh initialization supplies an `assistant` entry. Inspect existing agents before adding another with the same name:

```powershell
botnexus agent list
```

For a new named agent, follow the [tutorial's creation steps](../tutorials/first-agent.md#_4-create-your-first-agent). They explain model placeholders, disabled-first setup, heartbeat settings and a workspace instruction file. Do not replace your existing settings with a sample JSON document.

### Apply Configuration Changes

Use CLI commands or supported configuration controls. Different settings have different reload behavior. For example, changing a listening address or refreshing a prompt already held by a running agent can require a planned restart. Follow the relevant setting's guide; do not assume every change applies immediately.

### Home Directory Structure

The [installation location table](../getting-started-release.md#2-initialize-botnexus) explains configuration, credentials, workspaces and logs. These locations can be customized. Configuration backups do not include all workspace or database content.

## First Run

<a id="option-1-using-the-dev-script-recommended"></a>
<a id="option-2-direct-command"></a>

Check settings and gateway status:

```powershell
botnexus validate
botnexus gateway status
```

If the gateway is stopped and configuration is valid, start it:

```powershell
botnexus gateway start
```

The gateway is the background service that runs agents. Do not start a second gateway against a home already in use. The CLI handles preparing and starting the installed application; no developer script is needed.

## Accessing the WebUI

Open `http://localhost:5005` for a default local installation, or your configured address. The **WebUI**, also called the portal, is the browser interface.

### Finding your way around

| Area | What it is for |
| --- | --- |
| **Home** | Choose an agent and begin work |
| **Chat** | Work with a conversation and its Workspace, Reports, Canvas and Todo views |
| **Agents** | Inspect and configure agents |
| **Configuration** | Manage platform settings through the available editor |
| **Skills** | Browse the instructions agents can load |
| **Cron Jobs** | Inspect scheduled work |
| **Activity** | Inspect work and usage information |
| **Tools** | Manage portal links to external websites, not the tools an agent calls |
| **Plugins** | Inspect installed plugin packages |
| **Guide** | Read the help supplied with the portal |

Available controls can differ by layout and version. An agent's actual tools depend on its configuration and permissions; a link in the portal's **Tools** area does not give it an executable tool.

## Verify Installation

<a id="health-check-endpoint"></a>
<a id="api-status"></a>

### CLI check

Run `botnexus gateway status` to check the process and `botnexus validate` to check settings. These do not prove that your model account works; send the test message below.

### UI check

Open the configured browser address. Check that the portal connects and your agent is available.

## Test Your Agent

### Using the CLI

After configuring an agent and starting your local gateway, run one request. Replace `AGENT_ID` with its exact ID from `botnexus agent list`:

```powershell
botnexus agent exec 'AGENT_ID' 'Explain what an agent is in three short sentences. Do not use tools.'
```

This sends a real model request and may use paid quota. A plain-language answer should appear in the terminal; report any error rather than assuming success. These examples use the default local gateway. A custom or remote address needs the supported connection settings in the CLI reference; changing the configuration home does not automatically select the request address. Do not publish gateway credentials.

### Using the WebUI

1. Choose your configured agent and start a conversation.
2. Send “Hello. Explain what you can help me with in three short bullets. Do not use tools.”
3. Check for a response without a provider error.

This is a suggested test, not a recorded result. The answer can vary and may use paid quota. Do not assume every capability named in a model's answer is actually available; check tools before relying on them.

<a id="using-the-rest-api"></a>

For this user task, use either method above. Programmatic integration is a separate developer task, not another required onboarding step.

## Next Steps

- [Providers and models](providers.md): account setup and model selection.
- [Working with agents](agents.md): agent settings and behavior.
- [Use extensions](using-extensions.md): configure additional capabilities.
- [Use features](capabilities.md) and [usage ideas](usage-ideas.md): useful tasks to try.
- [Configuration](configuration.md): detailed settings.

## Quick Reference

| Task | Command or location |
| --- | --- |
| Prepare the application | `botnexus install --build` |
| Configure a provider | `botnexus provider setup` |
| Inspect agents | `botnexus agent list` |
| Validate settings | `botnexus validate` |
| Start the gateway | `botnexus gateway start` |
| Inspect gateway status | `botnexus gateway status` |
| Default web interface | `http://localhost:5005` |

## Troubleshooting

Start with the failed step and record its exact error without secrets.

- Installation fails: check SDK/Git prerequisites and the [installation guide](../getting-started-release.md#troubleshooting).
- Agent is missing: run `botnexus agent list` and check its enabled state.
- Provider request fails: use [provider troubleshooting](providers.md#if-something-goes-wrong).
- Gateway or browser connection fails: use [gateway startup help](troubleshooting.md#gateway-wont-start).

Some older troubleshooting material is developer-oriented. Do not switch to manual source builds or repository operations as the ordinary user recovery path. Ask for help with the exact CLI failure instead.
