# Newline and empty text delta conformance (#3301)

## Contract under test

`tests/agent/BotNexus.Providers.Conformance.Tests/StreamingProviderConformanceTests.cs`
adds two inherited Facts to the existing provider suite:

- `Stream_NewlineOnlyText_EmitsExactTextDelta` calls
  `BuildTextPayload("\n", MapCanonicalStopReason("stop"))` and requires exactly one
  emitted `TextDeltaEvent`, with `Delta` exactly `"\n"`. A newline is content, not
  an empty fragment. This assertion inspects events, not assembled final text.
- `Stream_EmptyText_EmitsNoTextDelta` calls
  `BuildTextPayload("", MapCanonicalStopReason("stop"))` and requires no emitted
  `TextDeltaEvent`. An explicit empty text fragment is distinct from an empty
  HTTP response.

Both Facts reject every `ErrorEvent`, require the last event to be a
`DoneEvent` with `StopReason.Stop`, and require the result's stop reason to be
`StopReason.Stop`. An error or missing successful terminal cannot make the
empty-delta absence check pass. The existing `ExecuteAsync` helper collects the
stream events and result and asserts exactly one HTTP request.

These assertions do not depend on a fixture's `ExpectedTextEventSequence` or
ordering exclusions. Existing tests, assertions, payload builders, and the
harness remain unchanged.

## Concrete fixture and parser map

Fixture files below are under `tests/agent/`. Production paths are under
`src/agent/`.

| Concrete fixture file | Provider constructed | Parser path exercised |
| --- | --- | --- |
| `BotNexus.Agent.Providers.Anthropic.Tests/AnthropicProviderConformanceTests.cs` | `AnthropicProvider` | `BotNexus.Agent.Providers.Anthropic/AnthropicStreamParser.cs` |
| `BotNexus.Agent.Providers.OpenAI.Tests/OpenAIProviderConformanceTests.cs` | `OpenAICompletionsProvider` | `CompletionsStreamEngine` calls `OpenAIStreamProcessor.ParseOpenAiCompletionsAsync` in `BotNexus.Agent.Providers.Core/Streaming/` |
| `BotNexus.Agent.Providers.Copilot.Tests/CopilotProviderConformanceTests.cs` | `OpenAICompletionsProvider`, with a `github-copilot` model | The same `CompletionsStreamEngine` / `OpenAIStreamProcessor.ParseOpenAiCompletionsAsync` path, not the Copilot Messages parser |
| `BotNexus.Agent.Providers.OpenAICompat.Tests/OpenAICompatProviderConformanceTests.cs` | `OpenAICompatProvider` | `BotNexus.Agent.Providers.Core/Streaming/OpenAIStreamProcessor.cs`, via `ParseCompatAsync` |

### Wire payload verification

All four existing `BuildTextPayload` implementations explicitly include the
supplied text, including empty strings:

- Anthropic uses `JsonSerializer.Serialize(text)` as the `text` value of a
  `content_block_delta` frame whose delta type is `text_delta`. It surrounds
  this with message/block lifecycle frames and maps canonical `stop` to
  `end_turn`.
- OpenAI, Copilot, and OpenAICompat place `text` in
  `choices[0].delta.content`. Their `Data` helpers use
  `JsonSerializer.Serialize(payload)` without an empty-text filter. A finish
  frame with canonical `stop` mapped to `stop` and a `[DONE]` sentinel follow.

Thus `"\n"` is JSON-escaped inside a data frame and decoded as one LF character;
`""` is an explicit empty JSON string, not an omitted field or missing frame.
The tests reuse these builders rather than adding alternate fixture machinery.

## Coverage boundaries

This is coverage of the four concrete fixtures above, not every streaming
parser. Neither `ResponsesStreamParser` nor `CopilotMessagesStreamParser` is
currently exercised by a concrete fixture inheriting these Facts.

The separate
`tests/agent/BotNexus.Agent.Providers.Core.Tests/Streaming/ResponsesStreamAssemblyConformanceTests.cs`
has `NewlineOnlyDelta_IsPreserved`. Its helper returns concatenated final
`TextContent`, and the test checks the assembled `"para one\n\npara two"` text.
That is useful assembly coverage, but it does not assert the payload of an
emitted `TextDeltaEvent` and is not evidence for these inherited delta checks
on the Responses parser.

## Empty-fragment repair

At the baseline, `AnthropicStreamParser` appended, counted, and emitted empty
text fragments unconditionally. The empty-text Fact exposed that behavior in
remote validation. The parser now exits the text-delta case when
`text.Length == 0`, before accumulation, counting, or emission. It does not
trim or filter whitespace. Other parsers and rendering are unchanged.

## Mutation evidence and remaining work

Remote core run `20260906063149-6051e8e6` applied three mutations together:

| Emit path | Temporary mutation | Observed newline test failures |
| --- | --- | --- |
| `ParseOpenAiCompletionsAsync` | Replace `text.Length > 0` with `!string.IsNullOrWhiteSpace(text)` | OpenAI and Copilot fixtures |
| `ParseCompatAsync` | Replace `text.Length > 0` with `!string.IsNullOrWhiteSpace(text)` | OpenAICompat fixture |
| Anthropic text-delta case | Add `if (text.Length > 0 && string.IsNullOrWhiteSpace(text)) break;` | Anthropic fixture |

The Anthropic mutation deliberately leaves empty emission unchanged so the
same run also tests the baseline defect. All four newline assertions failed
because zero deltas were emitted. Anthropic's empty assertion failed because
one empty delta was emitted; the other three empty assertions passed. No other
tests failed. The result contract reports total 18,534, executed 18,496,
passed 18,491, failed 5, skipped 38, fixture failures 0, `isComplete=false`,
`failureReason=test-failures`, and no timeout. All mutations were reverted.

This is a batched mutation result, not independently executed one-mutant runs.
The issue remains referenced rather than automatically closed: separately
running each producer mutation, and extending emitted-payload coverage to
Responses and Copilot Messages if the broader all-producer scope is required,
remain follow-up work owned by #3301. The PR records final clean validation;
no existing assertion, skip policy, or threshold was relaxed.
