---
id: feature-user-documentation-review
title: "Design Review: User Documentation — MkDocs Material + Diátaxis"
type: design-review
status: complete
created: 2026-04-16
author: leela
spec: feature-user-documentation
reviewers: [leela]
decision: approved-with-conditions
---

# Design Review — User Documentation

**Ceremony**: Design Review
**Reviewer:** Leela (Lead Architect)
**Date:** 2026-04-16
**Grade:** B+

**Summary:** The concept is sound — MkDocs Material with Diátaxis is the
right call for this project. But the scope as described (20+ pages) is
wildly over-scoped for a single delivery because the existing docs are
already excellent. This is a "build the house around the furniture" job,
not a "write everything from scratch" job. Right-size to infrastructure +
migration + one polished tutorial, and defer net-new content authoring.

---

## 1. Spec Assessment

> **Note:** No `design-spec.md` file exists at
> `docs/planning/feature-user-documentation/design-spec.md`. This review is
> based on the feature description provided: MkDocs Material setup, Diátaxis
> framework, existing doc migration, and GitHub Pages deployment.

### What's Right

| Aspect | Assessment |
|---|---|
| **Tool choice** | MkDocs Material is the standard for .NET/Python project docs. Correct. |
| **Diátaxis framework** | Good fit — BotNexus already has content that naturally maps to all four quadrants. |
| **GitHub Pages deployment** | Free, zero-ops, integrates with existing GitHub workflow. Correct. |
| **Scope awareness** | Feature correctly identifies dependencies on `feature-api-documentation` and `feature-architecture-documentation` as non-blocking. |

### What Needs Adjustment

| Issue | Severity | Recommendation |
|---|---|---|
| **No written spec** | Medium | Spec should exist before implementation. For a docs feature this is less critical than a code feature, but the planning directory should have the spec for traceability. |
| **Over-scoped** | High | 20+ pages implies authoring new content. The actual work is infrastructure + migration of ~15 existing high-quality docs. |
| **MkDocs Material maintenance mode** | Low | Material is in maintenance mode with security support until Nov 2026. It's the standard today; Zensical is not yet production-ready. Pin version and move forward. |

---

## 2. Codebase Analysis

### 2.1 Infrastructure State — Starting from Zero

| Component | Exists? | Notes |
|---|---|---|
| `mkdocs.yml` | ❌ | No MkDocs configuration |
| `.github/workflows/` | ❌ | No GitHub Actions at all — not just docs-related |
| `requirements.txt` / `pyproject.toml` | ❌ | No Python dependency management |
| `docs/index.md` | ❌ | No landing page for docs site |
| `.gitignore` for `site/` | ❓ | Needs verification; MkDocs builds to `site/` |

### 2.2 Content Inventory — Surprisingly Rich

The existing docs are **substantial and well-written**. This is the single
most important finding: we are not writing docs from scratch. We are
building infrastructure around excellent existing content.

#### User-Facing Content (Diátaxis: Tutorials + How-To)

| File | Lines | Quality | Diátaxis Category |
|---|---|---|---|
| `docs/getting-started.md` | 126 | ⭐ Good routing page | Tutorial (landing) |
| `docs/getting-started-release.md` | — | Exists | Tutorial |
| `docs/getting-started-dev.md` | — | Exists | Tutorial |
| `docs/user-guide/getting-started.md` | 314 | ⭐⭐ Excellent walkthrough | Tutorial |
| `docs/user-guide/agents.md` | 762 | ⭐⭐ Comprehensive with examples | How-To |
| `docs/user-guide/configuration.md` | 24.3 KB | Large, needs review | How-To / Reference |
| `docs/user-guide/extensions.md` | 866 | ⭐⭐ Full MCP, tools, skills coverage | How-To |
| `docs/user-guide/troubleshooting.md` | 912 | ⭐⭐ Thorough, well-organized | How-To |

#### Reference Content

