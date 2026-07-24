# Architecture Decision Records (ADRs)

This directory holds the Architecture Decision Records for BotNexus. An ADR captures a
single, significant architectural decision together with its context and consequences,
so that the *why* behind the codebase is discoverable and durable.

## What is an ADR?

An ADR is a short, immutable-ish document describing one decision. Once accepted, an ADR
is not rewritten to reflect a new decision — instead a **new** ADR is added that
supersedes the old one. This preserves the historical record of how the architecture
evolved.

We use a lightweight [MADR](https://adr.github.io/madr/)-style format.

## Process

1. **Copy the template** — start from [`adr-template.md`](adr-template.md).
2. **Number it** — use the next sequential 4-digit number
   (e.g. `0001-...`, `0002-...`). Numbers are never reused.
3. **Name it** — `NNNN-short-kebab-title.md`, imperative mood
   (e.g. `0001-use-vogen-for-value-objects.md`).
4. **Set the status** — `Proposed` → `Accepted` → optionally `Deprecated` or
   `Superseded by ADR-NNNN`.
5. **Open a PR** — review the decision like any other change. Merge when accepted.
6. **Never delete** — to reverse a decision, add a new ADR that supersedes it and update
   the old ADR's status.

## Index

| ADR | Title | Status |
|-----|-------|--------|
| [0000](0000-record-architecture-decisions.md) | Record architecture decisions | Accepted |

> New ADRs should be appended to this table as they are accepted.

## Deferred (follow-ups)

This first slice establishes the ADR process and the meta-ADR. Future ADRs will capture
existing decisions such as domain purity / DDD boundaries, Vogen-generated value objects,
channel-centric routing, streaming-first LLM interaction, and SQLite persistence.
