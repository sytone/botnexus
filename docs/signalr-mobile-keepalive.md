# SignalR keep-alive & timeout tuning for the mobile hub path

> Issue: [#1840](https://github.com/Sytone/botnexus/issues/1840) (parent [#1836](https://github.com/Sytone/botnexus/issues/1836), PBI3)

## Problem

The mobile PWA reaches the gateway SignalR hub (`/hub/gateway`) through the
`n.netbird.sytone.net` netbird tunnel. Gateway logs showed connection lifetimes
swinging between ~800 ms and ~128 s — constant reconnect churn rather than one
stable long-lived connection. Two mismatches drove this:

1. **Keep-alive vs. tunnel idle cutoff.** A netbird (WireGuard-based) tunnel drops
   the NAT/idle mapping for a flow that goes quiet for longer than its idle window.
   When SignalR did not ping often enough (or the server timeout fired before a
   ping round-tripped over the higher-latency tunnel), the connection was torn down
   and immediately renegotiated.
2. **Reconnect budget too short for mobile.** Blazor WASM's default
   `WithAutomaticReconnect()` retries only ~5 times at 3 s (~15 s total) and then
   gives up. An iOS standalone PWA can be backgrounded far longer than 15 s, so a
   transient background drop became a terminal disconnect instead of self-healing on
   return.

## Chosen values

All values are **mobile-scoped** — the desktop portal passes no tuning and keeps the
framework defaults unchanged (no desktop regression).

### Client (mobile Blazor WASM)

Bound from the mobile client's `wwwroot/appsettings.json` under the `SignalR`
section, with mobile defaults in `MobileHubTuningOptions`:

| Setting | Default | Framework default | Reasoning |
| --- | --- | --- | --- |
| `KeepAliveIntervalSeconds` | 15 s | 15 s | Client pings every 15 s so the tunnel's idle window never elapses on a quiet connection. |
| `ServerTimeoutSeconds` | 60 s | 30 s | Widened so a mobile client tolerates radio stalls and tunnel jitter without declaring the server dead on a single missed ping. Kept ≥ 2× the keep-alive interval. |

The server timeout is always coerced to **at least twice** the keep-alive interval
(`ToTuning()`), matching SignalR's own guidance: a single lost ping must not trip a
timeout.

### Reconnect schedule (mobile)

`MobileReconnectRetryPolicy` adapts the pure, unit-tested `MobileReconnectBackoff`
schedule to SignalR's `IRetryPolicy`:

`2 s → 4 s → 8 s → 16 s → capped at 30 s`, **retrying indefinitely** (never returns
`null`). The first eight attempts already span > 60 s — well beyond the default
~15 s budget — and the connection keeps retrying at a steady 30 s cadence forever, so
a returning backgrounded app self-heals rather than surfacing a dead-end error bar.

### Server (gateway hub options)

Set via `SignalRHubLimits.Apply` on the `AddSignalR` `HubOptions` (options
registration only — the hub class body is untouched), overridable through
`gateway.signalR` in platform config:

| Setting | Default | Framework default | Reasoning |
| --- | --- | --- | --- |
| `KeepAliveIntervalSeconds` | 15 s | 15 s | Server pings idle clients every 15 s to keep the tunnel mapping warm from both directions. |
| `ClientTimeoutIntervalSeconds` | 30 s | 30 s | Server declares a client gone after 30 s of silence. Always coerced to ≥ 2× the keep-alive interval so a single missed client ping cannot flap the connection. |

## Why keep-alive < tunnel idle window

The netbird tunnel idle cutoff is the constraint. Both client and server ping every
15 s, so any given direction of the flow is never idle for more than ~15 s — safely
under a typical WireGuard/NAT idle window (tens of seconds to minutes). The timeouts
(client 60 s, server 30 s) sit above the 2× keep-alive floor so normal ping jitter
over the tunnel never trips a false-dead teardown. Together these remove the
sub-second/rapid renegotiate churn while still detecting a genuinely dead connection
within a bounded window.

## References

- SignalR configuration: server timeout should be at least double the keep-alive
  interval.
- dotnet/aspnetcore#18745, dotnet/aspnetcore#58336.
