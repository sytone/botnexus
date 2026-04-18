# BotNexus.Channels.Telegram

> Telegram Bot channel adapter for the BotNexus Gateway.

## Overview

This package provides a Telegram Bot channel adapter that connects the BotNexus Gateway to Telegram's messaging platform. It derives from `ChannelAdapterBase` and supports configuration for bot tokens, webhook URLs, and chat ID allow-lists.

**Status: Implemented** — Full Telegram Bot API integration with outbound message sending, chat ID filtering, and message chunking. Inbound message handling (long polling/webhook) is planned.

## Key Types

| Type | Kind | Description |
|------|------|-------------|
| `TelegramChannelAdapter` | Class | Telegram bot adapter. Sends outbound messages via Bot API; inbound webhook handling is planned. |
| `TelegramBotApiClient` | Class | HTTP client wrapper for Telegram Bot API — handles `sendMessage` and response parsing. |
| `TelegramOptions` | Class | Configuration options — bot token, webhook URL, and allowed chat IDs. |
| `TelegramServiceCollectionExtensions` | Static class | DI registration extension method `AddBotNexusTelegramChannel()`. |

## Current Capabilities

| Feature | Status | Notes |
|---------|--------|-------|
| Lifecycle management | ✅ Working | Start/stop with configuration logging |
| Outbound sends | ✅ Working | Sends via `TelegramBotApiClient.SendMessageAsync`; supports message chunking |
| Inbound messages | ❌ Planned | Long polling or webhook → `InboundMessage` → dispatch |
| Streaming deltas | ✅ Working | Accumulates deltas and sends as edited messages |
| Chat ID allow-list | ✅ Working | `AllowedChatIds` enforced on outbound; inbound enforcement pending |

### What It Does Now

- Registers as channel type `"telegram"` with display name `"Telegram Bot"`
- Sends outbound messages via `TelegramBotApiClient` using the Telegram Bot API
- Supports message chunking for messages exceeding `MaxMessageLength`
- Accumulates streaming deltas and sends/edits Telegram messages as content arrives
- Reports `SupportsStreaming = true` (pseudo-streaming via message editing)
- Enforces `AllowedChatIds` on outbound sends
- On `StartAsync`: logs startup with webhook URL status and allowed chat count
- On `StopAsync`: logs shutdown

### What's Planned

- Long polling or webhook mode for receiving inbound updates
- Mapping Telegram updates to `InboundMessage` and dispatching through `IChannelDispatcher`
- Chat ID allow-list enforcement on inbound messages

## Usage

### Registration

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBotNexusTelegramChannel(options =>
{
    options.BotToken = builder.Configuration["Telegram:BotToken"];
    options.WebhookUrl = builder.Configuration["Telegram:WebhookUrl"];
    options.AllowedChatIds.Add(123456789);  // Restrict to specific chats
});
```

### Configuration via appsettings.json

```json
{
  "Telegram": {
    "BotToken": "your-bot-token-here",
    "WebhookUrl": "https://your-domain.com/api/telegram/webhook"
  }
}
```

## Configuration

| Option | Type | Description |
|--------|------|-------------|
| `BotToken` | `string?` | Telegram Bot API token from [@BotFather](https://t.me/botfather). Required for API calls. |
| `WebhookUrl` | `string?` | Public URL for webhook mode. If unset, the adapter would use long polling. |
| `AllowedChatIds` | `ICollection<long>` | Telegram chat IDs allowed to interact with this bot. Empty allows all chats. |

## Dependencies

- **Target framework:** `net10.0`
- **Project references:**
  - `BotNexus.Gateway.Abstractions` — `IChannelAdapter`, `IChannelDispatcher`, message models
  - `BotNexus.Channels.Core` — `ChannelAdapterBase`
- **NuGet packages:**
  - `Microsoft.Extensions.DependencyInjection.Abstractions` — DI registration
  - `Microsoft.Extensions.Options` — `IOptions<TelegramOptions>` binding

## Extension Points

This is a concrete adapter. To customize Telegram behavior:

- Configure `TelegramOptions` via DI to set bot token, webhook URL, and allowed chats
- When the full implementation lands, extend by overriding or decorating the adapter's message mapping

## Reference

- [Telegram Bot API documentation](https://core.telegram.org/bots/api)
- [BotFather — creating a new bot](https://t.me/botfather)
