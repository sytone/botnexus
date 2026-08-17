# Test Channel

**Extension ID:** `botnexus-test-channel`
**Project:** `src/extensions/BotNexus.Extensions.Channels.Test`
**Status:** opt-in only — **disabled in the shipped manifest**

The test channel is a lightweight, HTTP-driven `IChannelAdapter` that exists so an integration test
can participate in a conversation as a **real, named, non-portal channel**.

## Why it exists

The integration harness could previously only join a conversation as a SignalR/portal client. That
makes multi-channel behaviour — cross-channel fan-out, per-binding delivery, channel-specific
echo — testable only against in-memory doubles, which bypass the routing pipeline, the channel
adapter lifecycle and the real dispatcher. A test that proves a mock works has proven nothing about
the gateway.

This adapter is registered through the ordinary extension loader, started by the normal channel
startup coordinator, and dispatches through the real `IChannelDispatcher`. Nothing in the pipeline is
stubbed; only the transport is replaced by an HTTP surface a test can drive.

## Safety: opt-in, never loaded by default

::: danger This extension exposes an unauthenticated message-injection endpoint.
`POST /test-channel/{channelId}/inbound` injects arbitrary messages into the gateway as if a real
user had sent them. It must never be enabled on a reachable deployment.
:::

Two independent mechanisms keep it off:

1. The shipped `botnexus-extension.json` declares `"enabled": false`, so
   `LoadConfiguredExtensionsAsync` filters it out of every configuration that does not deliberately
   turn it on.
2. Every HTTP handler resolves the adapter from the live `IChannelAdapter` set and returns `404`
   when it is absent, so even a host that somehow mapped the endpoints without the channel exposes
   no injection surface.

`TestChannelOptInTests` fails the build if either guarantee regresses, and pairs the negative
assertion with a positive one that opts the same staged manifest in — so the negative case cannot
pass merely because nothing was discovered.

## Enabling it for a test run

Either flip `enabled` to `true` in the deployed copy of the manifest, or compose the channel
in-process:

```csharp
builder.Services.AddBotNexusTestChannel(options =>
{
    options.ChannelId = "telegram";   // stand in for any channel key
    options.DisplayName = "Test Channel";
    options.MaxCapturedLogEntries = 2000;
});
```

### Standing in for another channel

`channelId` is the adapter's own `ChannelType`. Setting it to an existing key (for example
`telegram`) makes the router, bindings and fan-out treat this adapter **as that channel** — which is
the only way to exercise those paths without a live bot. Route matching compares the URL segment
against the adapter's own channel key, so a request addressed to `telegram` only reaches the test
adapter when the test adapter really is registered as `telegram`.

## HTTP API

| Endpoint | Description |
|---|---|
| `POST /test-channel/{channelId}/inbound` | Inject an inbound message into the gateway. |
| `GET /test-channel/{channelId}/outbound` | Poll captured deliveries. Optional `?address=` filter. |
| `DELETE /test-channel/{channelId}/outbound` | Clear the capture queue. Optional `?address=` filter. |
| `GET /test-channel/logs` | Read captured structured log entries. |
| `DELETE /test-channel/logs` | Clear the log buffer. |

Status codes on injection are meaningful and worth honouring:

- `202 Accepted` — dispatched.
- `404 Not Found` — no test adapter is registered under that channel key.
- `409 Conflict` — the adapter exists but is not running, so the message was **not** dispatched.

The `409` case matters: answering `202` there would convert a start-order defect into an unexplained
test timeout somewhere else.

## `TestChannelClient`

`TestChannelClient` wraps the API and ships in the extension assembly, so any test project can use it
with one project reference. It deliberately lives beside the endpoints it calls — a helper in a
different assembly from its own surface is how the two drift apart while both suites stay green.

```csharp
using var channel = new TestChannelClient(baseUrl, channelId: "telegram");

await channel.InjectMessageAsync("hello from portal", address: "chat-100");

var reply = await channel.WaitForMessageAsync("chat-100", timeout: TimeSpan.FromSeconds(5));
reply.Content.ShouldStartWith("User Said:");

var logs = await channel.GetLogsAsync();
logs.IsComplete.ShouldBeTrue();
logs.Entries.ShouldContain(entry => entry.Properties["ChannelType"] == "telegram");
```

Two behaviours worth knowing:

- **`WaitForMessageAsync` skips stream deltas.** A delta is usually a strict prefix of the final
  message, so a waiter that matched one would assert against truncated text and the failure would
  read as a content bug rather than a harness one.
- **A timeout reports what actually arrived**, not just that nothing matched. The usual cause is a
  message that arrived with different content, and a bare "timed out" sends the reader hunting for a
  delivery bug instead.

## Log capture

`TestChannelLoggerProvider` is **additive** — it is appended to the host's providers, so console and
file logging keep working while the gateway is under test. Structured state is flattened at capture
time so assertions can target a named property (`ChannelType`, `botnexus.session.id`) rather than the
rendered message text, which changes whenever someone rewords a log line.

The buffer is a ring bounded by `maxCapturedLogEntries` (default 2000). `GET /test-channel/logs`
returns `droppedEntryCount` alongside the entries, and `TestChannelLogSnapshot.IsComplete` exposes
it. This is not decoration: a **negative** assertion ("this was never logged") is unsupportable from
a truncated window, and the counter is the only way a caller can tell the difference.

::: warning The capture is host-wide, not channel-scoped.
It records ASP.NET's own request and routing entries too — including the ones produced by the very
request that reads the buffer. Filter by `Category` or a unique marker; an assertion on the total
entry count (or on an empty buffer after a clear) can never hold through the HTTP surface.
:::

## Configuration reference

| Key | Default | Description |
|---|---|---|
| `channelId` | `test` | Channel key the adapter presents itself as. |
| `displayName` | `Test Channel` | Human-readable adapter name. |
| `maxCapturedLogEntries` | `2000` | Log ring-buffer bound; oldest entries are evicted first. |

Bound from the `channels:test` configuration section, following the same convention as the Telegram,
Service Bus and Agent 365 channel extensions.

## Known gaps

The cross-channel user-message echo described in issue #320 is not implemented in the gateway, so
there is no echo scenario to assert against yet. Once #320 lands, an integration scenario driving the
echo through this channel is the natural follow-up. See issue #326 for the full acceptance criteria.
