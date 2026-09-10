# Documentation grooming

Grooming keeps documentation accurate and understandable. Automated checks run in continuous
integration (CI). A human or independent agent also reviews whether a reader can understand the page.
Neither part replaces the other.

Use the [Documentation standards](documentation-standards.md) for audiences, plain language, task
structure, evidence, and review authority. Apply them to changed sections and improve older pages
incrementally; a grooming pass does not certify the whole documentation set.

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
| `legacy-marker` | A "legacy" / "deprecated" / "non-functional" / "do not copy" disclosure inside a how-to section must appear **above** the code sample, not below it. | Historical: the `LlmProviderBase` sample in `extension-development.md`, disclosed as non-functional only after the fence (removed in #2862). |
| docs-vs-source trigger | A PR touching an extension manifest, a provider interface or a controller route must change a `docs/` page or state `no-docs-impact` in the body. | Extension layout drift; `IApiProvider` vs `LlmProviderBase`; `/api/exchanges/budget`. |

The first three rules check page content. The fourth is implemented by the `docs-impact` job
in the same workflow and the **Documentation impact** item in `.github/pull_request_template.md`.

**Current limit:** the workflow starts for documentation and lint-file changes, not for source-only
changes. A PR that changes only a controller, provider interface, or extension manifest will not
start this check. Reviewers must check documentation impact themselves until the workflow trigger
covers those source paths. The table describes the intended requirement, not complete enforcement.

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

<a id="what-stays-human"></a>

## What needs understanding review

The lint does not decide whether a page is useful. A human or independent agent must read the
changed page as its intended audience, using the [author and reviewer checklist](documentation-standards.md#author-and-reviewer-checklist).
Check whether the reader can identify the goal, understand the prerequisites, follow the steps,
recognize the result, and find safe help when something fails. Also check whether the page belongs
in the chosen guide and whether it contradicts its linked references.

Trace factual claims to source and record that evidence separately from steps actually executed.
Use the existing PR Validation section and identify the reviewed revision, reviewer, findings,
and remaining gaps. Follow the standard's [review and authority boundaries](documentation-standards.md#review-is-not-merge-authority)
for higher-risk instructions, protected pages, and decisions that require a human.

Link checking is separate: `npm run docs:build` (VitePress) already fails on a dead link, and that
is the gate `deploy-docs.yml` runs. Neither a build pass nor source inspection proves an end-to-end
installation works.

## The release walk

**Every release, walk `getting-started-release.md` end to end on a clean VM.**

Not a read — a walk. Download what it says to download, run what it says to run, open what it says
to open. It is the page where an error costs a user permanently, and it is exactly where this
batch's worst defect lived. No lint catches "the instructions are individually true but do not add
up to a working install".

## Related

- Issue #2865 — the gate itself
- [Documentation standards](documentation-standards.md) — shared writing and review guidance
- [PR and commit conventions](pr-and-commit-conventions.md) — the documentation check before opening a PR
