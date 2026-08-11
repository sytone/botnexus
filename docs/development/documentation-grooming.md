# Documentation grooming

Grooming keeps the docset true. Half of it is mechanical and runs in CI; half of it is a person
reading prose. This page says which is which, so neither half assumes the other covered it.

## Why this exists

On 2026-08-07 one writer read the docset cold and found twelve defects. None required running code
or special access. Several were two statements contradicting each other **on the same page**. One
sent every new user to `http://localhost:18790` on `getting-started-release.md` — the
highest-traffic page in the docset — while `GatewayBindAddress.LoopbackListenUrl` declares
`http://localhost:5005`.

Every one of them survived grooming, because grooming was entirely a human read. A human read
reliably catches tone, structure and readability, and reliably glides straight past a wrong port
number in the third code fence of a long page. Issue #2865 added the mechanical half.

## What is automated

`scripts/repo/docs-lint.ps1`. One command, runnable locally and in CI:

```powershell
pwsh -NoProfile -File scripts/repo/docs-lint.ps1
```

Exit code `0` is clean, `1` is findings, `2` is a usage error (including the lint's own
anti-vacuity floor — it refuses to certify a docset it barely read). Add `-AsJson` for a
machine-readable result on pure stdout, or `-Rule <name>` to run one rule.

CI runs it from `.github/workflows/docs-lint.yml` on any change to `docs/**` **or** to the lint's
own inputs. That second trigger matters: a change to the fact registry or the allow-list can
silently disarm the gate, so it is gated too.

| Rule | What it asserts | Defect it exists to prevent |
| --- | --- | --- |
| `literal-drift` | A port or config key instructed inside a fenced code block in `docs/**` must appear somewhere in `src/**`. A literal that lives only in docs is stale by definition. | `localhost:18790` on `getting-started-release.md`; the `BotNexus.Cron.Jobs` config key the binder never read. |
| `intra-page-contradiction` | For a registry of high-value facts, one page states at most one distinct value. **Hard failure, not a warning** — a reader cannot tell which half is true, so both halves are worthless. | `cron-and-scheduling.md` giving `tickIntervalSeconds` as 60 in a table and 10 in a diagram. |
| `legacy-marker` | A "legacy" / "deprecated" / "non-functional" / "do not copy" disclosure inside a how-to section must appear **above** the code sample, not below it. | The `LlmProviderBase` sample in `extension-development.md`, disclosed as non-functional only after the fence. |
| docs-vs-source trigger | A PR touching an extension manifest, a provider interface or a controller route must change a `docs/` page or state `no-docs-impact` in the body. | Extension layout drift; `IApiProvider` vs `LlmProviderBase`; `/api/exchanges/budget`. |

The first three are content rules in the lint script. The fourth is not a content rule and lives
where it belongs: the `docs-impact` job in the same workflow, plus the **Documentation impact**
checklist item in `.github/pull_request_template.md`.

### Tuning the rules

- **Fact registry** — `scripts/repo/docs-lint-facts.json`. Each entry carries an `id`, the `defect`
  it prevents, and a regex with a named `value` group. Add a fact when a page states a
  documented default, a port, an enum value list or a config path that a future edit could
  contradict.
- **Allow-list** — `scripts/repo/docs-lint-allow.json`. For literals that are legitimately absent
  from `src/`: Jaeger's `16686`, Ollama's `11434`, an OTLP collector's `4317`. **Every entry must
  carry a written justification.** An unexplained suppression is how a gate gets hollowed out one
  entry at a time.
- A line that *sets* a value (`botnexus config set gateway.listenUrl http://localhost:8080`,
  `export BotNexus__...=`) is a demonstration, not an assertion about the system, and is excluded
  from both rule 1 and rule 2. The motivating defect was a *browse* instruction with no assignment,
  so it is still caught.

### Proving the lint still works

A lint that cannot fail on the defect that motivated it is decoration. `DocsLintScriptTests`
(in `BotNexus.Architecture.Tests`) pins each rule against a fixture reproducing its motivating
defect **and** against the corrected form, so the gate can neither pass vacuously nor flag
everything. If you change a detector, re-prove it by mutation: inject the known-bad literal and
the known contradiction, confirm the gate goes red naming that rule, then restore and confirm the
baseline is green again.

## What stays human

The lint has no opinion about whether a page is any good. Grooming still owns:

- prose quality, tone, and voice
- structure — is the reader told things in an order that works
- readability, and whether an explanation actually explains
- whether a page should exist at all, and whether something is missing
- accuracy of anything not reducible to a literal

Link checking is not in scope here either: `npm run docs:build` (VitePress) already fails on a dead
link, and that is the gate `deploy-docs.yml` runs.

## The release walk

**Every release, walk `getting-started-release.md` end to end on a clean VM.**

Not a read — a walk. Download what it says to download, run what it says to run, open what it says
to open. It is the page where an error costs a user permanently, and it is exactly where this
batch's worst defect lived. No lint catches "the instructions are individually true but do not add
up to a working install".

## Related

- Issue #2865 — the gate itself
- `docs/development/pr-and-commit-conventions.md` — the documentation check before opening a PR
