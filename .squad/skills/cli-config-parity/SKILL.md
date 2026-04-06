# CLI Config Parity

## Intent
Implement CLI commands that mutate runtime config without diverging from gateway config semantics.

## Reusable Pattern
1. Resolve paths with `PlatformConfigLoader.DefaultHomePath` and `PlatformConfigLoader.DefaultConfigPath`.
2. Ensure home scaffold via `PlatformConfigLoader.EnsureConfigDirectory(...)` (creates `sessions/`, `agents/`, `logs/`, `tokens/`, `extensions/`).
3. Load config with `PlatformConfigLoader.LoadAsync(..., validateOnLoad: false)`.
4. Apply mutation (`agents` map edits or dotted-path set).
5. Persist config JSON.
6. Re-load and validate with `PlatformConfigLoader.Validate(...)` to enforce runtime-compatible rules.
7. Return exit code `0` on success, `1` on failure.

## Notes
- Keep `--verbose` global so every command can emit trace output consistently.
- Warn (don’t silently overwrite) when `config.json` exists unless `--force` is supplied.
