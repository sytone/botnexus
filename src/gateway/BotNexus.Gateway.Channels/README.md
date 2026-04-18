# BotNexus.Channels.Core

> Base channel infrastructure — abstract adapter base class, channel registry, and lifecycle management.

## Overview

This package provides the foundation for building channel adapters that connect external communication protocols (Telegram, Discord, Slack, console, etc.) to the BotNexus Gateway. It includes `ChannelAdapterBase`, an abstract base class implementing the template method pattern for consistent lifecycle management and allow-list enforcement, and `ChannelManager`, the runtime registry for looking up registered adapters.

## Key Types

| Type | Kind | Description |
|------|------|-------------|
| `ChannelAdapterBase` | Abstract class | Base class for channel adapters. Manages start/stop lifecycle, sender allow-list, and inbound message dispatching. |
| `ChannelManager` | Class | Implements `IChannelManager`. Read-only registry of `IChannelAdapter` instances, with case-insensitive lookup by channel type. |

## Usage

### Creating a New Channel Adapter

Derive from `ChannelAdapterBase` and implement the protocol-specific hooks:

```csharp
public sealed class DiscordChannelAdapter(ILogger<DiscordChannelAdapter> logger)
    : ChannelAdapterBase(logger)
{
    public override string ChannelType => "discord";
    public override string DisplayName => "Discord Bot";
    public override bool SupportsStreaming => true;

    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        // Connect to Discord gateway, register event handlers
    }

    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        // Disconnect from Discord gateway
    }

    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        // Send message via Discord API
    }

    public override async Task SendStreamDeltaAsync(
        string conversationId, string delta, CancellationToken cancellationToken = default)
    {
        // Edit the last message to append the delta (Discord supports message editing)
    }
}
```

### Dispatching Inbound Messages

Inside your adapter, call the inherited `DispatchInboundAsync` method to forward received messages into the Gateway routing pipeline:

```csharp
// Inside your message handler
var inbound = new InboundMessage
{
    ChannelType = ChannelType,
    SenderId = discordUserId,
    ConversationId = channelId,
    Content = messageText
};
await DispatchInboundAsync(inbound, cancellationToken);
```

The base class automatically checks the allow-list before dispatching. Messages from senders not in the allow-list are silently dropped with a debug log.

### Registering with DI

```csharp
public static class DiscordServiceCollectionExtensions
{
    public static IServiceCollection AddBotNexusDiscordChannel(this IServiceCollection services)
    {
        services.AddSingleton<IChannelAdapter, DiscordChannelAdapter>();
        return services;
    }
}
```

`ChannelManager` is constructed with all registered `IChannelAdapter` instances via DI.

## Configuration

### Allow-List

`ChannelAdapterBase` exposes an `AllowList` property. When populated, only messages from listed sender IDs are dispatched. An empty list (default) allows all senders.

### Capability Flags

| Property | Default | Description |
|----------|---------|-------------|
| `SupportsStreaming` | `false` | Whether the channel supports incremental content deltas. Override to `true` in adapters that support it. |

## Dependencies

- **Target framework:** `net10.0`
- **Project references:**
  - `BotNexus.Gateway.Abstractions` — `IChannelAdapter`, `IChannelManager`, `IChannelDispatcher`, message models
- **NuGet packages:**
  - `Microsoft.Extensions.Logging.Abstractions` — `ILogger` for lifecycle logging

## Extension Points

| Extension | How |
|-----------|-----|
| New channel adapter | Derive from `ChannelAdapterBase`, implement `OnStartAsync`, `OnStopAsync`, and `SendAsync`. Register as `IChannelAdapter` in DI. |
| Custom routing on inbound | Call `DispatchInboundAsync` with a populated `TargetAgentId` or `SessionId` on `InboundMessage` to override default routing. |
| Streaming support | Override `SupportsStreaming` to return `true` and implement `SendStreamDeltaAsync`. |

### Template Method Lifecycle

```
StartAsync(dispatcher)
  ├─ OnStartAsync()         ← Your protocol-specific startup
  ├─ IsRunning = true
  └─ Log: "Channel adapter '{type}' started"

StopAsync()
  ├─ OnStopAsync()          ← Your protocol-specific cleanup
  ├─ IsRunning = false
  └─ Log: "Channel adapter '{type}' stopped"
```

The base class handles dispatcher registration, running-state tracking, and lifecycle logging. You focus on protocol integration.
