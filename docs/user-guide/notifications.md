# Notifications

An agent that fails at 2am, or one that stops halfway through a job waiting for an answer, is only
useful information if it reaches you. The notification manager is the part of BotNexus that carries
that news out of the gateway and to wherever you actually are.

It has four layers, and you can stop at whichever one suits you:

| Layer | What it does | Needs |
| --- | --- | --- |
| The store | Records what happened, server-side, whether or not anyone is connected | Nothing |
| The bell | Shows it in the portal, live, with an unread badge | The portal open |
| Desktop alerts | Raises an OS notification when the portal is not what you are looking at | Permission, and a secure connection |
| Web push | Raises one with the portal **closed**, including on a phone | The same, plus a service worker |

The last two share a single switch. Web push is strictly better — it survives the tab being closed
— so it is used whenever the browser allows it, and the panel says which one you actually have.

The important design choice is that notifications are stored **on the gateway**, not in your
browser. Read state travels with them. Dismissing something on your laptop leaves it dismissed on
every other device, and a notification raised while every browser was closed is waiting for you when
one opens.

## The bell

The bell sits in the top bar. A red badge carries the unread count; it caps at `99+` rather than
growing without limit.

Opening it lists the fifty most recent notifications, newest first, read and unread together.

| Action | Effect |
| --- | --- |
| Click a notification | Marks it read and, when it has somewhere to go, takes you there |
| **Mark all read** | Clears the badge without dismissing anything. Only offered when something is unread |
| **&times;** on a row | Deletes that notification permanently |
| Click anywhere outside, or press <kbd>Esc</kbd> | Closes the panel |

An unread row carries a coloured bar down its left edge rather than a background wash, so a long
list stays legible and an unread item never looks disabled.

An empty list is the ordinary state of a healthy gateway. It says so rather than showing you an
empty table.

## What raises a notification

Four things do. Each one was chosen because it is something you would want to know *without*
watching for it.

| Raised when | Kind | Severity | Takes you to |
| --- | --- | --- | --- |
| An agent run ends in an error | `AgentRunFailed` | Error | The conversation, when there is one |
| An agent asks a question and blocks | `AgentWaitingForInput` | Warning | The paused conversation |
| A scheduled job fails, times out, or cannot deliver its result | `CronRunOutcome` | Error | The cron page |
| The gateway stops responding, and again when it recovers | `GatewayHealth` | Error, then Info | — |

Three deliberate silences are worth knowing about, because their absence is a design decision rather
than a gap:

- **Successful runs are not notified.** They are visible in run history, and a notification for
  every success is how people learn to ignore notifications.
- **Successful scheduled runs are not notified** either, for the same reason — and neither is a job
  *you* aborted, since being told about something you just did is noise.
- **A failing run notifies once**, not once per error event. A single run can emit several errors;
  you get one report of the failure.

The gateway health notification is also latched: it fires when the watchdog first sees the gateway
stop responding, and does not fire again until after it has recovered. A gateway that is still down
is not news a second time.

## Desktop alerts

The bell answers "what happened?" for someone looking at the portal. Desktop alerts answer it for
someone who is not.

**To turn them on:** open the bell and click **Enable desktop alerts** in the panel footer. Your
browser will ask for permission; granting it also opts you in.

That button is the only way in, and that is a browser rule rather than a choice — a permission
prompt raised any other way is ignored by Chrome and refused outright by Safari.

**If there is no button, the panel says why.** There are two quite different reasons it can be
missing, and they have opposite remedies — the panel tells them apart, and so do the two sections
below.

### The portal must be served securely

This is the one that catches most people, and it is not a browser setting.

Browsers only expose the Notification API in a **secure context**: HTTPS, or `localhost`. A portal
served over plain `http://` to a LAN address or hostname is neither. In that case the browser
reports the permission as *denied* before anyone has been asked, and **no site setting, flag or
re-prompt can change it**. Chasing it through browser settings is wasted effort.

Two things fix it:

- **Serve the gateway over HTTPS**, or
- **reach it on `localhost`**, which browsers treat as secure even over plain http.

