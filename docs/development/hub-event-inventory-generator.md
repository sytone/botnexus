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

## What is still hand-written

The Blazor client's `On<T>(...)` registration block in `GatewayHubConnection.cs` is **not** generated.
It matches the interface exactly today (24/24) and each registration carries distinct delegate
wiring and payload types, so emitting it is a separate, larger change than the inventory. See
[#3318](https://github.com/Sytone/botnexus/issues/3318) and its follow-up for that half.
