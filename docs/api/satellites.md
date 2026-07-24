# Satellites API Reference

Reference for the **Satellites** endpoints. Satellites are remote nodes (desktop or
device companions) that connect to the gateway; these read-only endpoints report
their registration and live connection status.

All endpoints are served under the base route `api/satellites` and require the
gateway API key (see [Authentication](README.md#authentication)).

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
| `capabilities` | string[] | Advertised capabilities. |
| `status` | string | Current status, lower-cased (`online`, `offline`, `stale`). |
| `lastSeen` | timestamp \| null | Last heartbeat time. |
| `connectionId` | string \| null | SignalR connection ID when online. |

---

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
