# Choose and connect a provider

A **provider** is the service that supplies an agent's language model. The **model** is the program that reads your request and produces a response. You can use an existing provider without writing code.

This guide explains the setup route in the current source. A saved setting or a listed model does not prove that your account can use it. Check the result with a small request before relying on the connection.

## Choose a service

| Service | What to prepare | Setup details |
| --- | --- | --- |
| GitHub Copilot | An account with the required Copilot access; a browser for sign-in | [Copilot guide](../providers/github-copilot.md) |
| OpenAI | An API key and access to the model you want | [OpenAI guide](../providers/openai.md) |
| Anthropic | An API key and access to the model you want | [Anthropic guide](../providers/anthropic.md) |
| Ollama | A running Ollama server with a model installed | [Ollama guide](../providers/ollama.md) |
| GitHub Models | The credentials and model access described by its own guide; this is not Copilot | [GitHub Models guide](../providers/github-models.md) |
| An OpenAI-compatible server | The server address, credentials if required, and exact model ID | [Compatible-server guide](../providers/openai-compatible.md) |

Check the service's account terms, price and data-handling rules before sending information. Do not assume a model is free because a local catalog has no price recorded. A local model also does not make every agent action offline: tools can still contact other services.

## Before you start

1. Complete [Install BotNexus](../getting-started-release.md). The commands below assume that the `botnexus` command is installed.
2. Open a terminal. On Windows, search the Start menu for PowerShell and open it. On Linux, open your desktop's Terminal application. The simple commands below work in PowerShell or Bash. Type one line and press Enter before continuing.
3. Use the same operating-system account and BotNexus home as your gateway. These provider commands can run from any folder when using the default home. If your installation uses a custom home, use its existing `BOTNEXUS_HOME` setting or the CLI's global `--target` option with an absolute path. Do not guess a different home.
4. Have the required sign-in details ready. Enter API keys only into the intended secret prompt. Do not add `--verbose` while collecting output to share, and do not paste credentials into a chat or public issue.

A **home** is the folder where this installation keeps settings and data. Changing the account or home may make a correctly configured provider appear missing.

## Connect your account

For a first setup, run:

```powershell
botnexus provider setup
```

Choose one of the wizard's supported services: GitHub Copilot, OpenAI, Anthropic or Ollama. Follow its questions. For Copilot, use the browser sign-in instructions it displays. For API-key services, enter your key at the secret prompt.

To start directly with a particular API-key service, use **one** of these commands instead:

```powershell
botnexus provider setup --provider openai
```

```powershell
botnexus provider setup --provider anthropic
```

For Ollama, read its [provider guide](../providers/ollama.md) first. The model must be installed on the server separately. The wizard and advanced nested configuration are different setup paths; do not assume rerunning the wizard replaces every advanced setting.

GitHub Models and other compatible endpoints are not choices accepted by this setup wizard. Follow their linked guide rather than substituting their name into `provider setup`.

## Check the saved connection

Run:

```powershell
botnexus provider list
```

Check that the intended provider is listed. This checks saved configuration, not whether a request will succeed.

For Copilot, these additional commands contact the service and may refresh saved sign-in credentials:

```powershell
botnexus provider copilot whoami
botnexus provider copilot models
```

Check the account and available model IDs. Do not share raw diagnostic output without checking it for private information.

## Choose a model and try it

Use the [agent setup steps](../getting-started-release.md#5-create-your-first-agent) to select the configured provider and an available model. Copy the model ID exactly. Do not infer an agent's selection from the provider's default alone.

Then open the web interface, choose that agent, and send a harmless request such as:

> Explain the difference between a file and a folder in two sentences. Do not use tools.

You should receive an understandable answer without a provider error. This is an example request, not a recorded test result. It checks a basic response; it does not test file access, web search or every model feature. The request may use paid quota or local computing resources.

For future tasks, choose models based on the features you need and the access your account actually has. A model that can chat may not support tools, images or every reasoning option.

## Useful things to try

- Ask for a simpler version of a paragraph you wrote.
- Compare two short pieces of text you paste into the conversation.
- Ask for questions to help plan a project, without creating tasks or contacting anyone.

Start with non-sensitive material. See [Usage ideas](usage-ideas.md) for examples that use additional capabilities.

## If something goes wrong

| Symptom | What to check |
| --- | --- |
| `botnexus` is not recognized | Finish CLI installation and reopen the terminal. Use the installation guide rather than downloading an unknown executable. |
| Provider is missing | Check the operating-system account and selected BotNexus home, then run `provider list` again. |
| Sign-in fails | Check the provider account, access and credential instructions. Copilot and GitHub Models are different connections. |
| Model is unavailable | Check its exact ID and your account/server's available models. A built-in list is not an access guarantee. |
| Chat works but a task fails | Check the required tool or extension. Changing the model does not install tools or grant access. |
| A previous advanced setting still applies | Consult the provider reference for nested settings; do not repeatedly overwrite unrelated configuration. |

To stop using a provider, select a different configured provider/model for affected agents before removing its configuration. Do not remove a shared connection without checking which agents use it. The [CLI reference](../cli-reference.md) covers provider removal.

## Next steps

- [Use extensions](using-extensions.md) to give an agent additional capabilities.
- [Configuration](configuration.md) for settings and storage details.
- [Troubleshooting](troubleshooting.md) for broader failures.

For maintainers, the setup and listing commands are implemented by `ProviderCommand.Build` and `ExecuteSetupAsync`; Copilot diagnostics by `CopilotProviderSubcommand.Build`; home selection by `CliPaths.ResolveTarget` in `src/gateway/BotNexus.Cli/`. This guide was checked against those source paths, not through a live provider login.
