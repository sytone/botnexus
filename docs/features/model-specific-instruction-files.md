# Model-Specific Instruction Files

Workspace instruction files can have **model-specific variants** selected by a filename suffix, so
one agent conversation running on GPT and another running on Claude read different instructions from
the *same* workspace — without forking the workspace or branching inside the prose.

Introduced by issue #2435.

## The grammar

```
AGENTS.md                    base — the fallback every model gets
AGENTS.gpt.md                family
AGENTS.gpt-5.md              family + major version
AGENTS.gpt-5-6.md            family + major + minor version
AGENTS.claude-opus.md        family + model
AGENTS.claude-opus-4-8.md    family + model + version
```

A variant file name is exactly three dot-separated segments: `<stem>.<suffix>.<extension>`.
The suffix must satisfy `^[a-z0-9]+(-[a-z0-9]+)*$`:

| Rule | Valid | Invalid |
|---|---|---|
| all lowercase | `AGENTS.gpt.md` | `AGENTS.GPT.md` |
| single `-` between tokens | `AGENTS.gpt-5.md` | `AGENTS.gpt--5.md` |
| no leading/trailing `-` | `AGENTS.gpt-5.md` | `AGENTS.-gpt.md`, `AGENTS.gpt-.md` |
| `.` only as a segment delimiter | `AGENTS.gpt-5.md` | `AGENTS.gpt.5.md` |
| `-` is the only separator | `AGENTS.gpt-5.md` | `AGENTS.gpt_5.md` |
| name tokens precede version tokens | `AGENTS.gpt-5.md` | `AGENTS.5-gpt.md` |
| at most a major and a minor | `AGENTS.gpt-5-6.md` | `AGENTS.gpt-5-6-7.md` |

This is **the same grammar** enforced on the `Family` and `Version` values of the
`[PromptVariant]` attribute. That is deliberate: agents author these files, and a family spelled one
way in an attribute and another way on disk would resolve differently while looking identical to a
reader. A conformance test asserts the two patterns are the same string, not merely similar ones.

Version components are parsed by `ModelFamilyVersion` — the single version parser in the tree — so a
release date stamp is never mistaken for a version. `claude-opus-4-20250514` is Opus **4.0**, and
matches `AGENTS.claude-opus-4.md`, not `AGENTS.claude-opus-4-8.md`.

## Resolution

Most specific wins; **the base file is always the final fallback.**

For a conversation running `gpt-5.6`, given all six files above, `AGENTS.gpt-5-6.md` is loaded and
the others are not. Files are not merged — exactly one file per base name reaches the prompt.

Resolution uses the **effective** model of the conversation: a conversation whose model has been
overridden reads the variant for the model actually serving the turn, not the agent descriptor's
default.

If nothing matches, the base file is used:

- a suffix the grammar rejects (`AGENTS.GPT.md`) is not a variant at all — it is an ordinary file,
  ignored by prompt assembly, and `AGENTS.md` is loaded;
- a well-formed suffix naming a different family (`AGENTS.mistral.md` on a Claude conversation)
  simply does not match;
- a version rung that does not match stops the ladder one rung early — `gpt-5.2` takes
  `AGENTS.gpt-5.md` over `AGENTS.gpt-5-6.md`.

A malformed variant never throws and never silently substitutes the wrong instructions.

## Where it applies

The default prompt file set — `AGENTS.md`, `SOUL.md`, `TOOLS.md`, `BOOTSTRAP.md`, `IDENTITY.md`,
`USER.md`, `MEMORY.md` — plus any file named in `systemPromptFiles`, and the world-level
`~/.botnexus/WORLD.md`.

Existing behaviour is preserved for variants of special files:

- `BOOTSTRAP.<suffix>.md` is consumed (deleted) on read, like the base file;
- `MEMORY.<suffix>.md` and `USER.<suffix>.md` are owner-private and are withheld from shared
  conversations, like the base files;
- `memory.promptInjection: none` suppresses `MEMORY.<suffix>.md` too.

## Ordering

A variant sorts at its **base file's** position. `AGENTS.gpt.md` occupies the slot `AGENTS.md` would
have, ahead of `SOUL.md` and `MEMORY.md`. Choosing a variant changes *which* instructions are read,
never the order of the instruction stack around them.

A file whose suffix fails the grammar is unrecognised and sorts with the other unrecognised files,
at the end — another reason a typo is visible rather than silent.

## Use variants as a last resort

Once `AGENTS.gpt.md` exists, a shared policy change must be made in N places, and one day it will
not be. Before adding a file variant:

1. **Fix the base file.** Most instructions that seem model-specific are just imprecise.
2. **Use an attribute-level overlay.** `[PromptVariant]` can reword or drop a *single* rule by id
   while everything else stays anchored to one source of truth. Built-in GPT-6 guidance uses
   `Family = "gpt", Version = "6", MatchMajorVersion = true` to cover all parsed 6.x variants;
   exact-version overlays can refine it. See the [internal prompt-variant ladder](../development/prompt-pipeline.md#attribute-declared-instruction-variants).
   This is separate from workspace filename selection: the attribute opt-in preserves existing
   exact-version semantics, while a filename such as `AGENTS.gpt-6.md` already matches a major version.
3. **Only then** add a file variant — when a whole document genuinely cannot be shared.

A variant that has drifted far from its base is a maintenance hazard: it looks current and is not.