The second is usually a port-forward. The panel prints the exact command for your gateway; note
that it forwards to the gateway's *own address* rather than to `localhost`:

```bash
ssh -L 5005:192.168.1.10:5005 192.168.1.10
```

The textbook `ssh -L 5005:localhost:5005 host` form looks equivalent and often is not: a gateway
bound only to its LAN address has nothing listening on its own loopback, so that tunnel forwards to
a closed port and the portal simply never loads. Forwarding to the host's own address works whether
it binds loopback, that address, or everything.

Then open `http://localhost:5005` and enable alerts there. The permission belongs to that origin,
so it is remembered for the tunnelled address rather than the LAN one.

### What you will and will not be interrupted by

An alert is **suppressed while the portal is visible and focused**. You are already looking at the
bell; the badge has moved, and a duplicate toast on top of it is exactly how people end up switching
notifications off.

| Where you are | What happens |
| --- | --- |
| Portal open and in front of you | No toast — the badge already said it |
| Portal open behind another window | Toast |
| Portal tab in the background | Toast |
| Portal closed entirely | Nothing now; it is waiting in the bell when you return |

A toast carries the notification's own identifier, so a repeat of the same notification **replaces**
its toast rather than stacking a second copy. Clicking one focuses the portal and navigates to
whatever it is about, without reloading the app.

### Scope, and turning them off

The permission and the opt-in both belong to **one browser on one machine**. Enabling alerts in
Chrome on your desktop does nothing for Firefox, for your laptop, or for your phone; each is a
separate grant. This mirrors how the browser itself treats the permission, and it means a shared or
public machine cannot inherit your choice.

Click **Desktop alerts on** to turn them off again. That only clears your opt-in — the browser
permission is left alone, so turning them back on later does not re-prompt.

### If the permission was refused

This is the other case: the portal *is* served securely, the prompt appeared, and it was dismissed
or refused.

Once a browser permission has been **denied**, no site can ever ask again from script. There is
nothing the portal can offer at that point, so it says so instead of showing a button that cannot
work, and gives the steps for your browser:

| Browser | Where to clear it |
| --- | --- |
| Chrome | The icon at the left of the address bar → Site settings → Notifications → Allow |
| Edge | The icon at the left of the address bar → Permissions for this site → Notifications → Allow |
| Firefox | The padlock at the left of the address bar → clear the blocked Notifications permission |
| Safari | Safari → Settings → Websites → Notifications → find the site → Allow |

Reload afterwards.

### Browsers that cannot do this

If the footer control is missing entirely, the browser has no Notification API at all and there is
nothing to enable.

## Web push

Everything above only works while the portal is open in a tab. Web push is the layer that does not
need it open at all — and the same one a phone or desktop app would use.

**You do not turn it on separately.** The single **Enable desktop alerts** switch takes push
whenever the browser offers it, because it is strictly better than the in-page alert. The panel
reports which you ended up with:

| What the panel says | What it means |
| --- | --- |
| including when the portal is closed | Push. Close the tab; alerts still arrive |
| only while the portal is open | The in-page fallback. Closing the tab stops them |

### How it reaches you

The browser mints a **subscription** — an address at its own vendor's push service, plus a key pair
— and hands it to the gateway. When a notification is raised, the gateway encrypts it to that key
and posts it to the push service, which wakes a service worker in your browser to draw it. That
worker runs whether or not the portal is open, which is the whole point.

**The push service cannot read your notifications.** Google, Mozilla and Apple relay the message
without being able to decrypt it: the payload is encrypted to a key only your browser holds, so
which agent failed is not visible to whoever operates the relay. The gateway signs each request
with its own identity (VAPID), which is what stops anyone who learns a subscription address from
pushing to it.

A push is held for **24 hours** for a device that is offline, then dropped by the push service.
Nothing is lost: the notification is in the store, and the bell shows it whenever that device next
opens the portal.

### Reaching a phone

**Android** works in Chrome and Firefox as an ordinary web page, once the portal is served over
HTTPS.

