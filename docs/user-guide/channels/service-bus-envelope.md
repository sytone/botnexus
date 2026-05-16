# Azure Service Bus Envelope Reference

This guide is for developers building services or tools that communicate with BotNexus through Azure Service Bus queues. It covers the JSON envelope format for both inbound (request) and outbound (reply) messages, application properties, and the metadata keys injected into the BotNexus message context.

---

## Inbound envelope (request to BotNexus)

Send a Service Bus message to the **inbound queue** (`botnexus-inbound` by default). The message body must be a UTF-8 JSON object with the following fields.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `messageId` | string | No | Client-assigned ID for idempotency and tracing. Appears in logs; not used for deduplication by the adapter itself. |
| `correlationId` | string | No | Arbitrary correlation token that is echoed back verbatim in the outbound reply envelope. |
| `agentId` | string | No | Target agent ID. If omitted, the gateway routes to the default agent. |
| `conversationId` | string | No | Conversation thread identifier. Used as the channel address for session routing, so messages with the same `conversationId` are grouped into the same conversation. |
| `sessionId` | string | No | Resume a specific existing session by ID. |
| `senderId` | string | No | Identifier of the sender (user name, service account, etc.). Used for `AllowedSenderIds` filtering. |
| `role` | string | No | Informational only. Always treated as `"user"` regardless of the value supplied. |
| `content` | string | **Yes** | The message text to deliver to the agent. |
| `replyTo` | string | No | Per-message override for the reply queue name. Takes precedence over `DefaultReplyQueueName`. |
| `timestamp` | ISO 8601 | No | When the message was created (e.g. `2024-11-01T14:30:00Z`). Informational; not used for scheduling. |
| `metadata` | object | No | Arbitrary key/value string pairs forwarded to the gateway as additional context. |

### Inbound example

```json
{
  "messageId": "msg-001",
  "correlationId": "req-abc-123",
  "agentId": "farnsworth",
  "conversationId": "conv-42",
  "sessionId": null,
  "senderId": "billing-service",
  "role": "user",
  "content": "Summarise the Q3 expense report attached to ticket #8821.",
  "replyTo": "billing-service-replies",
  "timestamp": "2024-11-01T14:30:00Z",
  "metadata": {
    "ticketId": "8821",
    "department": "Finance"
  }
}
```

---

## Outbound envelope (reply from BotNexus)

After the agent produces a response, BotNexus sends a Service Bus message to the reply queue (the value of `replyTo` from the inbound message, or `DefaultReplyQueueName` if `replyTo` was absent). The body is a UTF-8 JSON object.

| Field | Type | Description |
|-------|------|-------------|
| `messageId` | string | Gateway-assigned unique ID for this reply. |
| `correlationId` | string | Echoed from the inbound `correlationId`. |
| `agentId` | string | ID of the agent that produced the reply. |
| `conversationId` | string | Echoed from the inbound `conversationId`. |
| `sessionId` | string | ID of the session that handled the request (use this to resume the conversation). |
| `role` | string | Always `"assistant"`. |
| `content` | string | The agent's reply text. |
| `timestamp` | ISO 8601 | When the reply was produced. |
| `metadata` | object | Optional additional metadata. |

### Outbound example

```json
{
  "messageId": "reply-7f3a2c1e",
  "correlationId": "req-abc-123",
  "agentId": "farnsworth",
  "conversationId": "conv-42",
  "sessionId": "sess-0099aabb",
  "role": "assistant",
  "content": "The Q3 expense report for ticket #8821 shows total expenditure of $142,300, with the largest category being travel at $61,000 (43%). Three line items exceed policy limits and are flagged for review.",
  "timestamp": "2024-11-01T14:30:04Z",
  "metadata": {}
}
```

---

## Service Bus application properties

The adapter supports application properties on inbound messages as **fallbacks** when the corresponding envelope JSON fields are absent or null. This is useful for clients that set routing metadata at the transport layer rather than in the JSON body.

| Application property | Envelope field equivalent | Notes |
|----------------------|--------------------------|-------|
| `senderId` | `senderId` | Used for `AllowedSenderIds` filtering. |
| `conversationId` | `conversationId` | Thread / channel address for session routing. |
| `replyTo` | `replyTo` | Per-message reply queue override. |
| `correlationId` | `correlationId` | Echoed back in the outbound envelope. |
| `agentId` | `agentId` | Target agent ID. |
| `sessionId` | `sessionId` | Resume an existing session. |

> **Precedence:** Envelope JSON fields take priority over application properties when both are present.

On outbound messages, BotNexus mirrors the following values as Service Bus application properties to allow broker-side routing, filtering, or subscriptions:

| Application property | Value |
|----------------------|-------|
| `agentId` | Agent that produced the reply |
| `conversationId` | Conversation thread ID |
| `sessionId` | Session that handled the request |

---

## Metadata injected into InboundMessage.Metadata

When a message is dispatched to the agent, the adapter injects the following keys into `InboundMessage.Metadata`. These are available to agents, tools, and middleware running inside BotNexus.

| Key | Description |
|-----|-------------|
| `servicebus.requestKey` | A per-dispatch unique key generated by the adapter (useful for deduplication within a single processing run). |
| `servicebus.replyTo` | The resolved reply queue name (either from `replyTo` envelope field / application property, or `DefaultReplyQueueName`). |
| `servicebus.correlationId` | The correlation ID from the envelope or application property. |
| `servicebus.conversationId` | The conversation ID from the envelope or application property. |
| `servicebus.agentId` | The target agent ID from the envelope or application property. |

---

## See also

- [Service Bus channel configuration](./service-bus.md) -- prerequisites, YAML config, options reference, and managed identity setup.
