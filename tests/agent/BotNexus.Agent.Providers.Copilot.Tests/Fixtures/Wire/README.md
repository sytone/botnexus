# Copilot Wire Fixtures

**Synthetic** SSE response bodies whose structure mirrors real GitHub Copilot
enterprise responses. Used by `CopilotWireReplayTests` to assert that today's
provider parsers (`AnthropicProvider`, `OpenAIResponsesProvider`,
`OpenAICompletionsProvider`) accept every wire shape Copilot returns.

These fixtures are the **baseline contract** that the upcoming Copilot
carve-out (#810) must preserve. Each fixture file pair is a regression anchor:
the new dedicated `CopilotProvider` must produce the same `StreamResult` from
the same body.

## Provenance

The body shapes here are modelled on real captures collected via mitmproxy
against `api.enterprise.githubcopilot.com`, but every field that could carry
identifying information has been replaced with a deterministic dummy:

- **Message / tool-use ids** — replaced with the consistent pattern
  `msg_01FixtureMessage000000NN` and `toolu_01FixtureToolUse000000NN`
  (same character class and length as the originals; NN is a sequence
  number).
- **Assistant content text** — replaced with placeholder strings so no
  developer prompt content, file path, or repo-local identifier leaks.
- **Thinking content / signatures** — replaced with neutral placeholders;
  `signature` fields are emitted as `"<redacted>"`.
- **Token counts** — rounded to clean dummy values (1000, 500, etc.) so
  they can't be cross-referenced against a real session.
- **Request `body_length`** — set to `0`; the response side carries the
  authoritative `byte_length` of the synthetic body.

Each meta file carries `"synthetic": true` to make this explicit.

If you regenerate fixtures from a fresh mitmproxy capture, you **must** scrub
the body before committing — see `scripts/dev/extract-copilot-fixtures.py`
which handles the bulk redaction (encrypted blobs, long strings) but does not
rewrite ids or placeholder text.

## Layout

```
Fixtures/Wire/
  <api>/<model>/
    req-NN.body.txt    # synthetic SSE body, structure mirrors a real capture
    req-NN.meta.json   # method, path, status, content-type, response byte length
```

`<api>` mirrors the Copilot transport surface (see `docs` and the carve-out
plan):

- `messages/`     — `/v1/messages` (Anthropic schema), Claude family
- `responses/`    — `/responses`   (OpenAI Responses schema), gpt-5.x + MAI  (NOT YET COMMITTED)
- `completions/`  — `/chat/completions`, gpt-4o-mini, gpt-4.1, Gemini, Grok   (NOT YET COMMITTED)

`responses/` and `completions/` are tracked in #810 but ship in a follow-up PR
once the redaction pipeline can shrink them below the 30 KB-per-fixture target
(raw `/responses` bodies embed a full tool catalogue and are ~1.3 MB).

## Regenerating from real captures

```pwsh
python scripts/dev/extract-copilot-fixtures.py `
  --capture-root "$HOME/captures" `
  --output tests/agent/BotNexus.Agent.Providers.Copilot.Tests/Fixtures/Wire `
  --redact-encrypted
```

The script removes encrypted blobs and truncates long strings. After running,
**manually scrub the output** before committing: replace any remaining ids
with the `msg_01Fixture…` / `toolu_01Fixture…` shapes above, and replace any
recognisable assistant text with neutral placeholder strings.

The architecture fence `PersonalPathLeakArchitectureTests` will fail the build
if any committed file contains a Windows user-home path, a Linux user-home
path, or a OneDrive segment — re-run `dotnet test` after editing fixtures.

