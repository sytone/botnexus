# Extension Guide

Choose the extension seam that owns the behavior you need. This guide covers tools,
channels, providers, hooks, MCP servers and prompt contributions. It describes the
contracts in the repository, not a complete deployable extension template.

> **Migration note:** Earlier versions of this page showed obsolete interface members
> and configuration shapes. The replacements below supersede those examples; do not
> copy the former `InputSchema`, `StreamAsync`, `BeforeAsync`/`AfterAsync`, root
> `mcpServers`, channel-array or per-agent `apiProvider`/`modelId` recipes.

## Adding a Tool

Implement [`IAgentTool`](../../src/agent/BotNexus.Agent.Core/Tools/IAgentTool.cs) in
`BotNexus.Agent.Core.Tools`. Its required members are:

| Member | Authoring contract |
|---|---|
| `Name` | Tool-call routing name; match `Definition.Name`. |
| `Label` | Human-readable diagnostic label. |
| `Definition` | A `Tool(Name, Description, Parameters)` record; `Parameters` is a JSON Schema `JsonElement`, not an `InputSchema` property. |
| `PrepareArgumentsAsync(arguments, cancellationToken)` | Validate, coerce or enrich arguments before execution. Throw on invalid input. |
| `ExecuteAsync(toolCallId, arguments, cancellationToken, onUpdate)` | Return `Task<AgentToolResult>`; the optional `AgentToolUpdateCallback` carries partial progress. |

A minimal tool implementation, with no external service dependency:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

public sealed class EchoTool : IAgentTool
{
    public string Name => "echo_text";
    public string Label => "Echo Text";

