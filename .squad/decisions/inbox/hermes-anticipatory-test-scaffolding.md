# Hermes Decision — Anticipatory test scaffolding strategy

## Context
Phase-5 gateway features are being implemented in parallel by other agents, but QA coverage needed to be staged immediately.

## Decision
Create expected-behavior test suites now, and mark tests with explicit `Skip` reasons when they depend on not-yet-landed runtime types or wiring.

## Rationale
- Preserves test intent and naming now, reducing integration lag when feature PRs land.
- Avoids brittle compile-time coupling to in-flight implementation classes.
- Enables selective early assertions for already-available behavior while keeping the suite green.

## Follow-up
As feature branches merge, remove `Skip` attributes and replace placeholders with concrete arrange-act-assert implementations against real types.
