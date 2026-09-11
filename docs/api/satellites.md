# Satellites API Reference

Reference for the **Satellites** endpoints. Satellites are remote nodes (desktop or
device companions) that connect to the gateway; these read-only endpoints report
their registration and live connection status.

All endpoints are served under the base route `api/satellites` and use normal
gateway authentication: an API key is required when keys are configured. The
no-key development mode is subject to the optional browser-Origin guard
(see [Authentication](README.md#authentication)).

Source: `src/gateway/BotNexus.Gateway.Api/Controllers/SatellitesController.cs`.

---

## Data type

### SatelliteStatus

| Field | Type | Notes |
|-------|------|-------|
| `id` | string | Satellite identifier. |
| `displayName` | string | Human-readable display name. |
| `platform` | string | Platform (`windows`, `macos`, `linux`). |
| `ownerUserId` | string | Owner user ID. |
| `capabilities` | string[] | Advertised capabilities. **Display-only — not an authorization control (#2606).** The gateway never reads this list to permit or refuse an operation, and an empty list behaves identically to a populated one. |
| `status` | string | Current status, lower-cased (`online`, `offline`, `stale`). |
| `lastSeen` | timestamp \| null | Last heartbeat time. |
| `connectionId` | string \| null | SignalR connection ID when online. |

---

## Heartbeat Timeout

The per-satellite setting `gateway.satellites.<id>.staleTimeoutSeconds` is an integer
in seconds, defaults to `120`, and has a declared valid range of `1` through
`2147483647`. The registry copies it from enabled satellite configuration during
startup; these read-only endpoints do not change it.

Freshness is measured with a monotonic clock, so changing the host's wall clock does
not change heartbeat age. An **online** satellite becomes a stale candidate only
when that age is **greater than** the configured timeout. An entry with no monotonic
heartbeat stamp is not selected. The periodic detector checks every 30 seconds by
default, so this is not an exact disconnect deadline.

The detector then marks the satellite **offline** and clears its `connectionId`.
Despite the setting's name and the `stale` enum value, this sweep does not publish
an intermediate `stale` status. `lastSeen` remains the wall-clock timestamp for
display; it is not the clock used for the timeout decision. Disabled configured
satellites are not seeded into this registry.

Sources: [SatelliteConfig](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway.Configuration/SatelliteConfig.cs),
[InMemorySatelliteRegistry](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Satellites/InMemorySatelliteRegistry.cs),
and [SatelliteStaleDetectionService](https://github.com/Sytone/botnexus/blob/main/src/gateway/BotNexus.Gateway/Satellites/SatelliteStaleDetectionService.cs).

## Endpoints

| Verb | Route | Purpose |
|------|-------|---------|
| GET | `/api/satellites` | List all registered satellites with current status. |
| GET | `/api/satellites/{satelliteId}` | Get a single satellite's status. |

### `GET /api/satellites`

Returns `200 OK` with a JSON array of satellite status objects.

### `GET /api/satellites/{satelliteId}`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `satelliteId` | path | string | The satellite identifier. |

Returns `200 OK` with the satellite status, or `404 Not Found` with body
`{ "error": "Satellite '<id>' not found." }` when it does not exist.

**Example response**

```json
{
  "id": "desktop-01",
  "displayName": "Jon's Laptop",
  "platform": "windows",
  "ownerUserId": "jon",
  "capabilities": ["shell", "screenshot"],
  "status": "online",
  "lastSeen": "2025-01-15T09:30:00+00:00",
  "connectionId": "abc123"
}
```