| File | Size | Quality | Notes |
|---|---|---|---|
| `docs/configuration.md` | 40.2 KB | ⭐ Comprehensive | Massive — may need splitting |
| `docs/api-reference.md` | — | Exists | REST + SignalR |
| `docs/cli-reference.md` | — | Exists | CLI commands |
| `docs/skills.md` | — | ⭐ Good structure | Skills system reference |
| `docs/websocket-protocol.md` | — | Exists | Protocol docs |
| `docs/botnexus-config.schema.json` | — | Exists | JSON Schema |

#### Explanation Content (Architecture / Internals)

| File | Notes |
|---|---|
| `docs/architecture/overview.md` | System design |
| `docs/architecture/domain-model.md` | DDD model |
| `docs/architecture/extension-guide.md` | Extension architecture |
| `docs/architecture/principles.md` | Design principles |
| `docs/architecture/system-flows.md` | Message flow diagrams |
| `docs/development/` (10 files) | Agent execution, DDD, LLM lifecycle, etc. |
| `docs/observability.md` | Tracing and monitoring |
| `docs/cron-and-scheduling.md` | Scheduling system |

#### Training Content

| Path | Files |
|---|---|
| `docs/training/` | 12+ files: providers, agent-core, coding-agent, tool development, glossary, etc. |

### 2.3 Content Duplication

There is **notable overlap** between top-level docs and `user-guide/` docs:

- `docs/getting-started.md` (routing page) vs. `docs/user-guide/getting-started.md` (full tutorial)
- `docs/configuration.md` (40KB reference) vs. `docs/user-guide/configuration.md` (24KB guide)
- `docs/extension-development.md` (top-level) vs. `docs/user-guide/extensions.md`

**Decision needed:** Which version becomes canonical under MkDocs? The
user-guide versions are better structured. The top-level versions are more
comprehensive. Recommendation: user-guide versions become the primary nav
entries; top-level reference docs become the Reference section.

### 2.4 README.md

The README is 306 lines with extensive documentation links. It currently uses
relative paths like `docs/getting-started.md`. After MkDocs deployment, these
should update to point at the deployed site (or keep relative for GitHub
browsing — both work since MkDocs serves from `docs/`).

**Decision:** Keep README links as relative paths. They work for both GitHub
browsing and MkDocs since source files stay in `docs/`.

---

## 3. Architectural Decisions

### AD-1: MkDocs Material Version

**Decision:** Pin `mkdocs-material==9.7.6` (latest stable).

**Rationale:** Material is in maintenance mode (security updates until Nov
2026). This is fine — it's the dominant docs framework today, the community
ecosystem is vast, and Zensical (its successor) is not production-ready.
Pin the version to avoid surprise breakage.

**Compatibility:** Must use MkDocs 1.x (not 2.0). Pin `mkdocs>=1.6,<2.0`.

### AD-2: Plugin Selection — Minimal Viable Set

Keep plugins minimal. Every plugin is a dependency that can break.

| Plugin | Include? | Rationale |
|---|---|---|
| `search` | ✅ Yes (built-in) | Essential for any docs site |
| `tags` | ✅ Yes (built-in) | Useful for cross-cutting categorization |
| `git-revision-date-localized` | ✅ Yes | Shows "last updated" — builds trust |
| `social` | ❌ Defer | Social cards are nice-to-have, not launch-critical |
| `optimize` | ❌ Defer | Premature — site is small |
| `minify` | ❌ Defer | Premature — site is small |
| `awesome-pages` | ❌ No | Unnecessary — explicit nav in mkdocs.yml is clearer |
| `mermaid2` | ❌ No | Material has native Mermaid support via SuperFences |

**requirements.txt:**
```
mkdocs>=1.6,<2.0
mkdocs-material==9.7.6
mkdocs-git-revision-date-localized-plugin>=1.4
```

### AD-3: Navigation Structure — Diátaxis Mapped to Existing Content

