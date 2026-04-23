# Hermes — Issue #12 agents.defaults test coverage

**Date:** 2026-04-22
**Status:** Complete
**Branch:** `test/issue-12-agent-defaults-hermes`

## What I added

### 1. `tests/BotNexus.Gateway.Tests/Configuration/AgentConfigMergerTests.cs`
Comprehensive merge-contract coverage for `agents.defaults`:
- memory full inherit
- memory partial override
- memory explicit `false`
- heartbeat partial override
- fileAccess list replacement
- fileAccess partial inheritance
- toolIds inherit when omitted
- toolIds full replacement when set
- explicit empty toolIds list replaces defaults
- null-default passthrough behavior
- direct presence-aware merge helper coverage for memory/heartbeat

### 2. `tests/BotNexus.Gateway.Tests/PlatformConfigAgentSourceTests.cs`
Integration coverage for descriptor loading:
- backward compatibility when no `agents.defaults` exists
- reserved `defaults` key is not emitted as an `AgentDescriptor`
- memory inheritance flows into loaded `AgentDescriptor`
- toolIds inheritance flows into loaded `AgentDescriptor`

### 3. `tests/BotNexus.Gateway.Tests/PlatformConfigurationTests.cs`
Loader/validation coverage:
- invalid `agents.defaults.memory.indexing` reports exact field path
- invalid `agents.defaults.heartbeat.intervalMinutes` reports exact field path
- invalid `agents.defaults.fileAccess.allowedReadPaths[1]` reports exact field path/index
- valid `agents.defaults` returns no validation errors
- `CronConfig` default remains `Enabled = true`
- `ExtractAgentDefaults` extracts defaults block and strips reserved key
- `ExtractAgentDefaults` no-op behavior when no defaults block exists

### 4. `tests/BotNexus.Gateway.Tests/Cli/InitCommandTests.cs`
Scaffold coverage:
- init scaffold emits `cron.enabled = true`
- init scaffold emits `agents.defaults.memory.enabled = true`

## Test result

Ran:

```bash
dotnet test tests/BotNexus.Gateway.Tests --nologo --tl:off
```

Result:
- **Passed:** 987
- **Failed:** 0
- **Skipped:** 0

## Gaps / findings

- Farnsworth’s merge implementation appears present enough for the contract tests to pass.
- I hit one compile issue in `PlatformConfigAgentSource` around nullable `JsonElement` handling for raw agent elements; corrected it so tests could run.
- I did **not** add effective-config API coverage here; that is Bender’s/API-side contract and can land with endpoint implementation.
- I did **not** add a direct “config file without cron block resolves to enabled=true after full load” integration case because the current model-level default is already covered and scaffold emission is now covered separately.

## Notes for merge

- Keep `scripts/botnexus-sync.sh` out of this test commit; unrelated local file.
- Branch is safe to cherry-pick or merge on top of Farnsworth’s feature branch.
