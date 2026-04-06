# Farnsworth Decision: Config Schema + Path Resolver Layering

**Date:** 2026-04-06  
**Owner:** Farnsworth

## Decision

1. Keep config path traversal as a gateway abstraction (`IConfigPathResolver`) implemented in `BotNexus.Gateway` and consumed by CLI through DI, instead of keeping reflection utilities in `Program.cs`.
2. Validate platform config JSON against generated schema at load time **before** manual semantic validation, but normalize incoming JSON property names to camelCase first so legacy PascalCase config files remain valid.
3. Expose schema generation via CLI command (`botnexus config schema --output ...`) and commit generated schema to `docs/botnexus-config.schema.json`.

## Why

- Removes non-testable reflection plumbing from CLI and centralizes it in reusable platform service.
- Preserves backward compatibility for existing config files while still enforcing structural schema checks.
- Gives users and tooling a deterministic way to regenerate the published schema artifact.
