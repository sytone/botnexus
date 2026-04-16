# Consistency Review — Extension-Contributed Commands

**Reviewer:** Nibbler
**Date:** 2025-07-24
**Grade:** B+

## Summary

The Extension-Contributed Commands feature is a well-architected, cohesive delivery across four waves. The contracts are clean, the registry pattern is solid, and the WebUI integration is thoughtful. Two bugs need attention before this ships — a case-sensitivity gap in `BuiltInCommandContributor` and a DI registration issue that will silently drop extension-contributed commands — but neither required a rework. Documentation drifted from the implementation in several places.

## Findings

### Critical (Must Fix)

#### 1. Extension ICommandContributor registrations silently dropped (DI bug)

**Files:** `AssemblyLoadContextExtensionLoader.cs:328-337`, `GatewayServiceCollectionExtensions.cs:126`

`CommandRegistry` takes `IEnumerable<ICommandContributor>`, expecting multiple contributors. The built-in registration correctly uses `TryAddEnumerable`:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandContributor, BuiltInCommandContributor>());
```

But when the extension loader discovers an `ICommandContributor` implementation in an extension assembly, it falls into the `else` branch of `RegisterServices`:

```csharp
// Line 336 — only fires for contracts NOT in the explicit list
_services.TryAddSingleton(contract, implementation);
```

`TryAddSingleton` registers a **single** implementation and silently skips subsequent ones. This means only the first extension to contribute commands will be registered; all others are silently ignored. `ICommandContributor` must be added to the `AddSingleton` branch alongside `IChannelAdapter`, `IIsolationStrategy`, and `IAgentTool`:

```csharp
if (contract == typeof(IChannelAdapter) ||
    contract == typeof(IIsolationStrategy) ||
    contract == typeof(IAgentTool) ||
    contract == typeof(ICommandContributor))   // ← add this
{
    _services.AddSingleton(contract, implementation);
}
```

**Impact:** Any project with two or more command-contributing extensions will silently lose all but the first. This is the core extensibility promise of this feature and it doesn't work for extensions.

#### 2. BuiltInCommandContributor case-sensitivity bug

**File:** `BuiltInCommandContributor.cs:61-74`

`CommandRegistry` uses a case-insensitive `FrozenDictionary` for lookup but passes the **original-case** command name to the contributor's `ExecuteAsync`. The built-in contributor's switch expression routes via `NormalizeCommand(commandName)` which only trims — it does not lowercase:

```csharp
private static string NormalizeCommand(string commandName)
    => commandName.Trim();  // ← no case normalization
