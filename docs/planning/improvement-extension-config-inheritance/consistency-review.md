# Consistency Review — Extension Config Inheritance

**Reviewer:** Nibbler
**Date:** 2026-04-16
**Grade:** A-

## Summary

The Extension Config Inheritance feature is well-implemented across all three waves. The merge logic is clean, correctly wired, and backward compatible. Docs accurately describe code behavior. Schema is correctly regenerated. Test coverage is solid with 10 passing tests (8 unit + 2 integration). A few minor doc-code alignment issues and test coverage gaps prevent a full A.

## Findings

### Critical (Must Fix)

None.

### Important (Should Fix)

1. **Doc claims untested "null overrides" behavior** — `extension-development.md` line 1139 states: *"Null overrides — An explicit null from an agent removes a value from the merged result."* The code does handle this correctly (a null `JsonElement` from agent replaces the world value since it's not `JsonValueKind.Object`), but there is no test verifying this behavior. Since it's documented as a merge semantic, it should have a test.

### Minor (Nice to Have)

1. **No multi-extension-ID test** — All merger unit tests use a single extension key (`"ext"`). A test where world defaults define extensions A and B, while agent overrides define B and C, would confirm the key-union logic more explicitly. The code handles it (HashSet union on line 30–31), but a test would lock it in.

2. **Spec risk item not implemented (acceptable)** — The design spec mentions under Risks: *"Config validation: Should warn if an agent overrides a key that doesn't exist in world defaults (potential typo)."* This was not implemented. Acceptable since it was a risk note, not an acceptance criterion, and can be added later.

3. **Schema `defaults` typing could be tighter** — The schema defines `defaults` as `additionalProperties: {}`, which accepts any value type per extension key. This is technically correct (extension config is arbitrary JSON), but a `description` field on the schema property would improve IntelliSense for operators. Very minor.

### Positive Observations

1. **Clean separation of concerns** — `ExtensionConfigMerger` is a pure static utility with no side effects, making it easy to test and reason about independently.

2. **Correct wiring location** — The merge call in `PlatformConfigAgentSource` (line 95–97) is placed exactly where `ExtensionConfig` is assigned to the descriptor, before validation. This means the merge happens once at load time, not on every config access.

3. **Backward compatibility is solid** — When `Defaults` is null (existing configs), `Merge()` returns a clone of agent overrides or an empty dictionary. The only behavioral change is that `ExtensionConfig` is now an empty `Dictionary` instead of `null` when both are absent — this is safe because `ResolveExtensionConfig<T>()` does a key lookup that returns the same result either way.

4. **Docs are excellent** — The "World-Level Extension Defaults" section includes a before/after example, merge semantics table, and backward compatibility note. Matches the spec's merge rules exactly.

5. **All 8 acceptance criteria met:**
   - ✅ AC1: World defaults applied to all agents (integration test confirms)
   - ✅ AC2: Deep merge with agent wins (unit + integration tests)
   - ✅ AC3: No extensions block inherits defaults (integration test: agent with no Extensions key)
   - ✅ AC4: Explicit disable with `enabled: false` (unit test)
   - ✅ AC5: Backward compatible (null handling in Merge)
   - ✅ AC6: No changes to `ResolveExtensionConfig<T>()` (verified — not touched)
   - ✅ AC7: Schema regenerated with `defaults` property
   - ✅ AC8: Unit tests cover all listed scenarios

6. **Production deserialization confirmed** — `PlatformConfigLoader` uses `PropertyNameCaseInsensitive = true`, so the lowercase `defaults` in real config.json maps correctly to the PascalCase `Defaults` C# property.

## Test Coverage Assessment

| Scenario | Unit Test | Integration Test |
|---|---|---|
| Both null → empty | ✅ `Merge_BothNull_ReturnsEmpty` | — |
| World only → world defaults | ✅ `Merge_WorldOnly_ReturnsWorldDefaults` | ✅ `LoadAsync_WithWorldDefaults_MergesIntoAgentExtensionConfig` |
| Agent only → agent config | ✅ `Merge_AgentOnly_ReturnsAgentConfig` | — |
| Both present → deep merge | ✅ `Merge_BothPresent_DeepMergesObjects` | ✅ `LoadAsync_WithWorldDefaultsAndAgentOverrides_DeepMerges` |
| Nested objects → recursive merge | ✅ `Merge_NestedObjects_MergesRecursively` | — |
| Scalar override → agent wins | ✅ `Merge_ScalarOverride_AgentWins` | — |
| Array replace → agent wins | ✅ `Merge_ArrayReplace_AgentWins` | — |
| Explicit disable (`enabled: false`) | ✅ `Merge_AgentDisablesExtension_ExplicitFalse` | — |
| Null value override | ❌ (documented but untested) | — |
| Multi-extension key union | ❌ (code handles, no test) | — |

**Coverage: 8/10 documented scenarios tested. 2 gaps identified above.**

## Recommendation

**Ship** — The implementation is correct, well-tested, and backward compatible. The two gaps (null override test, multi-key test) are low-risk since the code paths are exercised indirectly. They can be added in a follow-up pass if desired.