```yaml
nav:
  - Home: index.md
  - Getting Started:
    - Overview: getting-started.md
    - Install from Release: getting-started-release.md
    - Developer Setup: getting-started-dev.md
  - User Guide:
    - First Steps: user-guide/getting-started.md
    - Working with Agents: user-guide/agents.md
    - Configuration: user-guide/configuration.md
    - Extensions & MCP: user-guide/extensions.md
    - Skills: skills.md
    - Cron & Scheduling: cron-and-scheduling.md
    - Troubleshooting: user-guide/troubleshooting.md
  - Reference:
    - Configuration Reference: configuration.md
    - CLI Reference: cli-reference.md
    - API Reference: api-reference.md
    - WebSocket Protocol: websocket-protocol.md
    - Config Schema: botnexus-config.schema.json
  - Architecture:
    - Overview: architecture/overview.md
    - Domain Model: architecture/domain-model.md
    - Extension Guide: architecture/extension-guide.md
    - Design Principles: architecture/principles.md
    - System Flows: architecture/system-flows.md
  - Development:
    - Developer Guide: dev-guide.md
    - Agent Execution: development/agent-execution.md
    - Message Flow: development/message-flow.md
    - LLM Request Lifecycle: development/llm-request-lifecycle.md
    - Prompt Pipeline: development/prompt-pipeline.md
    - Session Stores: development/session-stores.md
    - Workspace & Memory: development/workspace-and-memory.md
    - DDD Patterns: development/ddd-patterns.md
    - Triggers & Federation: development/triggers-and-federation.md
    - WebUI Connection: development/webui-connection.md
  - Features:
    - Sub-Agent Spawning: features/sub-agent-spawning.md
```

**Key principle:** The nav follows existing file paths. We are NOT
reorganizing the file tree — only wrapping it in MkDocs navigation. This
avoids broken links, minimizes diff churn, and preserves `git blame`.

### AD-4: Landing Page (index.md)

Create `docs/index.md` as the MkDocs home page. It should be a condensed
version of the README's "Getting Started" table + a brief intro. NOT a copy
of README.md (that creates maintenance drift).

### AD-5: GitHub Actions Workflow

**Approach:** Single workflow file `.github/workflows/docs.yml` with:
- Trigger: push to `main` when `docs/**` or `mkdocs.yml` changes
- Manual dispatch for ad-hoc deploys
- Uses `actions/setup-python` + `pip install` + `mkdocs gh-deploy`
- Deploys to `gh-pages` branch

**No PR preview builds** in Wave 1. That's a nice-to-have for later (adds
complexity with artifact storage and PR comments).

### AD-6: Content Migration Strategy

| Category | Action | Effort |
|---|---|---|
| `docs/user-guide/*` (5 files) | **Migrate as-is** — quality is high | Low |
| `docs/getting-started*.md` (3 files) | **Migrate as-is** | Low |
| `docs/configuration.md` (40KB) | **Migrate as-is** — splitting is deferred | Low |
| `docs/api-reference.md` | **Migrate as-is** | Low |
| `docs/cli-reference.md` | **Migrate as-is** | Low |
| `docs/skills.md` | **Migrate as-is** | Low |
| `docs/architecture/*` (5 files) | **Migrate as-is** | Low |
| `docs/development/*` (10 files) | **Migrate as-is** | Low |
| `docs/features/*` | **Migrate as-is** | Low |
| `docs/training/*` | **Defer** — internal training material, not user-facing | None |
| `docs/planning/*` | **Exclude** — not user documentation | None |
| `docs/sample-config.json` | **Include** as downloadable asset | Low |
| New: `docs/index.md` | **Write new** — landing page | Medium |
| New: Tutorial ("Your First Agent") | **Defer to Wave 3** | High |

**Migration means:** Adding to `mkdocs.yml` nav + minor fixups (relative
link adjustments, adding any missing H1 headings required by Material).
NOT rewriting content.

### AD-7: What to Exclude from MkDocs

These directories should NOT be in the docs site:
- `docs/planning/` — internal planning artifacts
- `docs/training/` — internal training material (revisit if team wants these public)
- `docs/archive/` — archived content

Use a `.pages` file or explicit nav to exclude them. Since we use explicit
nav in `mkdocs.yml`, unlisted files are simply not shown in navigation
(they remain accessible via direct URL unless we add an exclude plugin).

---

## 4. Risk Analysis

