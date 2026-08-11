# Conversations API Reference

Reference for the **Conversations** endpoints — list, create, inspect, update, and
manage conversations, their channel bindings, per-conversation model overrides,
pinning, history, and audit log.

All endpoints are served under the base route `api/conversations` and require the
gateway API key (see [Authentication](README.md#authentication)).

Source: `src/gateway/BotNexus.Gateway.Api/Controllers/ConversationsController.cs`.

---

## Endpoints

| Verb | Route | Purpose |
|------|-------|---------|
| GET | `/api/conversations` | List conversations (optionally filtered by agent). |
| GET | `/api/conversations/{conversationId}` | Get a conversation with its bindings. |
| POST | `/api/conversations` | Create a conversation. |
| PATCH | `/api/conversations/{conversationId}` | Update title / purpose / instructions. |
| DELETE | `/api/conversations/{conversationId}` | Archive (soft-delete) a conversation. |
| POST | `/api/conversations/{conversationId}/reset` | Reset the active session. |
| POST | `/api/conversations/{conversationId}/bindings` | Add a channel binding. |
| DELETE | `/api/conversations/{conversationId}/bindings/{bindingId}` | Remove a channel binding. |
| GET | `/api/conversations/{conversationId}/history` | Get assembled cross-session history. |
| GET | `/api/conversations/{conversationId}/audit` | Get the audit log. |
| PUT | `/api/conversations/{conversationId}/override` | Set model / thinking / context override. |
| DELETE | `/api/conversations/{conversationId}/override` | Clear all overrides. |
| POST | `/api/conversations/{conversationId}/pin` | Pin the conversation. |
| DELETE | `/api/conversations/{conversationId}/pin` | Unpin the conversation. |
| GET | `/api/agents/{agentId}/conversations/{conversationId}/todo` | Get per-conversation todo state. |
| GET | `/api/agents/{agentId}/conversations/{conversationId}/pending-ask-user` | Get pending `ask_user` prompt. |

---

### `GET /api/conversations`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `agentId` | query | string | Optional. When omitted, returns global active summaries. When set, returns conversations relevant to that agent (owned + participating). |

Returns `200 OK` with a JSON array of conversation summaries (active only).

### `GET /api/conversations/{conversationId}`

Returns `200 OK` with the full conversation (including channel bindings), or
`404 Not Found`.

The response carries the three provenance axes a client needs to render a conversation
without inference, each emitted as the enum **name** (matching the existing string
convention already used for `status`):

| Field | Values | Meaning |
|-------|--------|---------|
| `kind` | `HumanAgent`, `AgentAgent`, `AgentSubAgent` | pairing topology - who is talking to whom |
| `source` | `Channel`, `Cron`, `Webhook`, `Agent` | origination trigger - why the conversation exists |
| `visibility` | `UserFacing`, `InspectableReadOnly`, `InternalHidden` | who may see the row |

All three are stamped once at creation and are never re-written. `visibility` defaults to
`UserFacing`, so conversations persisted before the field existed remain visible. See
[Conversation Provenance](../features/conversation-provenance.md) for the full model.

### `POST /api/conversations`

Creates a conversation. Body fields: `agentId` (required), `title`, `purpose`,
`instructions`. Missing `agentId` or invalid title/purpose/instructions return
`400 Bad Request`. Returns `201 Created` with a `Location` header and the created
conversation.

### `PATCH /api/conversations/{conversationId}`

Updates editable metadata. At least one of `title`, `purpose`, or `instructions`
must be present, else `400 Bad Request`. Returns `200 OK` with the updated
conversation, or `404 Not Found`. Resolver-owned legacy conversations cannot be
modified and return `400 Bad Request`.

### `DELETE /api/conversations/{conversationId}`

Archives the conversation (soft delete): resets the active session (flush memory,
cancel pending `ask_user`, seal session) on a best-effort basis, then archives.
Returns `204 No Content`, or `404 Not Found`.

### `POST /api/conversations/{conversationId}/reset`

Resets the active session without archiving the conversation. Returns `200 OK` with
`{ conversationId, outcome, sealedSessionId }`, `404 Not Found`, or
`503 Service Unavailable` when the reset service is not configured.

### `POST /api/conversations/{conversationId}/bindings`

Adds a channel binding. Body: `channelType`, `channelAddress`, `mode`,
`threadingMode`, `displayPrefix`. Returns `201 Created` with the binding, or
`404 Not Found` when the conversation does not exist.

### `DELETE /api/conversations/{conversationId}/bindings/{bindingId}`

Removes a channel binding. Returns `204 No Content`, or `404 Not Found` when the
conversation or binding does not exist.

### `GET /api/conversations/{conversationId}/history`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `limit` | query | int | Max entries. Default `50`, capped at `200`. |
| `offset` | query | int | Zero-based offset from the most recent entry. Default `0`. |

Returns `200 OK` with a paginated history response, `400 Bad Request` when
`offset < 0` or `limit <= 0`, or `404 Not Found`.

Each entry carries `kind` (`message`, `boundary`, or `compaction`) and, since
#2936, an `isFolded` boolean. `isFolded` is true when the underlying transcript
entry was folded into a later compaction summary. **Folded entries are returned.**
Compaction evicts an entry from the LLM context window; it does not delete the
transcript, so pre-compaction history stays reachable by paging and clients are
expected to render folded entries collapsed rather than as ordinary turns.

> **A page may exceed `limit`.** When a page boundary lands inside a contiguous run
> of folded entries, the server extends the page backwards over the rest of that run
> (bounded at 500 entries) so a multi-thousand-row compacted transcript does not
> require ~137 sequential requests. Page backwards with `offset += entries.length`
> and use `totalCount` - not a short page - to decide when history is exhausted.

### `GET /api/conversations/{conversationId}/audit`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `limit` | query | int | Max entries. Default `50`, capped at `200`. |

Returns `200 OK` with the audit entries (empty array when auditing is not
configured), or `404 Not Found`.

### `PUT /api/conversations/{conversationId}/override`

Sets per-conversation model / thinking / context overrides. Body fields: `model`,
`thinking` (`minimal`, `low`, `medium`, `high`, `xhigh`, `max`), `contextWindow`
(positive token count). Each null field clears that override. Overrides are
validated against the resolved model's capabilities; an unsupported value returns
`400 Bad Request`. Returns `200 OK` with the updated conversation, or `404 Not Found`.

### `DELETE /api/conversations/{conversationId}/override`

Clears all three overrides back to the agent default. Returns `200 OK`, or
`404 Not Found`.

### `POST` / `DELETE /api/conversations/{conversationId}/pin`

Pin / unpin a conversation. Returns `204 No Content`, or `404 Not Found`.

### Portal hydration endpoints

- `GET /api/agents/{agentId}/conversations/{conversationId}/todo` — returns the raw
  todo JSON (`200 OK`), `204 No Content` when there is none, or `404 Not Found`.
- `GET /api/agents/{agentId}/conversations/{conversationId}/pending-ask-user` —
  returns the raw pending `ask_user` JSON (`200 OK`), `204 No Content` when none,
  or `404 Not Found`.

---

## Example

**Create a conversation**

```http
POST /api/conversations
Content-Type: application/json
X-Api-Key: <key>

{
  "agentId": "farnsworth",
  "title": "Planning session",
  "purpose": "Sprint planning"
}
```

**Response**

```
201 Created
Location: /api/conversations/c_ab12...
```
