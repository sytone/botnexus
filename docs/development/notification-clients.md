# Building a notification client

This is the contract for writing something that reports BotNexus notifications outside the portal:
a phone app, a desktop app, a status bar widget, a script. The [user guide](../user-guide/notifications.md)
describes the feature; this describes the wire.

Everything here is server-side by design. Read state, dismissals and history live on the gateway,
so two clients on two devices agree without talking to each other, and a client that was switched
off for a week sees what it missed rather than starting blank.

## Pick a delivery model first

This is the decision that shapes everything else, and it is constrained by the platform rather than
by BotNexus.

| Client | Live while running | Wakes when closed | Notes |
| --- | --- | --- | --- |
| Web page | SignalR | Web Push | Both already built |
| PWA installed to a home screen | SignalR | Web Push | iOS included, but see below |
| Native Android | SignalR | **Not built** — needs FCM | |
| Native iOS | SignalR | **APNs** | Needs an Apple Developer account — see below |
| Native Windows / macOS / Linux | SignalR | **Not built** — needs WNS, or run in the background | |
| Script, CI, status bar | Polling | n/a | Simplest thing that works |

**Web push cannot wake a native app.** Apple and Google only wake a native app for a push that came
from APNs or FCM respectively — a web push subscription reaches the browser, and nothing else.