| Risk | Severity | Mitigation |
|---|---|---|
| **Scope creep** — desire to rewrite/perfect every doc during migration | High | Strict rule: migrate as-is in Wave 1-2. Content improvements are follow-up PRs. |
| **Broken internal links** — docs have extensive cross-references | Medium | Run `mkdocs build --strict` which catches broken links. Add to CI. |
| **40KB configuration.md** — single massive file | Low | Works fine in MkDocs. Splitting is a content decision, not a technical one. Defer. |
| **MkDocs Material EOL** | Low | Maintenance mode until Nov 2026. We'll have 6+ months of support. Re-evaluate if Zensical matures. |
| **GitHub Pages not enabled** | Low | One-time repo settings change. Document in Wave 1 checklist. |
| **No Python in CI** | Low | `actions/setup-python` is trivial. No project-level Python dependency concern — this stays isolated in docs CI. |
| **Content duplication** (user-guide vs. top-level) | Medium | Both versions stay in nav. Top-level = Reference, user-guide = How-To. Clean up in future PR. |

---

## 5. Wave Plan

### Wave 1: Infrastructure + Skeleton

**Goal:** MkDocs builds locally and deploys to GitHub Pages. Navigation
structure is complete. Landing page exists.

**Assigned to:** Farnsworth (Platform Dev)

| Task | File | Details |
|---|---|---|
| Create `requirements.txt` | `requirements.txt` | Pin `mkdocs>=1.6,<2.0`, `mkdocs-material==9.7.6`, `mkdocs-git-revision-date-localized-plugin>=1.4` |
| Create `mkdocs.yml` | `mkdocs.yml` | Full configuration: site name, theme (material), palette (light/dark toggle), plugins (search, tags, git-revision-date-localized), nav tree per AD-3, markdown extensions (admonitions, code highlighting, tabs, SuperFences for Mermaid). Repo URL pointing to GitHub. |
| Create GitHub Actions workflow | `.github/workflows/docs.yml` | Trigger on push to `main` (paths: `docs/**`, `mkdocs.yml`, `requirements.txt`). Uses `actions/setup-python@v5`, pip install, `mkdocs gh-deploy --force`. Also trigger on `workflow_dispatch`. |
| Create landing page | `docs/index.md` | Brief project intro (2-3 paragraphs), feature highlights (bullet list), quick start code block, "Choose your path" links to Getting Started guides, links to key sections. Keep under 80 lines. Do NOT duplicate README. |
| Verify local build | — | Run `pip install -r requirements.txt && mkdocs serve`. Confirm all pages render, no broken links. |
| Add `site/` to `.gitignore` | `.gitignore` | Prevent MkDocs build output from being committed. |

**Kif contribution (Wave 1):** Write `docs/index.md` landing page content.
Brief, welcoming, links to the right places. Follow Material home page
patterns.

**Exit criteria:** `mkdocs build --strict` passes with zero warnings.
GitHub Actions workflow committed. Local `mkdocs serve` shows full site.

---

### Wave 2: Content Migration + Link Fixups

**Goal:** All existing user-facing docs render correctly in MkDocs. Internal
links work. Admonitions and code blocks render properly.

**Assigned to:** Kif (Docs Engineer)

| Task | Files | Details |
|---|---|---|
| Audit and fix relative links | All `docs/**/*.md` in nav | Ensure all `[text](path.md)` links resolve within MkDocs. Material expects paths relative to the file's location. Fix any that break under `mkdocs build --strict`. |
| Add/verify H1 headings | All docs in nav | Material requires a single H1 per page (used as page title). Verify each file has one. |
| Convert any raw HTML | Any affected files | MkDocs Material handles most markdown, but check for raw HTML tables or elements that might not render. |
| Add admonition syntax | Key docs (optional) | Where docs have "Note:", "Warning:", etc. as plain text, optionally convert to Material admonition syntax (`!!! note`). This is nice-to-have, not blocking. |
| Verify code block languages | All docs with code | Ensure fenced code blocks have language hints (`json`, `bash`, `csharp`, `powershell`) for syntax highlighting. Most already do. |
| Cross-link placeholders | Where referencing API docs / architecture docs | For content that depends on `feature-api-documentation` or `feature-architecture-documentation`, add placeholder text: *"See [API Documentation](api-reference.md) — detailed interactive docs coming soon."* |
| Test full build | — | `mkdocs build --strict` — zero warnings, zero broken links. |

