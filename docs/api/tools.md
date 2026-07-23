# Tools API Reference

Reference for the portal **Tools** endpoints. Tools are user-defined launchers
for external URLs. They are persisted server-side (SQLite) so they survive a
gateway restart and roam with the user across browsers and devices.

> This is the backend foundation (slice 1 of the portal Tools feature). The
> portal UI that consumes these endpoints is delivered in later slices.

All endpoints are served under the base route `api/tools`.

---

## Data type

### Tool

| Field            | Type      | Notes |
|------------------|-----------|-------|
| `id`             | string    | Stable identifier, supplied by the caller. Required. |
| `name`           | string    | Display name. Required. |
| `url`            | string    | Target URL the tool launches. Required. |
| `icon`           | string    | Emoji or character string. Optional, defaults to empty. |
| `order`          | int       | Ascending sort order within the list. Defaults to `0`. |
| `sandboxEnabled` | bool      | Whether the tool opens in a sandboxed frame. Defaults to `true`. |
| `createdAt`      | timestamp | Set server-side on creation; preserved across updates. |

---

## Endpoints

### `GET /api/tools`

Lists all tools ordered by `order` ascending (ties broken by `createdAt`).

Returns `200 OK` with a JSON array of tools.

### `GET /api/tools/{id}`

Returns `200 OK` with the tool, or `404 Not Found` when it does not exist.

### `POST /api/tools`

Creates a tool from the request body. Returns `201 Created` with a `Location`
header pointing at `GET /api/tools/{id}` and the created tool in the body.

### `PUT /api/tools/{id}` / `PATCH /api/tools/{id}`

Updates an existing tool. The route `id` wins over any `id` in the body, and the
original `createdAt` is preserved. Returns `200 OK` with the updated tool, or
`404 Not Found` when the tool does not exist.

### `DELETE /api/tools/{id}`

Deletes a tool. Returns `204 No Content`, or `404 Not Found` when the tool does
not exist.

---

## Persistence

Tools are stored in `tools.sqlite` in the gateway's writable data directory
(`BOTNEXUS_DATA_DIR`, falling back to the config directory). The store applies a
filesystem-aware SQLite journal mode (WAL on local disk, DELETE on network
mounts), consistent with the cron and webhook stores.