For **iOS** that sender now exists; see [Native iOS over APNs](#native-ios-over-apns).

For **Android**, FCM is not built. A native Android client gets the REST API and a live SignalR
stream while it is running, which is enough for an app in the foreground and not enough for one
expected to buzz overnight. Adding it is the same shape as APNs: a sender beside `ApnsSender` on
the same broadcaster with its own device-token store.

The alternative on either platform is to **ship a PWA**, where web push works today. On iOS the
portal must be added to the Home Screen via Share → Add to Home Screen before Safari will grant
push; a normal tab cannot subscribe.

## Authentication

If the gateway has no API keys configured it runs in development mode and accepts every request.
That is how a local gateway behaves out of the box, and it is why a `curl` against loopback works
with no headers.

Once keys are configured under `gateway.apiKeys` in `config.json`, every caller must present one:

```http
Authorization: Bearer {key}
```

or

```http
X-Api-Key: {key}
```

Either header is accepted on every path, `/hub/gateway` included — the hub is not exempt from
auth. Treat a 401 as "the key is missing or wrong", not as "this endpoint does not exist".

### The key must be a header, and that constrains SignalR

**There is no `access_token` query parameter.** The auth handler reads headers only. This is the
usual way a browser SignalR client authenticates, precisely because a browser cannot set headers on
a WebSocket handshake — and it does not work here.

The consequence depends on what you are writing:

| Client | Against a keyed gateway |
| --- | --- |
| Native SignalR client (.NET, Swift, Java, Python) | Fine — it controls the handshake headers |
| Browser SignalR client | Cannot authenticate a WebSocket or SSE handshake |

A browser client has to fall back to the long-polling transport, which sends headers via `fetch`,
or reach the gateway on an origin where no key is required. If you are writing a browser client
against a gateway with keys configured, verify this early — it is the kind of thing that works in
development, where no keys are set, and fails the first time the gateway is locked down.

## The REST API

Base path `/api/notifications`. All timestamps are ISO-8601 with an offset. All property names are
camelCase.

| Method | Path | Success | Purpose |
| --- | --- | --- | --- |
| `GET` | `/api/notifications` | 200 | List, newest first |
| `GET` | `/api/notifications/unread-count` | 200 | Just the number |
| `POST` | `/api/notifications/{id}/read` | 204 | Mark one read (404 if unknown) |
| `POST` | `/api/notifications/read-all` | 200 | Mark everything read |
| `DELETE` | `/api/notifications/{id}` | 204 | Delete permanently (404 if unknown) |
| `POST` | `/api/notifications/test` | 202 | Raise a test notification |

`GET /api/notifications` takes `includeRead` (default `true`) and `limit` (default `100`, clamped
to `500`).

### The notification object

```json
{
  "id": "56d9bc3f18364846802f12203228c0b7",
  "kind": "AgentRunFailed",
  "severity": "Error",
  "title": "Agent 'assistant' run failed",
  "body": "The provider returned 529.",
  "agentId": "assistant",
  "conversationId": "c47f...",
  "link": "agent/assistant/conversation/c47f...",
  "createdAtUtc": "2026-08-28T02:14:33.4210000+00:00",
  "readAtUtc": null
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `id` | string | Stable. Use it to deduplicate across transports |
| `kind` | string | A **name**, never a number — see below |
| `severity` | string | `Info`, `Warning` or `Error` |
| `title` | string | Written to be readable on a lock screen |
| `body` | string? | Optional detail |
| `agentId` | string? | Present when the notification concerns one |
| `conversationId` | string? | Present when the notification concerns one |
| `link` | string? | **Site-relative, no leading slash.** Null when there is nowhere to go |
| `createdAtUtc` | timestamp | |
| `readAtUtc` | timestamp? | Null while unread |

### Kinds

| Kind | Severity | Raised when |
| --- | --- | --- |
| `AgentRunFailed` | Error | An agent run ends in an error |
| `AgentWaitingForInput` | Warning | An agent asks a question and blocks |
| `CronRunOutcome` | Error | A scheduled job fails, times out, or cannot deliver |
| `GatewayHealth` | Error, then Info | The gateway stops responding, and again on recovery |
| `AgentRunCompleted` | — | **Defined but never raised.** Reserved |

**Kind and severity are names on the wire, not the integers they are stored as.** That is a
deliberate contract: a client should not have to know which number means "waiting for input", and
renumbering the enum must not silently change what an installed client displays.

**Handle an unknown kind gracefully.** New ones will be added. Fall back to `title`, `body` and
`severity`, which are always present and always meaningful, rather than switching exhaustively on
`kind` and dropping what you do not recognise.

## Live updates over SignalR

The hub is at **`/hub/gateway`** — an ASP.NET Core SignalR hub. Official clients exist for
JavaScript, .NET, Java, Python and Swift; anything else can speak the protocol directly.

The server calls a client method named **`NotificationRaised`** with a single argument:

```json
{
  "id": "56d9bc3f...",
  "kind": "AgentRunFailed",
  "severity": "Error",
  "title": "Agent 'assistant' run failed",
  "body": "The provider returned 529.",
  "agentId": "assistant",
  "conversationId": "c47f...",
  "link": "agent/assistant/conversation/c47f...",
  "createdAtUtc": "2026-08-28T02:14:33.4210000+00:00"
}
```

Identical to the REST object except that **`readAtUtc` is absent** — a notification is unread at
the moment it is raised, by definition. Everything else is field-for-field the same, on purpose, so
a client parses one shape however the notification arrived.

```csharp
connection.On<NotificationPayload>("NotificationRaised", payload => Show(payload));
```

The hub carries much more than notifications — agent streaming, sub-agent events, conversation
changes. A notification client can subscribe to `NotificationRaised` alone and ignore the rest.

### Treat the push as lossy

This is the part worth designing around rather than discovering.

The broadcast is **not guaranteed**. It goes to whoever is connected at that instant, over a
bounded queue that drops the oldest entry when a slow client falls behind. A client that was
disconnected, backgrounded or merely slow will miss messages.

That is safe because **the store is authoritative**. The notification is written before it is
broadcast, so anything missed is still there to be read.

So: use the push for immediacy, and a read for truth.

- On connect, and on returning to the foreground, `GET /api/notifications` — do not assume the
  stream filled the gap.
- Deduplicate on `id`. The same notification can reach you over SignalR and over a subsequent read.
- Never treat the absence of a push as the absence of a notification.

## Polling

Entirely legitimate, and the right choice for a script or a status bar.

Poll `GET /api/notifications/unread-count` rather than the list — it exists precisely so a badge
does not have to fetch a hundred rows to learn one number. Fetch the list only when the count
changes or the user opens something.

Notifications are raised by failures, so they are rare. Every 30–60 seconds is ample; there is no
value in polling faster than a person would react.

## Web Push

Only for browsers and installed web apps. A native client cannot use this.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/notifications/push/key` | The gateway's VAPID public key |
| `POST` | `/api/notifications/push/subscribe` | Register or refresh a subscription |
| `POST` | `/api/notifications/push/unsubscribe` | Forget one. Idempotent |

The flow:

1. `GET /api/notifications/push/key` → `{"publicKey":"BLo5..."}`, base64url.
2. `registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey })` with that
   key converted to bytes.
3. POST the result:

```json
{
  "endpoint": "https://fcm.googleapis.com/fcm/send/...",
  "p256dh": "BCVxsr7N...",
  "auth": "BTBZMqHH6r4Tts7J_aSIgg"
}
```

Returns 204. A 400 means one of the three is missing, the endpoint is not https, or `p256dh` is not
a valid P-256 public key — validated on the way in, because a key that is merely the right *length*
would be stored happily and then fail on every notification forever while the browser believed it
was subscribed.

The pushed payload is a subset — `id`, `kind`, `severity`, `title`, `body`, `link` — encrypted per
RFC 8291 so the push service cannot read it. `userVisibleOnly: true` is a promise the browser
enforces: every push **must** result in a visible notification, so a service worker that draws
nothing will have the subscription revoked.

Requirements a client must satisfy:

- **A secure context.** Service workers are refused over plain http to anything but `localhost`.
- **Home Screen installation on iOS.** Safari grants push only to an installed web app.
- Re-subscribe and re-POST whenever the browser hands you a new subscription; endpoints rotate.

The gateway prunes a subscription when a push service reports it `404` or `410`, and keeps it
through anything else, so a client does not have to manage expiry — but it should still POST
`unsubscribe` when the user opts out, rather than leaving the gateway to discover it.

## Native iOS over APNs

The gateway can push to a native iOS app, and this is the only route that wakes one.

**It is off unless configured**, which is the ordinary state — a gateway with no Apple Developer
account does nothing here and says so once at startup rather than warning. Ask before registering:

```http
GET /api/notifications/apns/status
```

```json
{ "configured": true, "bundleId": "com.example.yourapp" }
```

`configured: false` means registering would achieve nothing; fall back to SignalR while the app is
open, and tell the user push is unavailable rather than waiting silently for notifications that
cannot arrive.

### Gateway configuration

Four values under `gateway.apns` in `config.json`, all from an Apple Developer account:

| Key | What it is |
| --- | --- |
| `gateway:apns:teamId` | Team identifier, ten characters |
| `gateway:apns:keyId` | Identifier of the .p8 signing key |
| `gateway:apns:bundleId` | The app's bundle identifier, sent as the APNs topic |
| `gateway:apns:privateKeyPath` | Path to the .p8 file |

Token-based (`.p8`) rather than certificates: one key signs for every app under the team and does
not expire, where a certificate is per-app and lapses annually — which is how push stops working on
an anniversary nobody wrote down. Keep the `.p8` readable only by the gateway user; it can sign for
every app you own.

### Registering a device

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/notifications/apns/status` | Whether the gateway can push to iOS at all |
| `POST` | `/api/notifications/apns/register` | Register a device token, or refresh one |
| `POST` | `/api/notifications/apns/unregister` | Forget a device. Idempotent |

```json
{
  "deviceToken": "a1b2c3d4...",
  "environment": "sandbox",
  "deviceName": "Brad's iPhone"
}
```

**`environment` is required and must be `sandbox` or `production`.** The two are not
interchangeable and the gateway will not guess: a sandbox token — which is what a build run from
Xcode produces — is refused by the production host as `BadDeviceToken`, an error that reads like a
bad token rather than the wrong address. Send `sandbox` for development and TestFlight-from-Xcode
builds, `production` for App Store builds.

Call `register` on **every launch**, from
`application(_:didRegisterForRemoteNotificationsWithDeviceToken:)`. iOS re-issues tokens without
warning — after a restore, an update, or for no visible reason — and re-registering an unchanged
one is expected and cheap.

A 400 means the token is not hex, is the wrong length, or the environment was missing or
unrecognised. Validated on the way in, because a token accepted now but malformed is refused by
Apple on every future notification while the app goes on believing it is registered.

### What arrives

```json
{
  "aps": {
    "alert": { "title": "Agent 'assistant' run failed", "body": "The provider returned 529." },
    "sound": "default",
    "category": "AgentRunFailed"
  },
  "id": "56d9bc3f...",
  "kind": "AgentRunFailed",
  "severity": "Error",
  "link": "conversation/c1"
}
```

`category` carries the kind so an app can attach notification actions to it. `id`, `kind`,
`severity` and `link` sit alongside `aps` as custom keys, matching the REST and SignalR shapes, so
one parser handles all three.

The push carries the notice, not the notification — there is no `body` beyond the alert, no
timestamps and no read state. Fetch `GET /api/notifications` when the app opens.

Two behaviours to design around:

- **`apns-collapse-id` is the notification id.** The same notification arriving twice replaces the
  earlier alert rather than stacking a duplicate.
- **A long body is trimmed.** Apple rejects a payload over 4KB outright rather than truncating it,
  so the gateway halves the body until it fits. The title is never trimmed.

A push is held for 24 hours for a device that is offline, then dropped. Nothing is lost — the
notification is in the store, and the app sees it on its next read.

### When a device goes away

The gateway deletes a registration when Apple reports `410 Gone`, `Unregistered`, or
`BadDeviceToken` — the app was deleted, or the token was never valid for that environment. It keeps
the registration through everything else, including a rate limit or an Apple outage.

A credential fault (`InvalidProviderToken`, `ExpiredProviderToken`, `TopicDisallowed`) is logged as
an **error** and deletes nothing: it affects every device at once and means `gateway:apns` is
wrong, not that anyone's phone is.

## Testing a client

`POST /api/notifications/test` raises a real notification through the ordinary publisher. It is
stored, broadcast over SignalR, counted in the unread count, and pushed — so it exercises whichever
path your client uses, without waiting for something to fail.

```bash
curl -X POST http://your-gateway:5005/api/notifications/test
```

Worth testing explicitly, because these are the paths that break quietly:

- Reconnect after the gateway restarts, and confirm you backfill by reading rather than assuming.
- The same notification arriving twice; confirm you deduplicate on `id`.
- An unknown `kind`; confirm you still display it.
- A null `link`; confirm you do not navigate to the app root.
- Read state set by *another* device; confirm your unread count follows it.

## What is not there

Stated plainly so it is not discovered halfway through an implementation.

- **No FCM or WNS sender.** A native Android or Windows client cannot be woken; iOS can, over
  APNs.
- **APNs is untested against Apple.** Everything the gateway decides on its own is covered by
  tests — the request, the signed token, which refusals are permanent — but no push has been sent
  to a real device, because that needs an Apple Developer account and a registered bundle id.
  Expect to shake out the first real send.
- **No per-kind subscription or muting.** A client receives everything and filters locally.
- **No server-side pagination cursor.** `limit` caps at 500 and there is no offset; the store is
  not designed to be paged through.
- **No delivery receipts.** The gateway does not know whether a client displayed anything.
- **No query-parameter authentication.** See above; this constrains browser SignalR clients
  against a keyed gateway.
- **No automatic pruning.** Read notifications are kept until deleted.
- **`AgentRunCompleted` is never raised.** Do not build a UI that waits for it.