**iPhone and iPad need the portal installed to the Home Screen first.** Safari only grants push to
a web app added via Share → Add to Home Screen; a page open in a normal Safari tab cannot subscribe,
and the switch will report the in-page fallback instead. Open the installed app once and enable
alerts from there.

Each device is a separate subscription: enabling alerts on your laptop does nothing for your phone,
and turning them off on one leaves the others running. That is the same rule as the permission
itself.

### What the gateway keeps

| File | Holds |
| --- | --- |
| `~/.botnexus/push-subscriptions.sqlite` | One row per subscribed device |
| `~/.botnexus/vapid.json` | The gateway's push identity, generated on first use, mode `0600` |

**Do not delete `vapid.json`.** A subscription is bound to the public half of that key pair, so a
new one silently invalidates every device already subscribed — they stay subscribed as far as the
browser is concerned, and simply never hear anything again. Each would have to turn alerts off and
on to recover. It is not a secret in the usual sense, but it is not replaceable either.

The operator contact sent to push services defaults to the project URL and can be set with
`gateway:push:subject` — a `mailto:` or `https:` URI, required by the spec so a push service can
reach whoever runs a misbehaving gateway.

### Devices that go away

A phone gets wiped, a browser is uninstalled, a permission is revoked. The push service reports
that endpoint as gone, and the gateway **deletes the subscription** rather than retrying it forever.

Anything else — a rate limit, an outage — is treated as temporary and the subscription is kept.
Dropping one over a bad ten minutes would turn an outage into a device that never hears from the
gateway again.

## Writing your own client

Notifications are readable by anything that can make an HTTP request, and the store is the single
source of truth for every client — so a script, a status bar or a phone app all see the same
history and the same read state as the portal.

The wire contracts, authentication, the SignalR hub, and what each platform can and cannot do are
in [Building a notification client](../development/notification-clients.md).

## Checking that it works

Every real notification is raised by something going wrong, which makes the feature awkward to
verify — you would have to break something to find out whether being told works.

**Send test** in the panel footer raises one on purpose:

```bash
curl -X POST http://your-gateway:5005/api/notifications/test
```

It goes through the same publisher as everything else, so what it proves is the real path: the
notification is stored, pushed over SignalR to every connected portal, counted in the badge, and
raised as a desktop alert if this browser is set up for one. A test that wrote straight to the
store would prove nothing about the parts that actually fail.

The button is offered even where desktop alerts cannot work, because the store, the push and the
badge are worth checking on their own.

If the badge moves but no toast appears, the delivery chain is fine and the problem is in this
browser — see the two sections above.

## Where notifications live

`~/.botnexus/notifications.sqlite`, alongside the other gateway stores.

Nothing about a notification is kept in the browser except your desktop-alert opt-in. That is what
lets read state be shared, and it is the reason a phone or desktop app can later read exactly the
same records — which is the point of storing them server-side in the first place.

## Live updates

A connected portal is pushed each notification over SignalR as it is raised, so the badge moves
without polling.

The push is broadcast to every connected client, because a notification is about the gateway and its
agents rather than about one conversation. It is also allowed to be **lossy**: the store is
authoritative, and a client that was disconnected when something was raised picks it up on its next
read. Losing a push costs you immediacy, never the notification.

## The API

