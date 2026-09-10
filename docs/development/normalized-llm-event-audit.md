# Normalized LLM Event Audit

Code-backed gap analysis of the normalized LLM event contract, for [#2078](https://github.com/Sytone/botnexus/issues/2078)
(child of [#2074](https://github.com/Sytone/botnexus/issues/2074), sequenced after the capability-aware
Copilot Responses WebSocket transport landed in [#2075](https://github.com/Sytone/botnexus/issues/2075)).

This is an **analysis document, not a specification change**. Nothing here was implemented as part of the
audit; each gap that justifies work is filed as its own small follow-up issue and named in the table below.

## Verification scope

The completion, Responses cancellation-status, shared-engine safety mapping, reasoning-usage, and
cancellation follow-up entries below were rechecked against main
`d67942adf9e3886b7e78c9f132198408eb0272e1` on 2026-09-07. This is source inspection, not a new
provider test run or a claim of universal provider parity. The separately supplied OpenAI-compatible
safety mapper still differs and is tracked by [#4051](https://github.com/Sytone/botnexus/issues/4051).
Other findings, file/line references, producer/consumer counts, and absence claims are retained as
historical audit evidence unless explicitly marked reverified; their current status is unverified here.

## Contract snapshot

The normalized contract is three types, all in `BotNexus.Agent.Providers.Core`:

| Type | File | Role |
|---|---|---|
| `AssistantMessageEvent` | `src/agent/BotNexus.Agent.Providers.Core/Streaming/AssistantMessageEvent.cs` | Abstract record with 13 sealed derivations — the discriminated union of streamed semantics |
| `LlmStream` | `src/agent/BotNexus.Agent.Providers.Core/Streaming/LlmStream.cs` | Unbounded `Channel<AssistantMessageEvent>` plus a `TaskCompletionSource<AssistantMessage>` for the final result |
| `AssistantMessage` | `src/agent/BotNexus.Agent.Providers.Core/Models/Messages.cs:29-39` | The terminal snapshot: content blocks, `Usage`, `StopReason`, `ErrorMessage`, `ResponseId` |

The 13 event cases (`AssistantMessageEvent.cs`) are `start`, `text_start`/`text_delta`/`text_end`,
`thinking_start`/`thinking_delta`/`thinking_end`, `toolcall_start`/`toolcall_delta`/`toolcall_end`, `warning`,
`done`, and `error`. `warning` is explicitly non-terminal and carries a stable machine-readable `Code`, a
content-free diagnostic `Message`, and the unchanged partial assistant message. Block lifecycle events carry
a `ContentIndex`; terminal `done`/`error` and non-terminal `warning` do not.

The contract has exactly two consumers and five producers:

- **Consumer:** `src/agent/BotNexus.Agent.Core/Loop/StreamAccumulator.cs` — the only translation from provider
  events to gateway `AgentEvent`s. Confirmed sole consumer: a repo-wide search for `TextDeltaEvent`,
  `ThinkingDeltaEvent`, or `ToolCallDeltaEvent` outside `Providers.Core` returns `StreamAccumulator.cs` and
  nothing else.
- **Producers:** `AnthropicStreamParser`, `OpenAIStreamProcessor` (chat completions, both the streaming and
  non-streaming paths), `ResponsesStreamParser`, `CopilotMessagesStreamParser`, `IntegrationMockProvider`.

The transport boundary #2074 asks about is **already clean at the type level**: no SSE frame, WebSocket
message, or provider DTO appears in any of the 13 event records. The gaps below are all about *semantics that
have nowhere to go*, not about wire framing leaking through.

## Gap analysis

| Area | Current normalized representation (cited) | Gap | Recommendation | Follow-up? |
|---|---|---|---|---|
| **Text deltas** | `TextDeltaEvent(ContentIndex, Delta, Partial)`, `AssistantMessageEvent.cs`. Emitted by `OpenAIStreamProcessor.cs:240`, `ResponsesStreamParser.cs:274`, `AnthropicStreamParser.cs:259`. | Contract is adequate. Empty deltas are suppressed by length guards (`OpenAIStreamProcessor.cs:219` `text.Length > 0`; `ResponsesStreamParser.cs:261` `if (delta.Length == 0) continue`), which is correct — an empty delta carries no semantics. | None. | No |
| **Newline-only text deltas** | Same as above. The guards are `Length > 0`, **not** `IsNullOrWhiteSpace`, so a `"\n"` delta survives on all three text paths. Verified: a repo-wide search for `IsNullOrWhiteSpace` applied to a delta payload returns no hits in any parser. | **Not a gap.** The issue's specific concern — newline-only deltas being dropped as "empty" — does not reproduce in current code. `StreamAssemblyConformance.Reconcile` (`StreamAssemblyConformance.cs`) additionally reconciles assembled text against the provider's authoritative final text and escapes `\r`/`\n` in its diagnostic, so a dropped or mangled newline is now self-reporting. | Document the `Length > 0` (never `IsNullOrWhiteSpace`) rule as a contract invariant so it is not "tidied" back into a whitespace check. | Yes — a conformance test pinning newline-only delta survival across all producers. |
| **Reasoning deltas** | `ThinkingStartEvent`/`ThinkingDeltaEvent`/`ThinkingEndEvent`; `ThinkingContent(Thinking, ThinkingSignature, Redacted)` in `ContentBlock.cs`. Anthropic maps `thinking_delta` at `AnthropicStreamParser.cs:262-264` and `signature_delta` at `:277-280`; Responses uses `thinkingStates` (`ResponsesStreamParser.cs:183-187`). | Reasoning **signature** is accumulated into content (`ThinkingContent.ThinkingSignature`) but never surfaced as an event field; `ThinkingContent.Redacted` is settable but no parser sets it — Anthropic's `redacted_thinking` block is pushed as an ordinary `ThinkingEndEvent` (`AnthropicStreamParser.cs:318-322`) with no redaction marker. A consumer cannot distinguish redacted reasoning from visible reasoning. | Set `Redacted = true` on the Anthropic redacted-thinking path. This is a genuine semantic loss, not a presentation concern. | Yes |
| **Tool-call lifecycle** | **Resolved in #3290.** `ToolCallStartEvent` and `ToolCallDeltaEvent` carry nullable `ToolCallId` and `ToolName`; `ToolCallEndEvent` carries the completed `ToolCallContent`. | `ContentIndex` still orders heterogeneous content blocks and is not an index into `AssistantMessage.ToolCalls`. Producers report a null identity only when it is genuinely unknown at emit time; consumers must not reconstruct one by indexing the partial message. | Correlate concurrent calls by the explicit id/name fields and preserve null as “not known yet.” | No |
| **Multiple concurrent tool calls** | Explicit id/name fields correlate each start and delta independently of the producer-specific `ContentIndex`. | Resolved with the tool-call identity contract above; interleaving no longer depends on a “most recent call” fallback. | Keep `ContentIndex` as block ordering metadata, not call identity. | No |
| **Incremental tool arguments** | `ToolCallDeltaEvent.Delta` is the raw JSON fragment; budgeted append via `StreamToolArgumentBudget` (`AnthropicStreamParser.cs:268-274`) throws `StreamToolArgumentsTooLargeException` rather than truncating (#2902). Partial parse via `StreamingJsonParser.Parse`. | Adequate. The overflow case is a terminal error by design, which is the right call — a truncated argument blob is invalid, not partial. | None. | No |
| **Refusal / safety output** | **Reverified safety mapping:** `CompletionsStreamEngine.MapStopReason:326-341` maps `content_filter` to `StopReason.Sensitive` with `"Content filtered by provider"`; `ResponsesStreamPrimitives.MapStopReason:118-128` also maps it to `Sensitive`. | #3296 repaired the shared engine, not every compatible-provider path. `OpenAICompatProvider.cs:204-211` supplies its private mapper (`:314-328`), which still returns `Error` for `content_filter`; `OpenAIStreamProcessor.ParseCompatAsync:614-630` uses that result. Earlier refusal-text representation/loss findings belong to the historical #3295 audit and were not reverified here. | Preserve the shared engine's diagnostic. Track the remaining compatible mapper separately; do not claim all completions/providers are unified or infer refusal-text status from a stop-reason fix. | Shared-engine #3296 resolved; compatible-provider gap [#4051](https://github.com/Sytone/botnexus/issues/4051); #3295 status not reverified |
| **Usage** | **Reverified: #3297 resolved the attribution gap.** `Models/Usage.cs:39-53` exposes nullable `Reasoning`; `CompletionsStreamEngine.ParseUsage:245-296` populates it and preserves an earlier reported value when a later chunk omits the breakdown. `ResponsesStreamPrimitives.ParseUsage:80-104` populates it from `output_tokens_details.reasoning_tokens`. | `null` means not reported; `0` means a reported zero. `Output` remains inclusive; reasoning is an attribution field, not an amount to subtract or bill again. The shared completions code retains its `completionTokens + reasoningTokens` arithmetic; this check does not establish accounting parity across every provider. | Preserve the null/zero distinction and reasoning count across partial usage chunks. | No new follow-up for #3297 |
| **Service tier** | Read from the wire and used **only** to select a price multiplier: `ResponsesStreamParser.cs:426-429` calls `ApplyServiceTierPricing(usage, responseTier ?? configuredTier)`. `OpenAIResponsesOptions.ServiceTier` is the request-side knob. | The tier that was actually **served** is consumed and discarded. It never reaches `Usage`, `AssistantMessage`, or any event, so a turn billed at a flex/priority rate is indistinguishable after the fact. Chat Completions does not read `service_tier` at all. | Surface the served tier on `Usage` (or an explicit diagnostics seam) so billing is auditable. | Yes |
| **Cache metadata** | `Usage.CacheRead` / `Usage.CacheWrite`, populated by `AnthropicStreamParser.UpdateUsage` (`:344-347`, from `cache_read_input_tokens` / `cache_creation_input_tokens`), `ResponsesStreamPrimitives.ParseUsage:73-79`, and `CompletionsStreamEngine.ParseUsage`. | Adequate for cost. The **retention class** actually applied (`CacheRetention.Short`/`Long`, `Enums.cs:60-66`) is request-side only and never echoed back, so a request for `Long` that the provider silently served as `Short` is unobservable. Lower value than the tier gap; noted, not filed. | Consider folding into the service-tier follow-up if that work opens the `Usage` record anyway. | No |
| **Finish metadata** | **Reverified: #3294 resolved the Responses status mismatch.** `ResponsesStreamPrimitives.MapStopReason:118-128` maps `"cancelled"` to `StopReason.Aborted`; `"failed"` remains `StopReason.Error`. | The earlier in-band cancellation-as-error finding no longer applies to this mapper. This status mapping is distinct from the cancellation event-shape contract below. | Retain cancellation versus failure as separate outcomes; do not infer every producer's behavior from this one mapper. | No new follow-up for #3294 |
| **Structured output** | **Not covered.** No `response_format` / `json_schema` handling exists anywhere in `src/agent/**` (repo-wide search: zero hits). | The normalized contract has no representation for schema-constrained output. Model-side structured output arrives as ordinary text and is indistinguishable from prose. | Out of scope for this audit — this is a feature, not a normalization gap, and belongs to whoever adds structured-output support. Recorded here so the absence is documented rather than assumed. | No |
| **Multimodal output** | `ImageContent(Data, MimeType)` exists in `ContentBlock.cs` and is a declared `[JsonDerivedType]`, but **no parser ever constructs one** (repo-wide search for `new ImageContent` in `src/agent/**`: zero hits). It is input-only, populated from `UserMessageContent`. | There is no `ImageStartEvent`/`ImageDeltaEvent`/`ImageEndEvent`, so a model that emits an image mid-stream has no normalized channel. The content block type is a half-built seam: present in the union, unreachable from any producer. | Do not build speculatively. Record the shape (a `content_start`/`content_delta`/`content_end` generalization of the existing text/thinking/toolcall triads) so the eventual addition does not become a fourth hand-copied triad. | No — design note only, no code justified until a provider emits it |
| **Recoverable warnings vs terminal errors** | **Resolved in #3291.** `WarningEvent(Code, Message, Partial)` is the sole abnormal-condition event that does not complete `LlmStream`; `WarningCodes` currently defines `stream_assembly_mismatch` and `malformed_chunk_skipped`. | The warning message is restricted to lengths, indices, and identifiers — never model or user content — because it flows to consumers and persisted transcripts. `StreamAccumulator` preserves the warning as a non-terminal warning event and continues accumulating the turn. | Keep warning codes stable and add a code constant before emitting a new warning class. Never turn a recoverable warning into `ErrorEvent` merely to make it observable. | No |
| **Provider-specific metadata seam** | `ProviderDiagnostics` (`Diagnostics/ProviderDiagnostics.cs`) is an `ActivitySource` + logger factory only — a telemetry seam, not a data seam. `StreamOptions.Metadata` (`StreamOptions.cs`) is request-side `Dictionary<string, object>`. | There is **no** response-side extension seam. The constraint in #2078 ("provider-specific metadata should use an explicit diagnostics/extension seam") is currently satisfied only vacuously: nothing leaks because nothing is carried. Service tier is the live example of a value that gets discarded for want of a place to put it. | The service-tier and warning follow-ups are the concrete first users; defer designing a general `IReadOnlyDictionary` bag until there are ≥3 real consumers, or it becomes the loose typing the constraint forbids. | No |
| **Event ordering invariants** | Enforced only by construction inside each producer. The sole test is `StreamingProviderConformanceTests.Stream_EmitsExpectedEventSequence` (`tests/agent/BotNexus.Providers.Conformance.Tests/StreamingProviderConformanceTests.cs:82-89`), which asserts an `ExpectedTextEventSequence` supplied by each derived fixture and is **skipped entirely when `SupportsStreamingSequence` is false**. | No producer-independent statement of the ordering rules exists. The real invariants — `start` precedes every block event; every `*_start` at index *i* is followed by zero or more `*_delta` and exactly one `*_end` at index *i*; exactly one terminal `done` or `error`; nothing after the terminal — are nowhere asserted as a shared law. Each fixture declares its own expected sequence, so a producer can be self-consistently wrong. | Add a producer-agnostic ordering validator exercised by the conformance base class against a captured event list. | Yes |
| **Cancellation invariants** | **Resolved in #3292.** All three producers now push an **`ErrorEvent(StopReason.Aborted, …)`**: `ResponsesStreamEngine.EmitAborted` and `CompletionsStreamEngine.EmitAborted` were converged onto the shape `AnthropicProvider` already used. | Previously the two engines pushed a `DoneEvent(StopReason.Aborted, …)` for the identical condition, so a consumer matching on event type saw cancellation as normal completion from two of three producers. `StreamAccumulator` masked it — both cases emit `MessageEndEvent` (`StreamAccumulator.cs:210-236`) and `ErrorEvent` re-applies `error.Reason` — which is why it survived undetected. | Done: `StreamingProviderConformanceTests.Stream_Cancellation_EmitsErrorEventWithAbortedReason` asserts the event type by name across every producer, replacing the `ThrowsOrEmitsError` disjunction that both shapes satisfied. | No |
| **Completion invariants** | **Reverified: #3293 resolved the incomplete-result hang.** `LlmStream.End(AssistantMessage)` requires a result and rejects null before changing stream state (`LlmStream.cs:70-78`). `EndWithoutResult(string)` completes the channel and faults the pending result task with `LlmStreamIncompleteException` (`:91-98`). | There is no parameterless `End()` contract. The cancellation-aware `EndWithoutResult` overload uses token state to select cancellation versus fault; an already captured terminal result is preserved by the `TrySet*` transitions. Rejecting `End(null)` is not itself a stream-completion operation. | Producers with no result must use the explicit incomplete/cancelled termination seam, not omit the result. | No new follow-up for #3293 |
| **Producer-side `_done` race** | `LlmStream.Push` reads and writes `_done` as a plain `bool` with no synchronization; the channel is declared `SingleWriter = true` (`LlmStream.cs:14-18`). | Consistent — a single writer makes the unsynchronized field safe, and every producer does push from one task. Worth stating explicitly as a contract requirement rather than leaving it as an inferred property of the current implementations. | Note in the contract docs. | No |

## Summary of filed follow-ups

Each is deliberately single-clause, per the instruction in #2078 not to bundle semantic additions.

| # | Area | Shape |
|---|---|---|
| [#3290](https://github.com/Sytone/botnexus/issues/3290) | Tool-call lifecycle | **Resolved:** `ToolCallId`/`ToolName` now travel on start and delta events |
| [#3291](https://github.com/Sytone/botnexus/issues/3291) | Recoverable warnings | **Resolved:** non-terminal `WarningEvent` does not complete the stream |
| [#3292](https://github.com/Sytone/botnexus/issues/3292) | Cancellation invariant | **Reverified resolved:** Anthropic cancellation and the shared Responses/Completions `EmitAborted` paths push `ErrorEvent(StopReason.Aborted, ...)`; matches the main-table entry |
| [#3293](https://github.com/Sytone/botnexus/issues/3293) | Completion invariant | **Reverified resolved:** `End` requires a non-null result; explicit result-less termination faults or cancels the pending result task |
| [#3294](https://github.com/Sytone/botnexus/issues/3294) | Finish metadata | **Reverified resolved:** Responses `"cancelled"` maps to `Aborted`, separately from `"failed"` → `Error` |
| [#3295](https://github.com/Sytone/botnexus/issues/3295) | Refusal output | Refusal text is emitted as ordinary text deltas and dropped on the completions path |
| [#3296](https://github.com/Sytone/botnexus/issues/3296) | Safety mapping | **Reverified resolved for the shared engine:** `CompletionsStreamEngine` maps `content_filter` to `Sensitive` and retains its diagnostic; Responses agrees. The separate compatible mapper remains #4051 |
| [#3297](https://github.com/Sytone/botnexus/issues/3297) | Usage | **Reverified resolved:** nullable `Usage.Reasoning` preserves reported reasoning attribution in shared Completions and Responses parsing |
| [#3298](https://github.com/Sytone/botnexus/issues/3298) | Service tier | Served tier consumed for pricing and discarded |
| [#3299](https://github.com/Sytone/botnexus/issues/3299) | Reasoning deltas | Anthropic `redacted_thinking` never sets `ThinkingContent.Redacted` |
| [#3300](https://github.com/Sytone/botnexus/issues/3300) | Event ordering | Producer-agnostic ordering validator in the conformance base class |
| [#3301](https://github.com/Sytone/botnexus/issues/3301) | Newline deltas | Conformance test pinning newline-only delta survival across all producers |

The remaining follow-up rows above retain the original audit descriptions; this bounded update did
not re-audit their implementations or establish that their issues are still open.

| Additional current follow-up | Area | Verified remaining scope |
|---|---|---|
| [#4051](https://github.com/Sytone/botnexus/issues/4051) | Compatible-provider safety mapping | `OpenAICompatProvider.MapStopReason` still maps `content_filter` to `Error`; `ParseCompatAsync` consumes that supplied mapper. This is separate from the repaired shared engine in #3296. |

## Areas explicitly not covered (historical, not reverified)

Stated so absence is a finding rather than an omission:

- **Structured output** — no `response_format`/`json_schema` support exists anywhere in `src/agent/**`.
  Nothing to normalize until the feature exists.
- **Multimodal output** — `ImageContent` exists in the content union but no producer constructs one.
  No event triad is justified until a provider emits image output.
- **Gateway/channel presentation events** — a separate contract by the terms of #2078
  (`AgentEvent`, `MessageUpdateEvent`, and the SignalR fan-out). Out of scope.
- **Wire framing** — SSE and WebSocket transport details are #2074/#2075 territory and, per the
  non-goal in #2078, deliberately untouched here.
