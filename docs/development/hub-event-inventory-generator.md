# Hub event inventory generator

**Status:** shipped for [#3318](https://github.com/Sytone/botnexus/issues/3318) — candidate 1 of the
[source generator survey](./source-generator-survey.md).

## What it removes

`IGatewayHubClient` is the single declaration of the SignalR server→client contract, but before this
generator it was restated by hand in three places. Two of those — the `AllHubEvents` arrays in both
integration harnesses — carried **13 of the interface's 24 events**, so eleven declared events were
unobservable to those suites.

That gap failed silently by construction. A harness cannot receive an event it never subscribed to,
so a regression in `RunStarted`, `RunEnded`, `TurnEnd`, `TurnInterrupted`, `UserInputRequired`,
`AgentsChanged`, `ConversationChanged`, `SteeringFeedback`, `CanvasUpdated`, `CanvasStateChanged` or
`TodoUpdated` presented as *nothing was received* — indistinguishable from a quiet but passing run.

## How it works

`HubEventInventorySourceGenerator` is a Roslyn incremental generator in
`tools/BotNexus.SourceGenerators/`, following the same shape as the feature-flag generator (#2769)
and the tool-schema generator (#3320).

1. It injects a marker attribute, `BotNexus.SourceGenerators.Generated.HubEventInventoryAttribute`,
   via post-initialization. The attribute exists only during compilation, so the annotated project
   takes no runtime dependency on the generator.
2. `IGatewayHubClient` carries `[HubEventInventory]`.
3. The generator projects the interface's **ordinary methods** (property and event accessors are
   excluded — they are not hub methods, and subscribing to them would register handlers the server
   never invokes) into a static container in the interface's own namespace:

```csharp
public static class HubEvents
{
    public static readonly string[] All = [ "Connected", "SessionReset", /* … */ "RunEnded" ];
}
```

Both `TestSignalRClient` harnesses assign `AllHubEvents = HubEvents.All`. Adding a method to
`IGatewayHubClient` therefore makes the new event observable to both suites with no second edit.

## Wiring

The generator is referenced as an **analyzer**, not as an assembly:

```xml
<ProjectReference Include="..\..\..\tools\BotNexus.SourceGenerators\BotNexus.SourceGenerators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

`ReferenceOutputAssembly="false"` is what keeps the netstandard2.0 analyzer out of the net10.0
reference closure. The consuming test projects take an ordinary `ProjectReference` on
`BotNexus.Extensions.Channels.SignalR` to see the generated `HubEvents`.

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| `BNHE001` | Error | An interface marked `[HubEventInventory]` declares no methods. |

An empty inventory is a build error rather than silence, following the #2769 precedent: it would
make every consumer subscribe to nothing, which is precisely the "nothing was received" failure the
generator exists to remove.

## What is still hand-written - and why it stays that way

The Blazor client's `On<T>(...)` registration block in `GatewayHubConnection.cs` is **not** generated,
and [#3430](https://github.com/Sytone/botnexus/issues/3430) measured that it cannot be. Three
independent blockers, any one of which is decisive:

1. **The dependency edge runs the wrong way, deliberately.** The block lives in
   `BlazorClient.Core`, whose only permitted project reference is `BotNexus.Domain.Wire` - enforced
   by `WasmPayloadDependencyArchitectureTests`, because every assembly reachable from a WASM entry
   point is downloaded by the browser ([#2329](https://github.com/Sytone/botnexus/issues/2329)).
   `IGatewayHubClient` sits in an assembly that transitively drags `BotNexus.Domain` and Vogen. A
   generator needs the annotated interface's symbol in the *consuming* compilation, so generating
   the block means breaching that payload fence to delete 24 lines.
2. **The payload types are not the interface's payload types.** The client registers its own mirror
   records from `Services/HubContracts.cs`. Most sharply, `IGatewayHubClient.ContentDelta` declares
   `object` while the client registers `AgentStreamEvent` - emitting the interface's type verbatim
   would change what the portal deserialises.
3. **The delegate bodies are not uniform.** `CanvasUpdated`, `CanvasStateChanged` and `TodoUpdated`
   destructure into positional `Action<,,>` invocations rather than the single-argument shape the
   other twenty-one use.

The emission is therefore not a true projection of the interface, which is the exact condition
[#2770](https://github.com/Sytone/botnexus/issues/2770) AC4 uses to rule generation out.

What *is* projectable is the set of names, and that is pinned by
`HubRegistrationExhaustivenessTests` in `BotNexus.Gateway.Tests` - the one project that references
both the SignalR extension (for the generated `HubEvents.All`) and the Blazor client. It parses the
`On<...>("Name", ...)` literals out of `GatewayHubConnection.cs` and diffs them against the
generated inventory in both directions, so adding a member to `IGatewayHubClient` without a matching
registration fails **naming the missing event** instead of reporting a count mismatch.
