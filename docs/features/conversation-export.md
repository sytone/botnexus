# Conversation export

The portal can download the active conversation as a Markdown or HTML file. Open a conversation, choose **Export**, review the content controls, and choose **Download**. The browser uses the filename supplied by the gateway.

Choose the export scope in the dialog:

- **Whole conversation** downloads every persisted entry in the conversation.
- **Current active session** downloads the session currently attached to the conversation. This option is unavailable when the conversation has no active session.
- **Selected messages** lets you choose the first and last visible persisted messages. The preview shows the size of the resulting contiguous range, including messages between the two endpoints. Live messages that do not yet have a server entry ID and folded or hidden tool messages are not offered as range endpoints.

Cancelling the dialog clears any message range selection. The next export starts with no selected endpoints.

## Content and privacy controls

| Control | Default | Effect |
|---|---:|---|
| Include tool calls and results | On | Includes tool names, arguments, results, and errors. |
| Include thinking | Off | Includes assistant reasoning when the provider supplied it. |
| Include system messages and conversation instructions | Off | Includes system-role transcript entries and conversation-scoped instructions. |
| Redact recognised secrets | On | Replaces recognised credential shapes before the server renders the file. |

Review these controls before downloading. Instructions, system messages, tool arguments, tool results, and reasoning can contain sensitive information. Secret redaction reduces accidental disclosure, but it cannot recognise every kind of confidential data. Store and share the downloaded file according to the sensitivity of its contents.

If the gateway rejects the request or the download fails, the dialog shows an error and remains available for another attempt.
