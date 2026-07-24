# 0000. Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-04
- **Deciders:** BotNexus maintainers

## Context and Problem Statement

BotNexus is an evolving, domain-driven multi-agent platform with a number of deliberate
architectural choices (domain purity, library-backed composition, channel-centric
routing, streaming-first interaction, Vogen value objects). These decisions are currently
scattered across code, `AGENTS.md` convention files, and prose docs. New contributors
lack a single, durable record of *why* the architecture looks the way it does, and there
is no agreed lightweight process for capturing future decisions.

## Decision

We will record architecturally significant decisions as **Architecture Decision Records
(ADRs)**, stored as Markdown files under `docs/architecture/adr/`.

- ADRs use a lightweight [MADR](https://adr.github.io/madr/)-style format
  (see [`adr-template.md`](adr-template.md)).
- Each ADR is numbered sequentially (`NNNN-short-kebab-title.md`); numbers are never
  reused.
- ADRs are immutable once accepted. To change a decision, we add a **new** ADR that
  supersedes the old one and update the superseded ADR's status.
- Every ADR carries a status: `Proposed`, `Accepted`, `Deprecated`, or
  `Superseded by ADR-NNNN`.
- New ADRs are reviewed and merged through the normal PR process.

This is the meta-decision that establishes the ADR practice itself.

## Consequences

**Positive**

- The rationale behind architectural choices becomes discoverable and durable.
- New contributors can quickly understand why the system is shaped as it is.
- Decisions are reviewed deliberately and versioned in git alongside the code.

**Negative / costs**

- Contributors must invest a small amount of effort to write an ADR for significant
  decisions.
- The index in [`README.md`](README.md) must be kept in sync as ADRs are added.

## References

- Michael Nygard, *Documenting Architecture Decisions* (2011).
- [MADR — Markdown Architecture Decision Records](https://adr.github.io/madr/).
- [arc42 architecture overview](../README.md).
