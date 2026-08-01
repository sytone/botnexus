# Seam-test reviewer checklist (epic #2084)

Issue #2086 requires a deterministic convention for the seam tests in the channel event
projection epic. Fully automating "this test uses a real collaborator, not a mock" is not
soundly decidable from static text — a hand-written fake and a real collaborator are the same
shape — so per the issue's own allowance this is enforced as a **structural check plus a
reviewer checklist** rather than a mock-detection regex. A noisy fence gets disabled, which is
worse than no fence.

## What is enforced automatically

`ChannelKnowledgeFenceArchitectureTests` (tests/architecture/BotNexus.Architecture.Tests):

- **Rule 7** asserts every host-side `BotNexus.Extensions.Channels.*` project can reach the
  generic `IConversationEventSink` seam (#2085) via `BotNexus.Gateway.Contracts` or
  `BotNexus.Gateway.Channels`. A channel extension that cannot reference the seam structurally
  cannot participate in it, so this is the soundly checkable half of the guard.
- **Rule 1** asserts the converse: no generic project references a concrete channel extension.
- `Scan_ActuallyReachesTheSourceTree` asserts minimum scan counts so a misrooted scan cannot
  pass green (#2349).

## What a reviewer must check by hand

When a PR in epic #2084 (#2087–#2091) declares a test as **seam coverage**:

1. The test constructs the **real** publisher (`ConversationEventPublisher`) — not a stand-in
   for it. The publisher is the seam; substituting it tests nothing.
2. At least one **real** `IConversationEventSink` implementation is registered, and the test
   asserts on the effect that sink produced, not on "the sink was called".
3. Test doubles are permitted only for collaborators **outside** the seam under test
   (clock, network transport, provider HTTP, persistence when not the subject).
4. The test does not assert on a concrete channel key (`"signalr"`, `"telegram"`, …) to prove
   generic behaviour — that reintroduces the coupling the epic removes.
5. No wall-clock/duration assertion (this repo already carries four timing-flaky tests).

## Known gaps (documented rather than silently unchecked)

- **Rule 3** is expressed as a *naming* check: generic orchestration must not declare symbols
  such as `signalRObservers` / `telegramFanOut`. Proving "no concrete channel type is *resolved*
  for observer behaviour" in the general case needs call-graph analysis (a `ChannelKey` can be
  computed at runtime), which is out of scope for a text fence. The naming rule plus Rule 2's
  literal ban covers every violation actually present in the tree today.
- **Rule 5** counts a bare `.SendAsync(` only when it carries an `OutboundMessage`, because
  `SendAsync` is also `HttpClient`'s and `WebSocket`'s method name. `SendStreamDeltaAsync` /
  `SendStreamEventAsync` are unambiguous and are matched unconditionally. A direct adapter send
  that passes a pre-built `OutboundMessage` variable with an unrecognised name would be missed;
  this is a deliberate precision-over-recall trade to keep the fence non-noisy.
