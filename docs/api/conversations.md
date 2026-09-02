# Conversations API Reference

Reference for the **Conversations** endpoints — list, create, inspect, update, and
manage conversations, their channel bindings, per-conversation model overrides,
pinning, history, and audit log.

All endpoints are served under the base route `api/conversations` and require the
gateway API key (see [Authentication](README.md#authentication)). A handful of routes
are mounted under `api/agents/{agentId}/conversations/...` instead - those are marked
in the table below.

Sources: `src/gateway/BotNexus.Gateway.Api/Controllers/ConversationsController.cs` and
`src/gateway/BotNexus.Gateway.Api/Controllers/ConversationCanvasController.cs`. The
canvas concern was extracted into its own controller (#1688) but deliberately keeps the
`api/conversations` base route, so it is part of this surface rather than a separate one.

---

## Endpoints

| Verb | Route | Purpose |
|------|-------|---------|
| GET | `/api/conversations` | List conversations (optionally filtered by agent). |
| GET | `/api/conversations/costs` | Aggregate per-conversation cost roll-up. |
| GET | `/api/conversations/{conversationId}` | Get a conversation with its bindings. |
| POST | `/api/conversations` | Create a conversation. |
| PATCH | `/api/conversations/{conversationId}` | Update title / purpose / instructions. |
| DELETE | `/api/conversations/{conversationId}` | Archive (soft-delete) a conversation. |
| POST | `/api/conversations/{conversationId}/reset` | Reset the active session. |
| POST | `/api/conversations/{conversationId}/bindings` | Add a channel binding. |
| DELETE | `/api/conversations/{conversationId}/bindings/{bindingId}` | Remove a channel binding. |
| POST | `/api/conversations/{conversationId}/bindings/{bindingId}/move` | Move a channel binding to another conversation. |
| GET | `/api/conversations/{conversationId}/history` | Get assembled cross-session history. |
| GET | `/api/conversations/{conversationId}/audit` | Get the audit log. |
| GET | `/api/conversations/{conversationId}/export/{format}` | Download the whole conversation as a Markdown or HTML transcript. |
| PUT | `/api/conversations/{conversationId}/override` | Set model / thinking / context override. |
| DELETE | `/api/conversations/{conversationId}/override` | Clear all overrides. |
| POST | `/api/conversations/{conversationId}/pin` | Pin the conversation. |
| DELETE | `/api/conversations/{conversationId}/pin` | Unpin the conversation. |
| GET | `/api/agents/{agentId}/conversations/{conversationId}/todo` | Get per-conversation todo state. |
| GET | `/api/agents/{agentId}/conversations/{conversationId}/pending-ask-user` | Get pending `ask_user` prompt. |
| POST | `/api/agents/{agentId}/conversations/{conversationId}/messages` | Post a message into an existing conversation. |
| GET | `/api/agents/{agentId}/conversations/{conversationId}/canvas` | Get the rendered canvas HTML. |
| PUT | `/api/agents/{agentId}/conversations/{conversationId}/canvas` | Replace the canvas HTML. |
| GET | `/api/conversations/{conversationId}/canvas-state` | Get the whole canvas state dictionary. |
| GET | `/api/conversations/{conversationId}/canvas-state/{key}` | Get one canvas state key. |
| POST | `/api/conversations/{conversationId}/canvas-state/{key}` | Upsert one canvas state key. |
| DELETE | `/api/conversations/{conversationId}/canvas-state/{key}` | Delete one canvas state key. |

---

### `GET /api/conversations`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `agentId` | query | string | Optional. When omitted, returns global active summaries. When set, returns conversations relevant to that agent (owned + participating). |

Returns `200 OK` with a JSON array of conversation summaries (active only).

### `GET /api/conversations/costs`

Returns `200 OK` with a JSON array of per-conversation cost rollups, ranked by accumulation
descending with a deterministic conversation-id tie-break. One row per listed conversation.

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | string | The conversation the row addresses |
| `sessionCount` | int | How many sessions the conversation spans (the ramp signal) |
| `messageCount` | int | Transcript entries accumulated across those sessions |
| `compactionSummaryCount` | int \| null | Compaction summaries carried (the context-pressure signal) |
| `totalTokens` | long \| null | Provider tokens attributed to the conversation |

The nullable fields are nullable on the wire: **`null` means the signal was not measured, and is
never equivalent to `0`.** A measured zero and an unmeasured value are deliberately distinguishable,
because reporting the latter as the former would present "we did not look" as "this conversation is
free". `totalTokens` is `null` for every conversation today — no per-conversation provider-usage
measurement exists on this seam yet.

The rollup is derived at read time from the session and transcript tables; no stored counter backs
it. See [Conversation Cost](../features/conversation-cost.md).

### `GET /api/conversations/{conversationId}`

Returns `200 OK` with the full conversation (including channel bindings), or
`404 Not Found`.

The response carries the three provenance axes a client needs to render a conversation
without inference, each emitted as the enum **name** (matching the existing string
convention already used for `status`):

| Field | Values | Meaning |
|-------|--------|---------|
| `kind` | `HumanAgent`, `AgentAgent`, `AgentSubAgent`, `Ralph` | pairing topology - who is talking to whom |
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

Adds a channel binding. Body: `channelType` (**required**), `channelAddress`, `mode`,
`threadingMode`, `displayPrefix`. Returns `201 Created` with the binding.

| Status | When |
|--------|------|
| `201 Created` | Binding attached. |
| `400 Bad Request` | `channelType` missing or blank. |
| `404 Not Found` | The conversation does not exist. |
| `409 Conflict` | Another conversation of the same agent already holds a binding for this `(channelType, channelAddress)` pair. The response carries `conflictingConversationId`. |

The conflict guard exists because inbound routing resolves
`(agentId, channelType, channelAddress)` to exactly one conversation — a second
claim on the same pair would route messages non-deterministically. Addressless
bindings (empty `channelAddress`) are exempt.

### `DELETE /api/conversations/{conversationId}/bindings/{bindingId}`

Removes a channel binding. Returns `204 No Content`, or `404 Not Found` when the
conversation or binding does not exist.

### `POST /api/conversations/{conversationId}/bindings/{bindingId}/move`

Moves an existing binding to another conversation — the "merge two conversations"
and "re-home a channel onto a long-running conversation" operation. Body:

```json
{ "targetConversationId": "c_abc..." }
```

The binding keeps its `bindingId`, address, mode and `boundAt`, so outbound
fan-out suppression keyed on the originating binding keeps working. Detaching and
re-attaching is **not** equivalent: it mints a new `bindingId`.

| Status | When |
|--------|------|
| `200 OK` | Binding moved; the moved binding is returned. |
| `400 Bad Request` | `targetConversationId` missing, equal to the source, or owned by a **different agent**. |
| `404 Not Found` | Source conversation, target conversation, or the binding on the source does not exist. |
| `409 Conflict` | The target already holds a binding for the same `(channelType, channelAddress)`. |

Cross-agent moves are refused rather than performed: re-parenting a binding under a
different agent would silently re-route a live channel to a different brain.

Both the source and target conversations emit an `updated` change notification, and
the move is recorded in the audit log as `binding_moved` (attach and detach record
`binding_added` / `binding_removed`).

### `GET /api/conversations/{conversationId}/history`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `limit` | query | int | Max entries. Default `50`, capped at `200`. |
| `offset` | query | int | Zero-based offset from the most recent entry. Default `0`. |

Returns `200 OK` with a paginated history response, `400 Bad Request` when
`offset < 0` or `limit <= 0`, or `404 Not Found`.

Each entry carries `kind` (`message`, `boundary`, or `compaction`) and, since
#2936, an `isFolded` boolean. Since #2840 a message entry also carries `senderId`
— the origin attribution of a message posted through the
[messages endpoint](#post-api-agents-agentid-conversations-conversationid-messages),
e.g. `api:cron:pr-doctor`. It is `null` for ordinary channel turns and for every
entry persisted before #2840. Treat it as caller-supplied display text: render it
as provenance, never as an identity claim.

`isFolded` is true when the underlying transcript
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

### `GET /api/conversations/{conversationId}/export/{format}`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `format` | path | string | `markdown` or `html`. Anything else is `400 Bad Request`. |
| `firstEntryId` | query | string | Optional. Entry id — not an index — of the first included entry. |
| `lastEntryId` | query | string | Optional. Entry id of the last included entry. |

Renders the whole conversation — every linked session, with visible session boundary
markers — as a downloadable transcript assembled from the same projection that serves
`GET /api/conversations/{conversationId}/history`, so a download always agrees with what
the portal shows. Secret redaction is always on, and the HTML output is self-contained
(one inline `<style>` block, no `<script>`, no remote asset reference).

Returns `200 OK` with a `text/markdown` or `text/html` file attachment named
`<slug>-<yyyy-MM-dd>.<ext>`, or `404 Not Found`. An empty conversation still returns a
valid document carrying the header and an explicit "no messages" note; archived
conversations remain exportable and report their archived status.

The two range parameters are bound by `ExportRangeBinding`, shared with the session
export route so both spell the rule identically: **supply both or neither**. Supplying
only one is rejected with `400 Bad Request` and `{ "error": "range_incomplete", ... }`
rather than being completed from the transcript bounds, because inferring the missing
endpoint would hand back a document covering a range the caller never named. See
[Partial-range export](../api-reference.md#partial-range-export) for the full rejection
set.

Unlike the session surface, this route has **no** legacy literal-segment sibling — there
is no `/export/markdown` action competing with `{format}` here, so `markdown` binds to
this route and does accept the range parameters.

### Canvas and canvas state

Served by `ConversationCanvasController`, which keeps the `api/conversations` base route
deliberately: the iframe canvas bridge posts to `api/conversations/{id}/canvas-state/{key}`,
and a token-expanded `api/ConversationCanvas/...` base once 404'd every iframe write while
the agent tool path — which writes to the store directly rather than over HTTP — kept
working and hid the break (#1900).

| Verb | Route | Behaviour |
|------|-------|-----------|
| GET | `/api/agents/{agentId}/conversations/{conversationId}/canvas` | `200 OK` with `text/html`; `204 No Content` when the conversation has no canvas; `404 Not Found`. |
| PUT | `/api/agents/{agentId}/conversations/{conversationId}/canvas` | Body is the HTML string; an empty body clears it. `204 No Content`, or `404 Not Found`. |
| GET | `/api/conversations/{conversationId}/canvas-state` | `200 OK` with the state dictionary, `{}` when none has been set; `404 Not Found` when the conversation does not exist. |
| GET | `/api/conversations/{conversationId}/canvas-state/{key}` | `200 OK` with the raw JSON value; `404 Not Found` when the conversation **or** the key is absent. |
| POST | `/api/conversations/{conversationId}/canvas-state/{key}` | Body is the raw JSON value. `200 OK`, or `404 Not Found`. |
| DELETE | `/api/conversations/{conversationId}/canvas-state/{key}` | Idempotent — `204 No Content` even when the key did not exist; `404 Not Found` only when the conversation does not exist. |

Note the asymmetry on the key routes: `GET` treats a missing key as `404`, while `DELETE`
treats it as success. A successful `POST` or `DELETE` also broadcasts the change to
connected clients through the registered canvas notifiers, which is what drives the
`CanvasStateChanged` hub event.

See [Canvas](../features/canvas.md) for the agent-side tool surface and the
`window.canvasState` bridge these routes back.

### Portal hydration endpoints

- `GET /api/agents/{agentId}/conversations/{conversationId}/todo` — returns the raw
  todo JSON (`200 OK`), `204 No Content` when there is none, or `404 Not Found`.
- `GET /api/agents/{agentId}/conversations/{conversationId}/pending-ask-user` —
  returns the raw pending `ask_user` JSON (`200 OK`), `204 No Content` when none,
  or `404 Not Found`.

### `POST /api/agents/{agentId}/conversations/{conversationId}/messages`

Posts a message into an **existing** conversation and, by default, lets the agent take
a turn on it. This is the supported way for anything outside the gateway process — a
shell script, a CI step, a cron `command` job — to hand an agent work in a specific
thread.

The conversation must already exist; this endpoint never creates one. Use
`POST /api/conversations` for that.

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `message` | string | — | **Required.** Blank or missing returns `400 Bad Request`. |
| `wake` | bool | `true` | `true` schedules an agent turn. `false` appends the message to the conversation's session for history and audit without running the agent. |
| `sender` | string | `null` | Optional caller attribution recorded as provenance in history, e.g. `cron:pr-doctor`. Stored namespaced as `api:{sender}`; omitted it records the bare origin `api`. |

Returns `202 Accepted` with the resolved identifiers:

```json
{ "conversationId": "c_abc...", "sessionId": "s_def...", "wake": true }
```

The `sessionId` is the conversation's **already-bound** session, never a freshly
minted one — successive posts continue one thread rather than accumulating orphan
sessions, which is the failure mode `POST /api/chat` has when `sessionId` is omitted.

The response does **not** wait for the agent's reply. A fire-and-forget caller — the
common case — should not pay for the turn's latency; a caller that wants the reply
polls [`GET /api/conversations/{conversationId}/history`](#get-api-conversations-conversationid-history)
using the returned identifiers.

| Status | When |
|--------|------|
| `202 Accepted` | Message accepted. |
| `400 Bad Request` | Missing or blank `message`, or a malformed body. |
| `401 Unauthorized` | No or wrong API key (when a key is configured). |
| `403 Forbidden` | The key is scoped to other agents (see below). |
| `404 Not Found` | The agent does not exist, or the conversation does not exist **or is not owned by the named agent**. |
| `409 Conflict` | `wake:false` only — the conversation's session could not accept the append (e.g. it has been sealed). |

> A conversation owned by a *different* agent returns `404`, not `403`. "Exists, but
> not yours" would let a caller probe for other agents' conversation ids through a
> route their key is otherwise authorized for.

#### Calling it

```powershell
Invoke-RestMethod -Method Post `
  -Uri 'http://localhost:5005/api/agents/pr-doctor/conversations/c_abc/messages' `
  -Headers @{ 'X-Api-Key' = $env:BOTNEXUS_API_KEY } `
  -ContentType 'application/json' `
  -Body (@{ message = 'PR #123 has a failing check.'; wake = $true } | ConvertTo-Json)
```

**Where the API key comes from.** It is the gateway API key — the same one every other
`/api/*` route accepts, configured under `Gateway:ApiKey` (see
[Authentication](README.md#authentication)). Supply it as either `X-Api-Key: {key}` or
`Authorization: Bearer {key}`.

> [!WARNING]
> **Keyless development mode.** When **no** API key is configured, the gateway runs in
> development mode and allows *all* requests — including this one. A script written and
> tested against a keyless dev gateway will therefore work with no credentials at all,
> then fail with `401` the moment it is pointed at a gateway that has a key configured.
> That is not a change in the endpoint; it is the key going from absent to present.
> Send the key from the start.

**There is no loopback exemption, by design.** Requests from `127.0.0.1` are
authenticated exactly like any other. This endpoint can make an agent *act*, and an
origin-based bypass on a write endpoint of that kind is the wrong trade — any local
process, including one an attacker got a foothold in, would inherit the ability to
drive agents.

**Per-agent scoping applies automatically.** `agentId` is a route segment, so the
gateway's existing per-agent authorization sees it: a key whose `AllowedAgents` list
does not include the route agent receives `403 Forbidden`. A key scoped to one agent
cannot post into another agent's conversation.

#### `wake: false`

Appends the message to the conversation's session without scheduling a turn. The
message is durable and appears in `GET /api/conversations/{conversationId}/history`
like any other entry; the agent simply does not run. Useful for audit trails and for
aggregating context ahead of a single later wake.

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