**Scope note:** This is migration, not rewriting. If a doc has content
issues, file them as follow-up items, don't fix them in this wave.

**Exit criteria:** `mkdocs build --strict` passes. All nav items render.
All internal links resolve. Site is visually correct on `mkdocs serve`.

---

### Wave 3: Polish + First Tutorial (If Scope Allows)

**Goal:** One polished tutorial that demonstrates BotNexus end-to-end.
Site polish (custom 404, favicon, footer).

**Assigned to:** Kif (content), Farnsworth (site config)

| Task | Owner | Details |
|---|---|---|
| Custom 404 page | Farnsworth | Add `custom_dir` override with a friendly 404 page. |
| Favicon + logo | Farnsworth | Add BotNexus logo to Material theme config. Use existing assets or placeholder. |
| Footer links | Farnsworth | Add GitHub repo link, license link in Material footer config. |
| Tutorial: "Your First AI Agent" | Kif | A step-by-step tutorial walking through: install → configure provider → create agent → chat → customize prompt. This is a NET NEW doc, adapted from existing getting-started content but written as a true Diátaxis tutorial (learning-oriented, concrete steps, achievable goal). Target: ~200 lines. Place at `docs/tutorials/first-agent.md`. |
| Final review pass | Nibbler | Consistency check: tone, formatting, link integrity, nav order. |

**Exit criteria:** Site deployed to GitHub Pages. Tutorial reviewed and
approved. Nibbler sign-off on consistency.

---

### What's Deferred (NOT in this feature)

| Item | Reason |
|---|---|
| Splitting `configuration.md` (40KB) | Content decision, not migration. Needs content design. |
| Training materials on docs site | Internal content — needs decision on whether to publish. |
| PR preview deployments | Nice-to-have. Adds workflow complexity. Follow-up feature. |
| Search tuning / analytics | Premature until site has traffic. |
| Social cards plugin | Vanity feature. Defer. |
| API docs generation (OpenAPI → docs) | Depends on `feature-api-documentation`. |
| Architecture diagrams (Mermaid) | Depends on `feature-architecture-documentation`. |
| Versioned docs (`mike`) | Not needed until there are multiple releases. |
| Blog section | Not needed for a project this size. |
| Custom theme overrides | Default Material theme is fine. |
| Content rewriting / Diátaxis purism | Existing content quality is high. Don't rewrite working docs for framework purity. |

---

## 6. Open Items for User

1. **GitHub Pages enablement** — Is GitHub Pages already configured for the
   `sytone/botnexus` repo? If not, someone with admin access needs to enable
   it (Settings → Pages → Source: Deploy from a branch, Branch: `gh-pages`).

2. **Custom domain** — Is there a custom domain planned (e.g.,
   `docs.botnexus.dev`)? This affects `mkdocs.yml` `site_url` and CNAME
   configuration.

3. **Training content** — The `docs/training/` directory has 12+ files. Should
   these be included in the public docs site or remain internal? They look
   useful but may contain internal-only patterns.

4. **Spec file** — Should a formal `design-spec.md` be written retroactively
   for this feature? The planning directory exists but the spec doesn't. For
   a docs-only feature this is low-ceremony, but it breaks the pattern.

5. **README update** — After deployment, should README links point to the
   deployed docs site URL, or stay as relative paths (which work in both
   GitHub browsing and MkDocs)?

---

## 7. Appendix: Agent Assignment Summary

| Agent | Role | Waves | Model |
|---|---|---|---|
| **Farnsworth** | MkDocs config, GitHub Actions, site infrastructure | 1, 3 | gpt-5.3-codex |
| **Kif** | `index.md` content, link fixups, migration, tutorial | 1, 2, 3 | claude-haiku-4.5 |
| **Nibbler** | Post-wave consistency review | 3 | — |
| **Scribe** | Orchestration logging | All | claude-haiku-4.5 |

**Not needed:** Bender, Fry, Hermes, Amy — no code, no tests, no UI, no
design work.

---

*Review complete. The feature is well-conceived but over-scoped. Right-size
to infrastructure + migration + one tutorial. The existing docs do the heavy
lifting — respect them.*
