# Farnsworth Decision: Agent Config Writer Registration Fallback

**Date:** 2026-04-06  
**Status:** Implemented

## Context

`AgentsController` now depends on `IAgentConfigurationWriter` to persist API-driven agent changes. Some test harnesses and startup paths can register gateway services without file-backed agent configuration.

## Decision

Register `NoOpAgentConfigurationWriter` as the default writer in `AddBotNexusGateway` via `TryAddSingleton`, then replace it with `FileAgentConfigurationWriter` when `AddFileAgentConfiguration(...)` is used.

## Rationale

- Prevents runtime DI failures when no file configuration source is configured.
- Preserves backwards compatibility for harnesses that only need in-memory behavior.
- Enables persistence automatically whenever file agent configuration is active.