    public Tool Definition { get; } = new(
        "echo_text",
        "Return the supplied text",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" }
        }));

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        arguments.TryGetValue("text", out var value);
        var text = value switch
        {
            string supplied => supplied,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        if (text is null)
            throw new ArgumentException("text must be a string");
        IReadOnlyDictionary<string, object?> prepared =
            new Dictionary<string, object?> { ["text"] = text };
        return Task.FromResult(prepared);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = (string)arguments["text"]!;
        return Task.FromResult(new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, text)]));
    }
}
```

Throw for execution failures too: the agent loop converts failures into error tool
results. Do not encode failure by inventing a success flag on `AgentToolResult`.
Keep tools thread-safe for parallel execution. Optional interface defaults include
`DefaultTimeout`, `TimeoutArgument`, `ContentSource`, `GetPromptSnippet()` and
`GetPromptGuidelines()`. `ContentSource` defaults to `Unknown`, which taints; classify
returned bytes by origin rather than treating an unclassified tool as trusted.

### Registration and agent selection

For asynchronous per-handle construction or resources disposed with the handle,
implement [`IAgentToolContributor`](../../src/gateway/BotNexus.Gateway.Abstractions/Agents/IAgentToolContributor.cs).
`ContributeAsync(AgentToolContributionContext, CancellationToken)` returns an
`AgentToolContribution` containing tools and optional `ResourcesToDispose`.
The context supplies the descriptor, execution context, workspace and path validator.
The gateway also has a per-agent built-in `IAgentToolFactory` path and a global
`IToolRegistry` path; they are not interchangeable with asynchronous contribution.

Make the implementation available to host composition through the extension loader.
Use [`IServiceContributor`](../../src/gateway/BotNexus.Gateway.Abstractions/Extensions/IServiceContributor.cs)
only for registrations that contract auto-discovery does not cover. Its
`ConfigureServices(IServiceCollection)` runs before host construction, and the loader
requires a public parameterless contributor constructor. See the
[Extension Development Guide](../extension-development.md) for packaging and discovery.

In platform configuration, select a registered tool under `agents.<id>.toolIds`.
The following is a **fragment of an existing agent definition**, not a complete
standalone platform configuration:

```json
{
  "toolIds": ["echo_text"]
}
```

A name in `toolIds` does not register or load an implementation.

## Adding a Channel Adapter

Implement [`IChannelAdapter`](../../src/gateway/BotNexus.Gateway.Contracts/Channels/IChannelAdapter.cs),
whose namespace is `BotNexus.Gateway.Abstractions.Channels` despite the source project's
`Contracts` directory name. Implement all required members, not only streaming support:

| Members | Purpose |
|---|---|
| `ChannelType`, `DisplayName`, `IsRunning` | Typed channel identity and runtime state. |
| `SupportsStreaming`, `SupportsSteering`, `SupportsFollowUp` | Delivery and interaction capabilities. |
| `SupportsThinkingDisplay`, `SupportsToolDisplay`, `SupportsInboundImages` | Content and display capabilities. |
| `StartAsync(dispatcher, cancellationToken)`, `StopAsync(cancellationToken)` | Connect and disconnect under host lifecycle control. |
| `SendAsync(message, cancellationToken)` | Deliver a complete `OutboundMessage`. |
| `SendStreamDeltaAsync(target, delta, cancellationToken)` | Deliver a delta using the typed `ChannelStreamTarget`. |

`AdapterId` defaults to null; `SupportsInteractivePrompts` defaults to false. Lack of
interactive-prompt rendering does not disable `ask_user`: the gateway can render a
numbered text prompt. Structured stream events use the optional
`IStreamEventChannelAdapter` contract.

At the inbound boundary populate the typed sender as well as the wire audit identity,
then await `IChannelDispatcher.DispatchAsync`. Construct identifiers using their
factories, for example `ChannelKey.From("custom-chat")`. `ChannelKey` currently also
has a public constructor, but that does not establish a constructor contract for
other identifier types. Avoid unobserved `async void` message handlers; own the callback
lifetime and cancellation according to the external client's contract.

Register the adapter through host DI and the extension-loading path. Platform
`channels` is a dictionary keyed by channel id, **not an array or `channels.instances`**.
[`ChannelConfig`](../../src/gateway/BotNexus.Gateway.Configuration/PlatformConfig.cs)
models `type`, `enabled` and `settings`, preserving other adapter-owned properties
through `JsonExtensionData`. Each adapter must actually bind the location its guide
shows; unknown-property preservation is not binding.

A hypothetical adapter might consume `channels.customChat.settings`; that is an
implementation choice, not a shipped Slack or Discord configuration contract. Use the
existing [Telegram](../user-guide/channels/telegram.md),
[Service Bus](../user-guide/channels/service-bus.md) and
[SignalR](../user-guide/channels/signalr.md) pages for shipped adapters.

## Adding a Provider

The [`IApiProvider`](../../src/agent/BotNexus.Agent.Providers.Core/Registry/IApiProvider.cs)
contract belongs to `BotNexus.Agent.Providers.Core.Registry` and represents an API
format, not merely a display name:

| Member | Contract |
|---|---|
| `Api` | Format identifier used for registry lookup and model/API matching. |
| `Stream(model, context, options)` | Return `LlmStream`; options are `StreamOptions?`. |
| `StreamSimple(model, context, options)` | Return `LlmStream`; options are `SimpleStreamOptions?`. |
| `Capabilities` | Optional behavioral declaration, defaulting to `ProviderCapabilities.Default`. |

There is no `SupportedTransports` member or `Task<LlmStream> StreamAsync` member on this
interface. Network work must be owned by the returned stream's implementation rather
than exposing an unobserved task or accessing a public channel writer.
[`LlmStream`](../../src/agent/BotNexus.Agent.Providers.Core/Streaming/LlmStream.cs) exposes
`Push(event)`, `End(finalMessage)` and explicit `EndWithoutResult` paths; terminal
`DoneEvent`/`ErrorEvent` also settle its result. A `WarningEvent` is non-terminal.
Propagate the turn's cancellation and never leave `GetResultAsync` waiting indefinitely.

[`ApiProviderRegistry.Register(provider, ...)`](../../src/agent/BotNexus.Agent.Providers.Core/Registry/ApiProviderRegistry.cs)
is an **instance** method; registration keys on `provider.Api` and guards mismatched
`model.Api`. [`ModelRegistry.Register(provider, model)`](../../src/agent/BotNexus.Agent.Providers.Core/Registry/ModelRegistry.cs)
is also an instance method. These are composition calls, not automatic consequences of
declaring a DI service.

[`LlmModel`](../../src/agent/BotNexus.Agent.Providers.Core/Models/LlmModel.cs) carries
`Id`, `Name`, `Api`, `Provider`, `BaseUrl`, `Reasoning`, `Input`, `Cost`, `ContextWindow`
and `MaxTokens`, with optional capability/header/compatibility values. `ModelCost`
records input, output, cache-read and cache-write costs per million tokens. Do not reuse
the former `DisplayName`, `ApiProvider`, `MaxOutput`, `InputPricing` or `OutputPricing`
initializer from this guide, or assume a sample model's price and limits are current.

After registering a real provider/model pair, select it with
`agents.<id>.provider` and `agents.<id>.model`; `apiProvider` and `modelId` are not those
platform configuration keys. Test happy-path streaming, tool assembly, cancellation,
errors and incomplete-result handling against the actual event contract. Consult the
[normalized-event audit](../development/normalized-llm-event-audit.md) for the distinction
between shared contracts and provider-specific gaps.

## Adding a Hook

Gateway hooks use
[`IHookHandler<TEvent, TResult>`](../../src/gateway/BotNexus.Gateway.Contracts/Hooks/IHookHandler.cs)
in `BotNexus.Gateway.Abstractions.Hooks`. Implement `Priority` (lower first) and
`Task<TResult?> HandleAsync(TEvent hookEvent, CancellationToken ct = default)`.
Register the appropriate **closed generic** service in host DI; the old nongeneric
`IHookHandler`, `BeforeAsync` and `AfterAsync` recipe is not this contract.

The [tool hook events and results](../../src/gateway/BotNexus.Gateway.Contracts/Hooks/HookEvents.cs)
distinguish two boundaries:

- Before execution, `BeforeToolCallEvent` carries agent id, tool name, call id and
  arguments. `BeforeToolCallResult` can set `Denied`/`DenyReason` or
  `ModifiedArguments`; returning null makes no contribution.
- After execution, `AfterToolCallEvent` carries result text and `IsError`.
  The gateway `AfterToolCallResult` is a marker: it does **not** transform output.
  Agent-core after-tool results are a different contract; do not transfer their
  override capability to gateway hooks.

For example, a gateway policy returns
`new BeforeToolCallResult { Denied = true, DenyReason = "Reason" }`, not a fictional
`Block` factory. Avoid logging raw tool arguments or results containing secrets.
Keep handlers short and cancellation-aware because callers await them.

## Adding an MCP Server

Use the shipped [MCP extension](../extensions/mcp.md) for bridging, or
[MCP Invoke](../extensions/mcp-invoke.md) for on-demand calls. External servers are
separate software, not built-in BotNexus servers. Install and trust the server you
intend to run; this guide does not endorse an unverified package or automatic download.

The following is a **per-agent fragment** under `agents.<id>` for an already-installed
stdio server; replace its command and path with your actual installation:

```json
{
  "extensions": {
    "botnexus-mcp": {
      "toolPrefix": true,
      "servers": {
        "local-files": {
          "command": "your-mcp-server",
          "args": ["/path/to/allowed/files"],
          "inheritEnv": false,
          "initTimeoutMs": 30000,
          "callTimeoutMs": 60000
        }
      }
    }
  }
}
```

[`McpExtensionConfig`](../../src/extensions/BotNexus.Extensions.Mcp/McpExtensionConfig.cs)
uses a server-id dictionary and `toolPrefix`; server options include stdio command,
arguments, environment/working directory or HTTP URL/headers/auth.
[`McpToolContributor`](../../src/extensions/BotNexus.Extensions.Mcp/McpToolContributor.cs)
resolves the `botnexus-mcp` block from the descriptor. Non-auth servers use the warmup
cache and contribute tools when ready; HTTP servers with provider-auth references are
started per handle with resolved credentials and owned disposal resources. Configuration
alone does not guarantee a ready tool set, and this contract is not a blanket
process-restart guarantee.

Keep `inheritEnv: false` when the child should receive only explicitly supplied
variables. Credentialed remote HTTP servers require HTTPS, with a loopback HTTP
exception; explicit Authorization headers take precedence over provider-auth injection.
See the MCP page for failure, timeout and transport details.

## Adding a Prompt Section

[`IPromptSection`](../../src/gateway/BotNexus.Gateway.Prompts/IPromptSection.cs) belongs to
`BotNexus.Gateway.Prompts`. It exposes `Order`, `ShouldInclude(context)` and
`Build(context)`, plus optional `SectionId` and `XmlTag`. This is the seam for a
composer that owns a `PromptPipeline`; constructing a separate pipeline does not
register anything into the gateway's system prompt.

For a dynamically loaded extension, implement
[`IPromptContributor`](../../src/gateway/BotNexus.Gateway.Prompts/IPromptContributor.cs)
and register it in host DI. The current production collection path is
`WorkspaceContextBuilder` -> `SystemPromptParams.PromptContributors` ->
`PromptPipeline.AddContributors`.

| Member | Meaning |
|---|---|
| `Target` | `PromptSection?`; only null targets render as standalone blocks today. |
| `Priority` | Sort key alongside the composer's section keys, not a fixed 100-600 range. |
| `ShouldInclude(context)` | Decide whether the contributor participates. |
| `GetContribution(context)` | Return `PromptContribution` with `Lines`, optional `SectionHeading` and optional overriding `Order`. |

The [pipeline](../../src/gateway/BotNexus.Gateway.Prompts/PromptPipeline.cs) orders included
sections/contributions and emits their lines. Do not claim that a non-null target is
rendered or that DI resolves arbitrary sections into a pipeline automatically.
See [Prompt Pipeline](../development/prompt-pipeline.md) for composition context.

## Summary

| Extension point | Contract | Further reading |
|---|---|---|
| Tool | `IAgentTool`, `IAgentToolContributor` | [Extension Development](../extension-development.md) |
| Channel | `IChannelAdapter`, optional `IStreamEventChannelAdapter` | [Channel Binding](channel-binding.md) |
| Provider | `IApiProvider`, API/model registries | [Providers](../providers/openai-compatible.md) |
| Hook | `IHookHandler<TEvent, TResult>` | [Agent Execution](../development/agent-execution.md) |
| MCP server | External server plus `botnexus-mcp` configuration | [MCP](../extensions/mcp.md) |
| Prompt | `IPromptSection` for composers; `IPromptContributor` for extensions | [Prompt Pipeline](../development/prompt-pipeline.md) |
| Session store | `ISessionStore` | [Session Stores](../development/session-stores.md) |
| Execution isolation | `IIsolationStrategy` | [Architecture Overview](overview.md) |

A custom adapter/provider example is not a claim that the integration ships with
BotNexus. Validate extension code through the repository's required test workflow;
this source-aligned guide is not evidence that a deployable sample was compiled or
loaded. See [Developer Setup](../getting-started-dev.md) for validation guidance.