```

If a user types `/HELP` or `/Help`, the registry resolves it (case-insensitive), but the switch expression compares against lowercase literals like `"/help"` and falls through to the "Command Not Found" default branch.

By contrast, `SkillsCommandContributor` correctly uses `string.Equals(commandName, "/skills", StringComparison.OrdinalIgnoreCase)`. The built-in contributor should do the same — either lowercase in `NormalizeCommand` or use `StringComparison.OrdinalIgnoreCase` in the switch.

### Important (Should Fix)

#### 3. API documentation descriptions diverge from code

**Files:** `BuiltInCommandContributor.cs`, `SkillsCommandContributor.cs`, `api-reference.md:757-809`

Multiple command descriptions in the API reference sample response don't match actual code values:

| Command | Code (`CommandDescriptor.Description`) | API Doc Sample |
|---------|---------------------------------------|----------------|
| `/help` | "List all available commands." | "Show available commands" |
| `/agents` | "List registered agents and their models." | "List available agents" |
| `/reset` | "Reset the current chat (client-side only)." | "Clear chat and reset current session" |
| `/skills` | "Manage discovered skills for the active session." | "Manage skills for the current agent" |

The API docs show clients what they'll see, so they should match the actual serialized output.

#### 4. API docs show wrong category for /skills

**Files:** `SkillsCommandContributor.cs:15`, `api-reference.md:777`

Code has `Category = "Skills"`, but the API reference shows `"category": "Extension"`. Clients using the `category` field for grouping will get `"Skills"` from the live API but the docs say `"Extension"`.

#### 5. API docs omit /status and /new from sample response

**File:** `api-reference.md:757-809`

The `GET /api/commands` sample response shows `/help`, `/agents`, `/skills`, `/reset` but omits `/status` and `/new`. Both are real commands in the built-in contributor. The sample should include them or note that the list is illustrative.

#### 6. Sub-command descriptions diverge between code and docs

The `/skills` sub-command descriptions in the API reference sample don't match the code:

| Sub-command | Code | API Doc |
|-------------|------|---------|
| `list` | "Show discovered skills by status." | "Show loaded, available, and denied skills" |
| `info` | "Show metadata for a skill." | "Show skill metadata and size" |
| `add` | "Load a skill into this session." | "Load a skill into the current session" |
| `remove` | "Unload a skill from this session." | "Unload a skill from the current session" |
| `reload` | "Re-discover skills from disk." | "Re-discover skills from disk" |

These are minor but when docs are the contract clients code against, they should be accurate.

#### 7. Test helper code duplicated across three test files

**Files:** `CommandRegistryTests.cs`, `BuiltInCommandContributorTests.cs`, `CommandsControllerTests.cs`, `SkillsCommandContributorTests.cs`

The reflection-based test infrastructure (`CreateInstance`, `TryBuildArguments`, `TryCreateDefault`, `ResolveType`) is copy-pasted nearly identically across `BuiltInCommandContributorTests`, `CommandsControllerTests`, and `SkillsCommandContributorTests`. This is ~100 lines duplicated per file. Extract to a shared `TestConstructorHelper` or similar in a test utilities assembly.

### Minor (Nice to Have)

#### 8. WebUI fallback commands mark /new as clientSideOnly

**File:** `chat.js:783-787`

The FALLBACK_COMMANDS array sets `/new` as `clientSideOnly: true`, but the backend descriptor does not. When the backend is reachable (normal case), `loadCommands()` replaces fallbacks, so this is only relevant in offline/degraded mode. The degraded behavior is reasonable (client-side reset without session sealing), but the mismatch may confuse future maintainers. A comment explaining the intentional fallback degradation would help.

#### 9. CommandsControllerTests tests NonAction wrapper instead of HTTP action

**File:** `CommandsControllerTests.cs:20-21`

The first test resolves `GetCommands` (marked `[NonAction]`) via reflection instead of `List` (the actual `[HttpGet]` action). Both methods return the same result, but the test is exercising dead code from an HTTP perspective. Should test `List()` directly.

#### 10. /reset returns IsError = true by design — document why

**File:** `BuiltInCommandContributor.cs:207-212`

`ClientSideOnlyCommandResult()` returns `IsError = true`, which is a reasonable server-side signal that execution wasn't performed. However, this means a raw API call to `POST /api/commands/execute` with `/reset` returns a success HTTP status (200) but `isError: true`. This is correct behavior but worth a comment in the code or a note in the API docs explaining the semantics.

### Positive Observations

1. **Clean contract design.** The `ICommandContributor` → `CommandDescriptor` → `CommandResult` model is simple, well-documented, and easy to implement. The records are immutable with `required` properties — good use of modern C#.

2. **Defensive registry.** `CommandRegistry` handles empty input, unknown commands, and contributor exceptions gracefully — always returning a `CommandResult` rather than throwing. The `FrozenDictionary` for O(1) lookup with case-insensitive keys is a good performance choice.

3. **Smart parsing.** The `ParseRawInput` tokenizer handles edge cases (empty input, no slash prefix, sub-commands, arguments) cleanly. The "first token starting with `/`" strategy is simple and effective.

4. **WebUI progressive enhancement.** The fallback commands + backend fetch + dynamic palette is well-layered. The sub-command expansion UX (typing `/skills ` shows sub-commands) is a nice touch. Keyboard navigation (↑↓, Tab, Enter, Esc) is complete.

5. **SkillsCommandContributor is a strong reference implementation.** It demonstrates sub-commands with arguments, session tool resolution, async operations, error handling, and snapshot diffing for reload. Third-party extension authors can follow this pattern confidently.

6. **Extension discovery correctly updated.** Both `DiscoverableServiceContracts` and the manifest `allowedTypes` include `"command"`. The extension.json for skills correctly declares `["tool", "command"]`.

7. **Test coverage is solid.** 33 tests across the feature covering happy paths, error paths, edge cases (duplicate commands, empty input, unknown commands, denied skills, budget exceeded). The `StubContributor` pattern in `CommandRegistryTests` is clean and reusable.

## Cross-Wave Integration Assessment

The four waves integrate well. The contracts (Wave 1) provide a clean seam that the built-in commands (Wave 2), WebUI palette (Wave 3), and skills extension (Wave 4) all plug into naturally. The data flow is coherent:

- **Discovery:** Extension loader scans assemblies → `ICommandContributor` impls registered in DI → `CommandRegistry` aggregates on construction → `GET /api/commands` exposes to clients → WebUI fetches and populates palette.
- **Execution:** User selects command → WebUI routes client-side or posts to API → `CommandsController` validates and delegates → `CommandRegistry` parses and dispatches → contributor executes → result rendered in WebUI.

The one seam that needs attention is the DI registration path for extension-contributed commands (Finding #1). The built-in path works perfectly; the extension path has a registration bug that would make the whole extension model fail silently for its headline use case.

The `CommandExecutionContext.ResolveSessionTool` delegate is a clever way to bridge the command system with session-scoped tools (like SkillTool) without coupling the contracts to the isolation layer. The `IAgentHandleInspector` addition is cleanly scoped and only used where needed.

## Test Coverage Assessment

| Component | Tests | Coverage Quality |
|-----------|-------|-----------------|
| CommandRegistry | 10 | **Excellent.** Empty, single, multiple contributors, duplicate handling, sub-command parsing, argument parsing, unknown commands, empty input, exception handling. |
| BuiltInCommandContributor | 7 | **Good.** All 5 commands tested, plus unknown command and clientSideOnly verification. Missing: `/new` with valid session context (only tests missing AgentId path). |
| CommandsController | 5 | **Adequate.** GET, valid POST, empty input, unknown command, null input. Missing: POST with valid agentId/sessionId that exercises `BuildSessionToolResolver`. |
| SkillsCommandContributor | 11 | **Excellent.** All 5 sub-commands tested with happy + error paths. Includes denied skill, already loaded, budget exceeded, default-to-list. |

**Gaps:**
- No integration test exercising the full `POST /api/commands/execute` → `CommandRegistry` → `SkillsCommandContributor` flow with a real session context
- No test for case-insensitive command execution (would have caught Finding #2)
- `CommandsControllerTests` test the `[NonAction]` method instead of `[HttpGet] List()` for the GET endpoint
- No test for the extension loader registering an `ICommandContributor` from an assembly (would have caught Finding #1)

## Recommendation

**Fix-then-ship.** Fix the two critical issues:

1. Add `ICommandContributor` to the `AddSingleton` branch in `AssemblyLoadContextExtensionLoader.RegisterServices` (Finding #1)
2. Add case-insensitive comparison in `BuiltInCommandContributor.NormalizeCommand` or switch arms (Finding #2)

Then update the API reference descriptions to match the code (Findings #3–6). The test helper duplication (Finding #7) can be addressed in a follow-up cleanup.
