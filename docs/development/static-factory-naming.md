# Static factory naming

Choose a static method name by its contract, not merely by whether it returns a domain type.

| Contract | Name | Failure behavior |
| --- | --- | --- |
| Interpret an encoded representation | `Parse` | Throws for documented invalid input; document the exception types and invalid-input conditions. |
| Attempt to interpret an encoded representation | `TryParse` | Returns `bool` and supplies parsed values through `out` parameters. Malformed input returns `false`, without malformed-input exceptions. Document output values on failure. |
| Construct a value from its components or another value | `Create` or `From` | May reject invalid construction arguments; document the constraints and exceptions. |

Domain operations that return result objects are exempt from this naming rule. Returning a richer type does not by itself make an operation a parser or factory: keep names that describe the domain operation and its result contract.

## Agent 365 classification

This stage delivers only the `Agent365ChannelAddress` classification and rename:

- `Encode(string conversationId, string? serviceUrl)` becomes `Create`: it constructs an opaque `ChannelAddress` from the conversation identity and optional reply service URL.
- `TryDecode(ChannelAddress address, out string conversationId, out string? serviceUrl)` becomes `TryParse`: it interprets the channel wire representation and reports success as a Boolean.
- The legacy method names are removed, not retained as forwarding aliases.

The rename preserves the existing wire format and behavior:

- Construction rejects a null conversation id with `ArgumentNullException`, and an empty or whitespace-only conversation id with `ArgumentException`; both identify `conversationId` as the parameter.
- A null, empty, or whitespace-only service URL is omitted. Otherwise the exact output is `<conversationId>|svc:<serviceUrl>`. Nonblank inputs are not trimmed, escaped, or normalized, and arbitrary service URL strings are accepted without URL validation.
- Parsing matches the first `|svc:` marker using ordinal, case-sensitive comparison. Everything after that marker is preserved verbatim, including additional markers. An absent or empty suffix produces a null service URL.
- A default or empty address returns `false`, with an empty conversation id and null service URL. An empty conversation prefix also returns `false`, but any nonempty suffix remains in the service URL output. Callers must check the Boolean rather than assume all outputs are cleared on failure.
- Parsing accepts nonempty whitespace verbatim, including whitespace-only conversation text or suffixes. This differs intentionally from construction's blank-argument rejection and optional-URL omission.

No new validation or behavior fixes accompany this rename. Characterization tests pin the existing behavior, and executable API-contract tests pin the public static signatures and absence of legacy names.

The remaining repository-wide classification and naming sweep is tracked by [issue #2926](https://github.com/sytone/botnexus/issues/2926); this bounded Agent 365 stage does not complete that sweep.
