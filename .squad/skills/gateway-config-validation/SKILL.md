# Gateway Config Validation Pattern

## When to use
- Adding or changing Gateway platform config schema
- Needing user-facing validation feedback before startup
- Introducing new auth/channel/provider/agent config fields

## Pattern
1. Extend `PlatformConfig` with new schema fields and keep backward-compatible accessors.
2. Validate with field-path errors in `PlatformConfigLoader.Validate(...)`.
3. Fail fast on load via `OptionsValidationException` for invalid config.
4. Expose `GET /api/config/validate` returning `{ isValid, configPath, errors[] }`.
5. Keep startup wiring in `Program.cs` using `AddPlatformConfiguration(...)`.
6. For runtime updates, watch `config.json` directly with `FileSystemWatcher` + 500ms debounce and reload through `PlatformConfigLoader.Load(...)`.
7. Emit a config-changed callback/event so long-running services can react without restart.

## Notes
- Error messages should name exact field paths (e.g., `gateway.apiKeys.tenant-a.permissions`).
- Keep root-level legacy fields working while migrating to sectioned schema.
