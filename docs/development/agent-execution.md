# Agent Execution Architecture

This page describes how BotNexus Gateway loads descriptors, creates session-bound
instances, executes turns, and manages tools, context, and shutdown. The examples
are source-aligned excerpts, not replacement interface definitions or a benchmark.

## Overview

| Layer | Responsibility |
| --- | --- |
| `IAgentConfigurationSource` / `IAgentRegistry` | Load and store agent descriptors |
| `IAgentSupervisor` | Create, reuse, inspect, and stop session-bound instances |
| `IIsolationStrategy` | Construct an execution environment and return a handle |
| `IAgentHandle` | Blocking prompts, streaming, and run-control operations |
| `BotNexus.Agent.Core.Agent` | Own agent state and message queues |
| `AgentLoopRunner` / `ToolExecutor` | Run model turns and execute requested tools |

## Agent Descriptor Loading

### AgentDescriptor Model

[`AgentDescriptor`](https://github.com/Sytone/botnexus/blob/main/src/domain/BotNexus.Domain/Gateway/Models/AgentDescriptor.cs)
is a sealed record containing static registration settings; runtime instance state
belongs to `AgentInstance`. Selected members (other descriptor fields omitted):

```csharp
public required AgentId AgentId { get; init; }
public required string DisplayName { get; init; }
public required string ModelId { get; init; }
public required string ApiProvider { get; init; }
public string IsolationStrategy { get; init; } = "in-process";
public int MaxConcurrentSessions { get; init; }
public IReadOnlyList<string> ToolIds { get; init; } = [];
public IReadOnlyList<string> SubAgentIds { get; init; } = [];
public string? SystemPrompt { get; init; }
public string? SystemPromptFile { get; init; }
public IReadOnlyList<string> SystemPromptFiles { get; init; } = [];
public HeartbeatAgentConfig? Heartbeat { get; init; }
public SoulAgentConfig? Soul { get; init; }
public FileAccessPolicy? FileAccess { get; init; }
public IReadOnlyDictionary<string, System.Text.Json.JsonElement> ExtensionConfig
    { get; init; } = new Dictionary<string, System.Text.Json.JsonElement>();
```

`ApiProvider` is the **provider instance key** used by the model registry, not an
API contract name. `Kind` defaults to `Named`; `SubAgent` is reserved for runtime
spawning. Thinking and context-window overrides are separate descriptor fields.
There is no per-descriptor `ToolExecutionMode` setting.

### Configuration Sources

[`PlatformConfigAgentSource`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Configuration/PlatformConfigAgentSource.cs)
projects the `PlatformConfig.Agents` dictionary into descriptors. The dictionary
key supplies the agent ID. This JSON shows the agent section's shape; replace the
illustrative provider and model strings with a pair registered in your deployment:

```json
{
  "agents": {
    "coding-agent": {
      "displayName": "Coding Assistant",
      "provider": "registered-provider-instance",
      "model": "registered-model-id",
      "isolationStrategy": "in-process",
      "maxConcurrentSessions": 1,
      "toolIds": ["read", "write", "edit", "shell", "grep", "glob"],
      "systemPromptFiles": ["AGENTS.md", "SOUL.md"]
    }
  }
}
```

This is not an array and is not a recipe for a standalone per-agent JSON source.
The source reads the options monitor's effective platform configuration; it does
not discover agent definitions by scanning individual agent files. See
[configuration](../configuration.md) for persistence and configuration entry points.

The current loader:

- Skips disabled agents, the reserved `defaults` entry, and reserved sub-agent
  archetype IDs.
- Maps `provider` / `model` to `ApiProvider` / `ModelId`, `toolIds` to `ToolIds`,
  `heartbeat` to `Heartbeat`, and `extensions` to the `ExtensionConfig` bag.
- Uses each agent block as authored: `agents.defaults` is **not merged** here.
- Validates each projected descriptor and logs/skips invalid entries; a corrected
  configuration can be retried on reload.

### Configuration Loading and Validation

[`AgentConfigurationHostedService`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Configuration/AgentConfigurationHostedService.cs)
loads every registered source, watches for changes, and debounces registry
synchronization. It retains each source's latest descriptor set. Code-registered
agents take precedence; earlier sources win duplicate IDs. The platform source
also suppresses notifications whose effective descriptors have not changed.

[`DefaultAgentRegistry.Register`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Agents/DefaultAgentRegistry.cs)
rejects duplicate IDs; it does **not** run descriptor validation itself. Do not
mistake storage in the registry for proof that a descriptor can execute.

[`AgentDescriptorValidator`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Configuration/Validation/AgentDescriptorValidator.cs)
checks required display/model/provider values, field-length bounds, nonnegative
session limits, and a nonempty isolation strategy. It checks strategy membership
when a nonempty available-strategy set is supplied. `ValidateForConfig` additionally
rejects `Kind = SubAgent`.

Thinking syntax is checked when supplied. Capability validation uses the selected
model only when a model registry and a matching model are available; an unknown
model is not rejected by that capability check. In-process creation performs the
model lookup and throws when the provider/model pair is unregistered. The
validator does not establish tool-name validity or absolute file-access paths;
tool resolution and path enforcement happen at their consuming boundaries.

## Agent Supervisor

### Instance Management

[`DefaultAgentSupervisor`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Agents/DefaultAgentSupervisor.cs)
keys instances by `AgentSessionKey.From(agentId, sessionId)`. Each pair gets a
handle and agent-loop state, created lazily on demand rather than eagerly for
every registered agent. Different sessions of the same agent still use that
agent's workspace, not independent filesystem sandboxes.

The supervisor exposes this creation signature:

```csharp
Task<IAgentHandle> GetOrCreateAsync(
    AgentId agentId,
    SessionId sessionId,
    CancellationToken cancellationToken = default);
```

### GetOrCreateAsync Flow

1. Resolve the registered descriptor and stored session, including permitted
   session model overrides.
2. Reuse a cached `Idle` or `Running` instance if its resolved descriptor matches.
   A changed descriptor removes the cached entry and starts disposing its handle.
3. Share an existing pending creation for the same key. Otherwise check the
   agent's concurrency limit and reserve a pending creation under the lock.
4. Validate the descriptor against registered strategies and pass stored history
   and execution parameters in `AgentExecutionContext` to `CreateAsync`.
5. Cache the returned handle with an `Idle` instance; clear the pending entry.
   Creation failures clear the pending entry and propagate to callers.

### Concurrency Control

| `MaxConcurrentSessions` | Actual supervisor behavior |
| --- | --- |
| `0` | No configured session-count limit |
| `1` | Admit at most one counted session; reject excess creation |
| Positive `N` | Admit up to `N` counted sessions; reject excess creation |

The count includes cached instances that are neither `Stopped` nor `Faulted`
(**including idle instances**) and pending creations, deduplicated by session ID.
Excess creation throws `AgentConcurrencyLimitExceededException`; it is not queued
for serialized execution. Waiting on creation of the **same** session is a
separate mechanism. Steering/follow-up queues and tool-batch parallelism below are
also separate from this session admission limit.

### Instance Status and Metadata

[`AgentInstance`](https://github.com/Sytone/botnexus/blob/main/src/domain/BotNexus.Domain/Gateway/Models/AgentInstance.cs)
is a sealed class with required `InstanceId`, `AgentId`, `SessionId`, and
`IsolationStrategy`, plus mutable `Status` and `LastActiveAt` and a `CreatedAt`
timestamp. Its status vocabulary is:

```csharp
public enum AgentInstanceStatus
{
    Starting,
    Idle,
    Running,
    Stopping,
    Stopped,
    Faulted
}
```

The class defaults to `Starting`; the supervisor publishes successfully created
entries as `Idle`. This enum is not a claim that every path emits every transition.
`StopAsync` removes the cached entry, marks it `Stopping`, disposes its handle,
and marks it `Stopped` on successful disposal. `StopAllAsync` clears cached and
pending entries, then disposes the captured handles, logging disposal failures.

## Isolation Strategies

### InProcessIsolationStrategy (Default)

[`InProcessIsolationStrategy.CreateAsync`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs)
runs the core agent in the Gateway process. It is not an OS security boundary.

Creation resolves effective model/thinking/context settings, passes those same
settings into prompt construction, obtains the workspace, and creates
workspace-scoped tools with a path validator. It then assembles registry tools,
optional memory tools, gateway tool providers, and extension contributions before
applying the conversation's narrowing-only tool override.

The strategy wires tool audit and hook delegates, projects resumable history
(including folding compacted summaries into the system prompt), creates the core
agent with `AgentOptions`, and wraps it in `InProcessAgentHandle`. Model lookup,
authentication, context, and tool setup can fail; no startup latency is promised.

### ContainerIsolationStrategy

**Not implemented.** [`CreateAsync`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/ContainerIsolationStrategy.cs)
throws `NotSupportedException`. Container execution, explicit mounts, resource
limits, and network restrictions are planned capabilities, not current guarantees.

### RemoteIsolationStrategy

**Not implemented.** [`CreateAsync`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/RemoteIsolationStrategy.cs)
throws `NotSupportedException`. Forwarding execution and streaming over a remote
transport is a planned boundary, not an available backend or scaling recipe.

### SandboxIsolationStrategy

**Not implemented.** [`CreateAsync`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/SandboxIsolationStrategy.cs)
throws `NotSupportedException`. A separate process with OS confinement is planned;
no sandbox implementation, confinement guarantee, or startup measurement is implied.

## Agent Handle

### IAgentHandle Interface

[`IAgentHandle`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentHandle.cs)
extends `IAsyncDisposable` and exposes `AgentId`, `SessionId`, and `IsRunning`.
The principal execution members are shown below; this is a partial excerpt:

```csharp
Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default);
Task<AgentResponse> PromptAsync(AgentUserMessage message, CancellationToken cancellationToken = default);
IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default);
IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentUserMessage message, CancellationToken cancellationToken = default);
Task AbortAsync(CancellationToken cancellationToken = default);
Task SteerAsync(string message, CancellationToken cancellationToken = default);
Task FollowUpAsync(string message, CancellationToken cancellationToken = default);
Task FollowUpAsync(AgentTranscriptMessage message, CancellationToken cancellationToken = default);
```

Other control members are significant, not optional substitutes for those calls:

| Member | Contract |
| --- | --- |
| `ObserveTurns(Action)` | Returns a disposable turn subscription, or `null` when the handle cannot expose turns; `null` does not mean zero turns consumed |
| `SteerAsync(AgentUserMessage, ...)` | Multimodal steering; the in-process handle preserves the typed message |
| `SteerDeferrableAsync(string, ...)` | Queues a side turn that the in-process loop defers while busy |
| `TryFollowUpWhileRunningAsync(string/AgentUserMessage, ...)` | Returns `true` if queued for the active run; `false` requires normal inbound delivery instead |
| `InterruptAndSteerAsync(string/AgentUserMessage, ...)` | Redirects execution through the handle's abort-and-steer control path |

The interface's default multimodal steer/redirect implementations degrade to
text through an image-drop guard; implementations with typed queues override
them. Its default conditional follow-up implementation returns `false`.
In-process conditional follow-up uses enqueue/reverify/reclaim rather than a racy
`IsRunning` check followed by enqueue. Queue overflow is an exception, not a silent
drop. `ContinueAsync`, `GetHistoryAsync`, and `GetHealthAsync` are not members of
this Gateway handle contract; history and instance inspection use their own services.

### InProcessAgentHandle and Event Conversion

`PromptAsync` calls the core prompt API and builds an `AgentResponse` from returned
messages. It does not itself broadcast through an `IChannelAdapter`.
`StreamAsync` uses `StreamCoreAsync`, subscribes to core events, maps them into an
async channel, and yields Gateway events to its caller. Downstream delivery is
separate from the handle's mapping.

The actual `InProcessAgentHandle.MapAgentEvent` mapping in the
[in-process implementation](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs)
is summarized here:

| Core event | Gateway event / payload |
| --- | --- |
| `AgentStartEvent` / `AgentEndEvent` | `RunStarted` / `RunEnded`, bracketing the whole run |
| Assistant `MessageStartEvent` | `MessageStart` |
| `MessageUpdateEvent` with non-null delta and `IsThinking == false` | `ContentDelta` with `ContentDelta` payload |
| `MessageUpdateEvent` with non-null delta and `IsThinking == true` | `ThinkingDelta` with `ThinkingContent` payload |
| `ToolExecutionStartEvent` | `ToolStart` with call ID, name, and arguments |
| `ToolExecutionEndEvent` | `ToolEnd` with call ID, name, extracted result text, and error flag |
| Assistant `MessageEndEvent` | `MessageEnd` with reconciled `FinalContent` and optional usage |
| `ToolExecutionUpdateEvent` carrying `AskUserRequest` | `UserInputRequired` |
| `TurnEndEvent` | `TurnEnd` |
| `ClaimAuditEvent` | `ClaimAudit` with its blocking decision and unbacked claims |
| Other events | No mapped event (`null`) |

Mapped events carry the supplied `MessageId`. Run boundaries must not be inferred
from individual message/tool endings: a run can continue through multiple model
turns and follow-ups. Error propagation and deliberate teardown classification
also live in the stream pipeline; there is no generic core `ErrorEvent` arm in
this mapper.

## Agent Loop Runner

### Core Loop (AgentCore)

[`AgentLoopRunner`](https://github.com/Sytone/botnexus/blob/main/src/agent/BotNexus.Agent.Core/Loop/AgentLoopRunner.cs)
coordinates the model/tool cycle. In outline, not exhaustive retry/error pseudocode:

1. Check optional compaction at an outer-loop boundary and obtain queued input.
2. Incorporate eligible steering messages, build/transform model context, and
   obtain the streamed assistant response.
3. Execute requested tools only when the assistant finishes with `ToolUse` and
   carries tool calls, then append their results. Partial calls on other finish
   reasons are not dispatched.
4. Emit turn completion and inspect steering again. Deferrable side turns wait
   until there are no outstanding tool calls or other pending messages.
5. Continue model turns while work remains. At the idle boundary, queued
   follow-ups can seed another outer iteration; otherwise the run ends.

The Gateway configures `SteeringMode` and `FollowUpMode` as `QueueMode.All`.
These are input-drain choices, not session admission or parallel-tool controls.

### Tool Execution

[`ToolExecutor`](https://github.com/Sytone/botnexus/blob/main/src/agent/BotNexus.Agent.Core/Loop/ToolExecutor.cs)
supports sequential execution and parallel execution. The in-process Gateway
explicitly selects `ToolExecutionMode.Parallel` in `AgentOptions`; it does not
read a descriptor switch for this.

Parallel mode prepares calls in source order, runs prepared calls with
`Task.WhenAll`, then processes their outcomes in source order. Calls rejected
during preparation can emit an immediate end event before later start events;
do not assume all event starts precede all ends. Result ordering does not make
side effects sequential or make dependent tool calls safe to run concurrently.
This batch-level concurrency is independent of `MaxConcurrentSessions`.

## Tool Registry

### Available Tools

The final tool set is assembled, not guaranteed by a static list on this page:

- Workspace tools come from `IAgentToolFactory`.
- Registered extension tools come from the tool registry. Workspace tools win
  same-name collisions with those registry tools.
- Memory enablement, gateway provider dependencies, and extension contributions
  can add tools. Session tool overrides narrow the assembled set last.
- An empty `ToolIds` list or the single wildcard `["*"]` means unrestricted
  selection at this boundary, **not no tools**. Explicit IDs select workspace
  and registry tools; individual provider/contributor gates also matter.

Consult the actual exposed schema for names, arguments, and availability of
session, conversation, sub-agent, scheduling, skill, memory, or external tools.
A delay inside an executing call and a durable scheduled job are distinct concepts;
the tool list is not a promise of either one's scheduling semantics.

### Tool Factory

Current [`IAgentToolFactory`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentToolFactory.cs)
member:

```csharp
IReadOnlyList<IAgentTool> CreateTools(
    WorkingDir workingDirectory,
    IPathValidator? pathValidator = null,
    string[]? shellCommand = null);
```

[`DefaultAgentToolFactory`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Agents/DefaultAgentToolFactory.cs)
creates read, write, edit, shell, directory-listing, grep, glob, and tool-output
continuation tools. It resolves `WorkingDir` to a full path; the value object
itself does not promise an absolute path. File tools receive the effective path
validator. Shell-command selection prefers the per-agent override, then the
factory's gateway command, then preference-based detection. `ShellTool` is not
passed `IPathValidator`; file-tool path checks are not shell confinement.

## Workspace and Context

### Workspace Management

Selected [`IAgentWorkspaceManager`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentWorkspaceManager.cs)
members (memory overloads omitted):

```csharp
Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default);
Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default);
string GetWorkspacePath(string agentName);
bool TryCleanupWorkspace(string agentName) => false;
```

[`FileAgentWorkspaceManager`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Agents/FileAgentWorkspaceManager.cs)
resolves named-agent workspaces beneath the configured BotNexus home as
`agents/{agentId}/workspace`. It does not use `workspaces/{agentId}`, and
`FileAccessPolicy` does not select the workspace directory.

`LoadWorkspaceAsync` reads the soul, identity, user, and memory files, returning
empty content for missing files. `SaveMemoryAsync` appends beneath the memory root
and creates required directories; its overloads support a target file and a
workspace-relative memory-root override. There is no `EnsureWorkspaceExistsAsync`
member. Runtime sub-agent workspace names use a separate temporary/configured
root; cleanup is restricted to those temporary workspaces, not named agents.

### System Prompt Building

Current [`IContextBuilder`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Agents/IContextBuilder.cs)
primary member:

```csharp
Task<string> BuildSystemPromptAsync(
    AgentDescriptor descriptor,
    AgentExecutionContext? executionContext,
    EffectiveExecutionSettings? effectiveSettings = null,
    CancellationToken cancellationToken = default);
```

A second overload accepts `ConversationScope` before the cancellation token.
Pass the already-resolved execution settings when a run exists so the runtime
prompt describes the same model/thinking/context selection as execution.

[`WorkspaceContextBuilder`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Agents/WorkspaceContextBuilder.cs)
resolves workspace and conversation context, chooses prompt-file variants using
the effective model/provider, loads configured instruction files and world
instructions, and incorporates eligible memory/context-hook contributions.
It filters owner-private context files for shared conversations before calling
`SystemPromptBuilder.Build(SystemPromptParams)`, then merges prompt-hook results.
The resulting prompt includes workspace context, runtime and conversation data,
heartbeat configuration, and configured prompt contributors. This is prompt
composition, not a filesystem sandbox or a substitute for tool policy.

## Security and Validation

### Path Validation

The actual [`IPathValidator`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Security/IPathValidator.cs)
contract is:

```csharp
bool CanRead(string absolutePath);
bool CanWrite(string absolutePath);
string? ValidateAndResolve(string rawPath, FileAccessMode mode);
```

[`DefaultPathValidator`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Security/DefaultPathValidator.cs)
uses workspace-relative resolution and separate read/write allowlists plus denies.
A null or empty policy is workspace-only. `ValidateAndResolve` checks lexical
resolution and then a link-aware target walk, returning `null` when access is
refused. Do not replace this operation with an invented `IsPathAllowed` API or
claim descriptor validation alone proves filesystem containment.

### Tool Policy

[`IToolPolicyProvider`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Contracts/Security/ToolPolicy.cs)
provides `GetRiskLevel`, `RequiresApproval`, `GetApprovalFallback`,
`GetDeniedForHttp`, and `IsToolAvailable`. It is not an asynchronous
`IToolPolicy.EvaluateAsync(arguments)` interface.

[`DefaultToolPolicyProvider`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Security/DefaultToolPolicyProvider.cs)
supplies policy to consumers, while
[`ToolPolicyHookHandler`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Hooks/ToolPolicyHookHandler.cs)
denies blocked tools and handles approval-required calls. At this hook seam there
is no interactive approval workflow: the configured fallback is `Allow` by default
or opt-in `Deny`, with an audit decision. Do not interpret `RequiresApproval` alone
as evidence that a human approved execution. This tool-level policy is distinct
from file-path checks and any tool-specific command validation.

### Hook Dispatcher

[`HookDispatcher`](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Hooks/HookDispatcher.cs)
registers typed handlers and invokes a snapshot in ascending priority order,
collecting non-null results. It does not short-circuit on a `HookAction.Block`.

The in-process before-tool delegate durably records the invocation first, dispatches
Gateway before-tool hooks, and converts a returned `Denied` result into the core
blocking result. The core executor can apply after-tool replacements, but the
current Gateway after-tool delegate dispatches notifications and returns `null`;
it does not apply returned Gateway result rewrites. Keep the dispatcher contract,
core executor capabilities, and this concrete adapter's behavior distinct.

## Performance Characteristics

This page makes **no measured startup, throughput, latency, or memory claims**.
Source inspection cannot establish a benchmark, and the unimplemented strategies
cannot supply one.

For a reproducible performance report, publish the source revision, environment
(OS/runtime/hardware), provider/model, input and tool workload, settings, sample
count, warm-up policy, and measurement harness/command. Separate cold creation
from cached-handle lookup, queue/admission wait from execution, and provider
first-token latency from total turn time. Define token and memory counters, record
failures/cancellations, and report distributions rather than unexplained single
numbers. Run such measurements in an isolated, explicitly authorized environment;
a docs build checks rendering and links, not runtime speed or isolation security.

## Summary

- Instances and agent-loop state are session-bound; workspaces are agent-bound.
- Descriptors are loaded and validated at explicit boundaries, not by mere registry
  insertion.
- Session admission, steering/follow-up queues, and parallel tool batches are
  different controls.
- Handles separate blocking responses, streams, and run control; event consumers
  must distinguish run, message, tool, and turn boundaries.
- Context, file access, tool policy, and isolation are separate mechanisms.
- Container, remote, and sandbox execution remain future work. Instance pooling
  and further policy/execution optimizations require their own implementations and
  evidence; they are not capabilities established by this guide.
