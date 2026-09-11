# Your First AI Agent

Install BotNexus with its command-line interface (CLI), create a named agent and chat with it in your browser. You do not need to learn Git, write code or edit a JSON configuration file.

## What you'll build

You will create **My First Agent**, give it short written instructions and try a conversation. The example uses an OpenAI API key. If you use another service, follow [Providers and models](../user-guide/providers.md) and substitute its configured provider name and available model ID.

This is a source-checked tutorial, not a record of a completed clean-install test. Stop at a failed step rather than continuing with an incomplete setup.

## 1) Prerequisites

- A Windows or Linux computer where you can install software.
- The [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Git](https://git-scm.com/downloads). The current source specifies SDK `10.0.204` with compatible minor-version updates. The CLI uses these tools internally to download and prepare BotNexus. You do not run Git commands in this tutorial.
- An OpenAI API key and access to a model. Check your account's current model access and price; an API request can use paid quota.
- A browser and a plain-text editor, such as Notepad on Windows or your desktop text editor on Linux.

Open **PowerShell** from the Windows Start menu, or your Linux desktop's **Terminal** application. The commands below work in PowerShell or Bash. Run one line at a time by pressing Enter, and wait for it to finish.

Use your normal operating-system account, not an administrator account unless an installer specifically requires it. Commands use that account's default BotNexus home, normally `.botnexus` inside your home folder. You can run them from any folder. If you already operate a custom-home installation, use its existing home settings consistently; do not create a second gateway against it.

::: warning
Keep API keys private. Enter your key only at the provider wizard's secret prompt. Do not paste it into a chat, screenshot or public issue. This walkthrough creates a new agent; it must not replace an existing agent with the same ID.
:::

<a id="_2-clone-and-build-botnexus"></a>

## 2) Install BotNexus

Install the CLI:

```powershell
dotnet tool install -g BotNexus.Cli
```

If the tool is already installed, do not reinstall it or remove your settings. Continue with your installed CLI. If the next command is not recognized, reopen the terminal and check the installation message for the tool-path instructions.

Ask the CLI to download and prepare BotNexus:

```powershell
botnexus install --build
```

Wait until it finishes without an error. The CLI handles downloading and building; you do not need to open a repository or run a compiler yourself. The current installer still needs the Git and SDK prerequisites above. See [installation details](../getting-started-release.md) for an existing or custom-location installation.

Create the initial settings:

```powershell
botnexus init
```

An existing configuration is kept. Do not add `--force`: that option can overwrite it.

## 3) Configure a provider

Run:

```powershell
botnexus provider setup --provider openai
```

Enter the API key at the secret prompt and follow the model-selection questions. Note the exact model ID you intend to use. A model shown in a built-in list is not a guarantee that your account can access it.

Check the saved provider:

```powershell
botnexus provider list
```

Confirm `openai` appears. This checks saved settings; your first chat will check whether a model request succeeds.

## 4) Create your first agent

In the following command, replace `MODEL_ID` with an exact model ID available to your account. Keep the quotes. Do not run it with the placeholder unchanged.

```powershell
botnexus agent add my-first-agent --provider openai --model 'MODEL_ID' --display-name 'My First Agent' --disabled
```

If the command reports that this ID exists, stop. Inspect it with `botnexus agent show my-first-agent` before deciding whether to use it. Do not overwrite another agent to follow a tutorial.

The new agent starts disabled. Agent creation also supplies heartbeat settings for periodic activity. Turn that activity off for this exercise:

```powershell
botnexus config set agents.my-first-agent.heartbeat.enabled false
```

Check the new agent:

```powershell
botnexus agent show my-first-agent
```

Confirm the provider, model and disabled state. Do not continue after a failed configuration command.

## 5) Add a system prompt

A **system prompt** is written guidance for the agent. The CLI creates a workspace with instruction files when it creates the agent.

Open `SOUL.md` in that workspace with your plain-text editor:

- **Windows:** open File Explorer and enter `%USERPROFILE%\.botnexus\agents\my-first-agent\workspace` in the address bar. Open `SOUL.md` in Notepad or another text editor.
- **Linux:** open your file manager, show hidden files if needed, then open `.botnexus/agents/my-first-agent/workspace` inside your home folder. Open `SOUL.md` in a text editor.

These paths assume the default home. For a custom home, start from that folder instead. If the workspace or file is missing, stop and check the agent creation result rather than editing another agent's files.

For this new tutorial agent, save the following text in `SOUL.md`. Keep the name `SOUL.md`, not `SOUL.md.txt`.

```markdown
# My First Agent

You are a friendly assistant called Botty.
Use short sentences and explain unfamiliar terms.
Give practical answers and say when you are unsure.
Do not run tools or change anything for this tutorial.
```

This is guidance to the model, not a security restriction on its tools. Do not give the test agent sensitive information or rely on this text as an access-control rule.

The default prompt-file selection includes `SOUL.md`; no JSON edit or additional prompt-file setting is needed for this fresh agent.

## 6) Run the gateway

Enable your configured agent:

```powershell
botnexus config set agents.my-first-agent.enabled true
botnexus validate
```

Resolve validation errors before starting. Validation checks configuration, not your provider's live account access.

Check gateway status:

```powershell
botnexus gateway status
```

If it is stopped, start it:

```powershell
botnexus gateway start
```

If a gateway already runs for this home, do not launch another instance. Start may prepare the installed code before launching the background service. Wait for success, then check `botnexus gateway status` again.

Open `http://localhost:5005` in your browser for a default local installation. If you configured a different address, use that address instead. Do not expose the gateway to other networks just to complete this tutorial.

## 7) Chat with your agent

### CLI first: send one request

This sends a real request to your running local gateway and may use provider quota:

```powershell
botnexus agent exec my-first-agent 'Hi Botty. Explain what an agent is in three short sentences. Do not use tools.'
```

Check for an answer in the terminal and resolve any reported error. This is a single request, not an interactive terminal chat. It uses the default local gateway address; if your gateway uses a different address, consult the CLI reference for `agent exec` connection options. Selecting a custom settings home does not change this request address automatically.

### UI alternative: chat in the browser

1. Open the web interface and choose **My First Agent** (`my-first-agent`).
2. Start a conversation with it.
3. Send: “Hi Botty. Explain what an agent is in three short sentences. Do not use tools.”
4. Check that you receive a response without a provider error.

Responses vary. A successful reply proves a basic model request worked, not that every tool or feature is configured.

If the agent is missing, inspect `botnexus agent show my-first-agent`. If a provider error appears, check the configured account, provider name and exact model ID. Use [provider troubleshooting](../user-guide/providers.md#if-something-goes-wrong), not manual JSON changes.

## 8) Customize behavior

Edit the same `SOUL.md` file. For example, change the style instruction to:

```markdown
Use numbered steps when explaining a task.
Keep routine answers under 100 words when possible.
```

Save the file. A running agent may keep the instructions it loaded earlier. Saving the file does not guarantee that the next message uses the new instructions.

For this local tutorial installation, restart only when no other work is running:

```powershell
botnexus gateway restart
```

Restart interrupts work across this gateway, not only your test conversation. On a shared installation, arrange a safe time with its administrator instead.

Try the same question again and compare the answer. A changed style is an observation, not a guarantee that every instruction will always be followed.

## 9) Next steps

- [User guide](../user-guide/README.md): everyday tasks and settings.
- [Use extensions](../user-guide/using-extensions.md): add capabilities without writing code.
- [Usage ideas](../user-guide/usage-ideas.md): small tasks and result checks.
- [Configuration Reference](../configuration.md): advanced settings.
- [Extension Development](../extension-development.md): a separate developer path for writing code.

To stop using this test agent, disable it with `botnexus config set agents.my-first-agent.enabled false`. This is not a request to delete its workspace or conversation history.