Every endpoint is under `/api/notifications`. Non-loopback callers need a gateway token like any
other endpoint.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/notifications` | List, newest first |
| `GET` | `/api/notifications/unread-count` | Just the number, for a badge |
| `POST` | `/api/notifications/{id}/read` | Mark one read |
| `POST` | `/api/notifications/read-all` | Mark everything read |
| `DELETE` | `/api/notifications/{id}` | Delete one permanently |
| `POST` | `/api/notifications/test` | Raise a test notification through the real publisher |
| `GET` | `/api/notifications/push/key` | The gateway's VAPID public key, needed before subscribing |
| `POST` | `/api/notifications/push/subscribe` | Register a device, or refresh one already held |
| `POST` | `/api/notifications/push/unsubscribe` | Forget a device. Idempotent |

`GET /api/notifications` takes two query parameters:

| Parameter | Default | Notes |
| --- | --- | --- |
| `includeRead` | `true` | Set `false` for unread only |
| `limit` | `100` | Clamped to 500, so a client cannot ask for the whole history |

```bash
curl -s localhost:5005/api/notifications?includeRead=false | jq
curl -s localhost:5005/api/notifications/unread-count
```

`kind` and `severity` come back as **names**, not numbers, and the wire shape is identical whether a
notification arrived over REST or over SignalR — so a client parses one shape either way.

```json
{
  "id": "a1b2c3",
  "kind": "AgentRunFailed",
  "severity": "Error",
  "title": "Agent 'assistant' run failed",
  "body": "The provider returned 529.",
  "agentId": "assistant",
  "conversationId": "c47f...",
  "link": "agent/assistant/conversation/c47f...",
  "createdAtUtc": "2026-08-28T02:14:33Z",
  "readAtUtc": null
}
```

`link` is site-relative and has no leading slash. It is `null` when there is nowhere useful to go —
a gateway health notification, for instance, is not about any one page.

## Limits worth knowing

- **Nothing is deleted automatically.** The store can prune read notifications older than a given
  age, but no scheduled task calls it yet. In practice the volume is low — these are failures, not
  events — but the file grows without bound until you delete rows yourself.
- **There is no per-kind muting.** Alerts are all-or-nothing per browser; you cannot ask for agent
  failures but not scheduled-job failures.
- **`AgentRunCompleted` is defined but never raised.** It exists in the API surface for clients to
  handle, and is reserved for an opt-in "tell me when it finishes" setting that does not exist yet.
- **Subscribing needs HTTPS.** Service workers are refused on an insecure origin, so web push has
  the same requirement as desktop alerts — and on a plain-http portal the switch quietly falls back
  to the in-page alert rather than failing.
- **A push carries the notice, not the history.** The service worker draws the title, body and
  link; anything else needs the portal or the API. That is deliberate — the push service can see
  the size of what it relays, so there is no reason to send it more than the notice.
- **There is no native app yet.** The gateway can now push to a native iOS app over APNs, and the
  REST surface is the one any client would read — but no app has been written against either, and
  the APNs path has never sent to a real device. Android and Windows would each need their own
  sender.

## Troubleshooting

**No bell in the top bar.** The notifications client is not registered, which means the portal was
served by a gateway without the notifications API. Check `GET /api/notifications` responds.

**The badge never moves, but notifications appear when I open the panel.** The live push is not
arriving. The badge is still correct on open, so nothing is lost. Check the gateway log for
`NotificationSignalRBridge started.` — and note that the console only shows warnings and above, so
look in `~/.botnexus/logs/` rather than at the terminal.

**There is no Enable desktop alerts button.** Either the portal is not served over a secure
connection, or the permission was refused — the panel says which, and they are covered above. The
first cannot be fixed in browser settings; the second cannot be fixed anywhere else.

**Alerts are on, but I get no toast.** Check the four cases in the table above first — a portal that
is visible and focused is *supposed* to stay quiet. Then check your OS: macOS Focus, Windows Focus
Assist and Do Not Disturb all swallow browser notifications without telling the page.

**Alerts say "only while the portal is open" and I want push.** The browser refused the
subscription. On a plain-http portal that is expected — see the secure-connection section. On
iPhone or iPad it usually means the portal is open in a Safari tab rather than installed to the
Home Screen, which Safari requires before it will grant push.

**A device stopped receiving alerts and nothing changed.** Check whether `~/.botnexus/vapid.json`
was replaced; every subscription made against the old key is silently dead. Turning alerts off and
on again on that device re-subscribes it.

**The bell is empty and I expected something.** Notifications are raised by failures. A quiet bell
usually means a healthy gateway rather than a broken one. To confirm the pipeline end to end, let a
scheduled job fail — or stop one mid-run — and watch for the entry.
