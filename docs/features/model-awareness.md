# Model Awareness

An agent cannot self-detect that its prose is shaped by its own model family, because that shape is
simply what "clear" looks like from inside its own context. Left alone, an agent editing `AGENTS.md`
writes rules that are true only for itself into the file every model reads.

Two pieces address that, introduced by issue #2436 under epic #2431:

- the **`model-awareness` prompt section**, which tells the agent its instructions were *resolved*
  for it rather than written universally, and
- the **`model_profile` tool**, which lets it check that claim against data before it edits anything.

Neither works alone. The instruction without the tool leaves the agent guessing what variants exist;
the tool without the instruction gives it no reason to call it.

## The `model-awareness` section

Rendered into the system prompt as `<model_awareness>`, immediately after `<model_guidance>`, so the
"here is how you behave" rules and the "here is why those rules are yours specifically" framing read
as one block.

Its default rung tells every agent that:

- it is one of several families the platform serves, and its instructions were resolved for it;
- instructions resolve on a specificity ladder — default, family, family + version — where a more
  specific rung *overlays* the one beneath it;
- a base instruction file is the **contract** and a variant is the **dialect**, so a base-file edit
  must be consciously classified as agnostic or model-specific;
- `model_profile` is how that classification is answered from data;
- variant files are named `<stem>.<suffix>.<ext>`, and a name the grammar rejects is silently never
  read.

The section is itself declared with `[PromptVariant]` and resolved through the startup-frozen
`PromptVariantRegistry`. That is not merely symmetry: a warning about family-shaped prose that could
not itself be worded differently per family would be asserting something it does not practise. The
Claude rung, for example, adds the specific failure mode that produced this epic — layered rationale
and long motivating preamble read as plain good writing from inside a Claude context.

Because the default rung is mandatory, an unrecognised model gets the conservative default rather
than silence.

## The `model_profile` tool

Always available; never gated. It reports four things.

**Identity** — the model id, provider, detected family, and the parsed version. Version parsing goes
through `ModelFamilyVersion`, the single version parser in the tree, so a release date stamp is never
mistaken for a version. An id that carries no parseable version is reported as such rather than
defaulted to zero.

**Declared provider capabilities** — the `ProviderCapabilities` record from issue #2432, as
*declared* by the provider rather than probed. This is where the capability contract becomes
actionable: an agent can learn that its transport frames streamed deltas with CRLF, or that no
leaked-tool-call recovery applies, before relying on the behaviour instead of after reading a
failure.

**Resolved variant rungs** — for each laddered section, the rungs this turn actually climbs, e.g.
`model-guidance: default -> claude`. A rung declared for another family is not listed: an agent that
believed it inherited another family's overlay would draw exactly the wrong conclusion about what is
agnostic.

**Existing variant files** — which model-specific instruction files exist in the workspace, which of
them match the active model, and which file wins for this turn. Absence is stated explicitly, because
an empty section reads as "the scan failed" and pushes the agent back to guessing.

The output closes with the filename grammar, taken from the same `ContextFileVariants` parser that
enforces it, so the documentation cannot drift from the rule.

Pass `baseFile` to scope the scan to one file:

```json
{ "baseFile": "SOUL.md" }
```

## Using it

Before editing `AGENTS.md`, `SOUL.md` or `WORLD.md`, call `model_profile` and ask the question the
section poses: *is this rule true for every model, or only for the one I am running on?*

- True for everyone → edit the base file.
- True only for your family → prefer an attribute-level `[PromptVariant]` overlay, which can reword
  or drop a single rule by id while everything else stays anchored to one source of truth.
- Only when a whole document genuinely cannot be shared → add a file variant. See
  [Model-Specific Instruction Files](/features/model-specific-instruction-files) for the grammar and
  the reasons to treat file variants as a last resort.
