# Decision: OAuth Token Resilience and Config Safety

**Date:** 2026-04-03  
**Author:** Leela (Lead)  
**Status:** Implemented  
**Commit:** `50f27fe`

## Context

Jon reported that his Copilot OAuth token had disappeared for the SECOND time, despite previous work adding backup files and audit logging to config writes. This triggered an urgent investigation into config write safety and auth resilience.

## Investigation

Initial hypothesis: Config writes were overwriting the entire file and losing provider tokens.

**Reality discovered:**
- OAuth tokens are **never stored in config.json** — they live in `~/.botnexus/tokens/{provider}.json`
- The `config.json` file only contains provider settings (API base, timeout, model, etc.)
- The `.bak` file correctly showed `"apiKey": ""` for OAuth providers (this is expected)

**Actual root cause (from logs):**
1. Token expired naturally at `2026-04-03 14:03:43Z`
2. System detected expiry and initiated device auth flow
3. Device code `7C8C-E529` presented to user (15-min timeout)
4. User didn't complete auth in time → timeout → no token
5. Subsequent API calls failed with 401, but no automatic retry/reauth

**Secondary issue:**
When `CopilotProvider.ChatCoreAsync()` received a 401/403, it only cleared the short-lived Copilot access token, not the underlying GitHub OAuth token. This created a loop where the expired OAuth token was repeatedly used.

## Decisions

### 1. Auto-Reauth on Token Expiry/Failure

**Decision:** When any auth failure occurs (401/403 from API or token exchange), the provider must:
- Clear ALL auth state (GitHub OAuth token + Copilot access token + cached tokens)
- Automatically retry authentication once
- Provide clear error messages to guide user

**Rationale:**
- Eliminates silent failures that leave the system in a broken state
- Single retry is enough to handle transient issues without spamming device auth
- Clear errors tell the user WHY they need to re-authenticate

**Implementation:**
- New method `InvalidateAndClearTokensAsync()` clears all token state
- `GetCopilotAccessTokenAsync()` now retries token exchange after clearing tokens
- Both `ChatCoreAsync()` and `ChatStreamAsync()` call this on 401/403

### 2. Surgical Config Updates

**Decision:** Config write operations must use **partial JSON updates** (JsonNode) instead of full object serialization.

**Rationale:**
- The C# `BotNexusConfig` model may not include all fields present in the JSON (provider-specific fields, future extensions)
- Serializing the entire object risks losing fields that weren't deserialized
- Even though OAuth tokens aren't in config.json, other provider-specific data might be

**Implementation:**
- `ConfigFileManager.SaveConfig()` now:
  1. Reads existing JSON as `JsonNode`
  2. Parses the new config to `JsonNode`
  3. Deep merges the new structure into existing (preserving extra fields)
  4. Writes merged result back to disk
- `MergeJsonObjects()` recursively merges nested objects

**Trade-offs:**
- Slightly more complex than `JsonSerializer.Serialize(config)`
- Defensive programming — may not be strictly necessary today, but protects against future issues
- Small performance cost (negligible for config writes)

### 3. Config Backup Files

**Decision:** Keep existing `.bak` file creation on every config write.

**Rationale:**
- Already implemented and working
- Provides rollback capability if a write corrupts the file
- Low cost (single file copy)

## Impact

- **User Experience:** Auth failures now trigger automatic retry instead of requiring manual intervention
- **Data Safety:** Config writes preserve all JSON fields, even those not in the C# model
- **Debugging:** Clear error messages tell users when and why they need to re-authenticate
- **Testing:** All 6 Copilot provider tests pass, including auth expiry scenarios

## Open Questions

None. Implementation is complete and tested.

## Related Work

- Previous auth work added backup files and logging, but didn't address auto-retry
- Farnsworth is building SystemMessage infrastructure for broadcasting device codes to UI (future enhancement)

## Lessons Learned

1. **Verify assumptions with evidence** — The "token missing from config" hypothesis was wrong. Logs revealed the actual issue.
2. **OAuth tokens are separate from config** — Important architectural detail that wasn't immediately obvious.
3. **Partial updates > full serialization** — Even when not strictly necessary today, defensive programming prevents future pain.
4. **Silent failures are UX bugs** — Auth flows must guide users to completion, not leave them in a broken state.
