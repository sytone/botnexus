# Use extensions

An **extension** adds a capability to BotNexus. You do not need to write its code to use an extension that is installed and configured. Examples include reading a web page, connecting to a messaging service, or searching a document collection.

A **tool** is an action an agent can call. A **skill** is a set of instructions and supporting files that teaches the agent how to do a task. An extension may supply tools, load skills, or connect another service. These are related, but they are not interchangeable.

## Choose a capability

| I want to... | Start with | Before using it |
| --- | --- | --- |
| Read a public page or search the web | [Web Tools](../extensions/web-tools.md) | Network access; search may need separate credentials |
| Give an agent a repeatable procedure | [Skills](../skills.md) | Reviewed skill files and any tools the procedure requires |
| Connect an existing tool server | [MCP](../extensions/mcp.md) | An installed, trusted server and its access settings |
| Search a document collection | [QMD](../extensions/qmd.md) | QMD setup and permission to read the collection |
| Keep structured records | [Data Store](../extensions/data-store.md) | A configured store and clear ownership of its data |
| Work with GitHub | [GitHub extension](../extensions/github.md) | A credential with only the permissions needed |
| Use a browser or run a command | [Browser Tools](../extensions/browser-tools.md) or [Exec](../extensions/exec-tool.md) | Stronger review: these actions can affect websites or your computer |

**MCP** means Model Context Protocol. It is a way for BotNexus to connect to a separate program that provides tools. A skill that mentions a service does not itself install that service or grant access to it.

## Before you start

- Finish [installation](../getting-started-release.md) and confirm that your agent can answer a simple message.
- Read the specific extension's requirements. Some are bundled with the platform; others depend on software or accounts you must prepare separately.
- Use a test conversation and non-sensitive information for the first attempt.
- Check the settings for the intended agent, not another installation or operating-system account.
- Understand what the tool can read or change. Do not treat every extension as read-only.

Three things must agree: the extension is loaded, the agent has the required configuration, and the requested tool is available in that conversation. A file on disk or an extension name in a list does not prove all three.

## Set up one extension at a time

1. Choose the capability in the table above and read its owning guide.
2. Check that the extension is installed through your installation's supported setup path. For bundled code, use the platform installation/build process. Do not invent a generic `extension install` command or download an unknown package merely because a prompt suggests it.
3. Follow that guide's per-agent configuration instructions. Keep the agent's provider, model and other settings. Do not replace the whole configuration file with a small sample.
4. Use the [configuration guide](configuration.md) to inspect the saved settings. Prefer supported CLI or editor controls. Some extension-owned settings are not available through `config set`; if a path is rejected, do not assume a successful-looking alternative will be read.
5. Start a new test interaction and ask which tools are available for the chosen task. Then try the small example from the extension guide and inspect its actual tool result.

Extensions do not share one universal per-agent `enabled` setting. Use the switch or setup path documented for that extension. Changes to installed code may require a gateway restart; do not interrupt active work without planning for it.

## First example: read a public page

**Requirements:** the Web Tools extension must be loaded, and the agent must have its `botnexus-web` configuration. Web search is separate from reading a known URL. The latter does not require a search-provider key.

The following is an **agent configuration fragment**, not a complete file or a command. It illustrates a fetch-only configuration. Use the [Web Tools setup reference](../extensions/web-tools.md#configuration) and your supported configuration editor to apply it to the intended agent while preserving other settings. If your editor cannot author this extension subtree, stop and ask the installation administrator for the supported setup path.

```json
{
  "extensions": {
    "botnexus-web": {
      "fetch": {
        "maxLengthChars": 5000,
        "timeoutSeconds": 30
      }
    }
  }
}
```

This example limits returned text to 5,000 characters and the configured request time to 30 seconds. Private-network access stays disabled by default. These settings limit this web tool; they do not make every tool available to the agent safe or read-only.

Choose a public documentation page with no sign-in requirement. Ask:

> Read this public page using web_fetch and summarize three useful points: [paste the page URL]. Do not sign in, submit forms, download programs or change anything. Tell me if you could not read the page.

This is a suggested request, not a recorded result. Check that a successful `web_fetch` result names the intended page and that the summary matches it. If only part of the page was returned, ask the agent to say so. The request contacts the website and the model response may use paid quota.

## More ideas

- Ask the agent to list available skills before choosing one for a writing task.
- Connect a reviewed MCP server for a specific task instead of granting broad computer access.
- Use a test collection before asking a document-search tool to read sensitive material.
- Keep browser and shell actions separate from a first read-only experiment.

See [Usage ideas](usage-ideas.md) for sample requests and result checks.

## If a tool is missing or fails

| Symptom | Safe next check |
| --- | --- |
| Agent cannot find the tool | Check extension loading, the selected agent's configuration, and conversation tool restrictions. A different model does not install the tool. |
| Reading a URL works but search does not | Check the separate search provider and credentials. Fetch-only setup does not enable search. |
| Access is denied | Check the intended permission. Do not remove access controls to make a test pass. |
| A page needs sign-in or is blocked | Use a public page for the first test. Do not paste session cookies or tokens into the conversation. |
| A skill loads but its procedure fails | Check the software, tool and account prerequisites named by the skill. |
| A configuration key is rejected | Check the exact extension ID and supported configuration surface. Do not keep trying guessed names. |

To stop a running task, ask the agent to stop and verify its outcome. Disabling a capability is a separate configuration action: use its documented controls and check the affected agents. Do not delete an extension's files while it is in use.

## For developers

If you want to write a new extension, read [Extension development](../extension-development.md). The older [custom tool and skill authoring reference](extensions.md) remains available for technical readers; it is not the beginner setup route.

The fetch example is source-checked against `WebToolsContributor.ContributeAsync`, `WebToolsConfig` and the Web Tools manifest under `src/extensions/BotNexus.Extensions.WebTools/`. The contributor reads `botnexus-web`, adds fetch when its configuration exists, and adds search separately when its requirements are met. The example was not executed against a live installation.
