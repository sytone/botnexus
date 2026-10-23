# Agent Guidelines — Tests

## Test Folder Structure

Production projects under `src/` require an exact test mirror: `src/<category>/<Name>/<Name>.csproj` maps to `tests/<category>/<Name>.Tests/<Name>.Tests.csproj`, preserving any intermediate folders. `SrcTestsMirrorArchitectureTests` enforces this mapping, eight explicit justified source-project exemptions, and placement under a known category. Do not infer an exemption from a project's name or purpose.

No project may be added at the `tests/` root: the root-project exemption list is empty and closed. Known categories are `agent`, `domain`, `extensions`, `gateway`, `architecture`, `persistence`, `integration`, `e2e`, `scenarios`, `examples`, and `tools`. Cross-cutting tests belong in the appropriate category, not at the root. Provider conformance already lives under `agent`; `tools` covers non-`src` tooling.

The `.Tests` folder/project suffix is required for source mirrors, not a blanket description of every project under `tests/`: the existing `BotNexus.Integration.ProviderTests` name is legacy, and `BotNexus.Scenarios.Harness` is a reusable class library. These are not naming templates for new source mirrors.

Use [Test taxonomy](../docs/development/test-taxonomy.md) to choose between project-local unit tests, architecture rules, real integration seams, persistence seams, channel/browser E2E, and deterministic scenarios. This is contributor routing guidance, not new semantic enforcement.

## Assertion Library

This project uses **Shouldly** (BSD-2-Clause) for test assertions. **Do not add FluentAssertions** — it was removed due to its non-open-source license.

### Shouldly API Quick Reference

```csharp
// Equality
actual.ShouldBe(expected);
actual.ShouldNotBe(expected);

// Null
actual.ShouldBeNull();
actual.ShouldNotBeNull();

// Boolean
actual.ShouldBeTrue();
actual.ShouldBeFalse();

// String
text.ShouldContain("substring");
text.ShouldNotContain("substring");
text.ShouldStartWith("prefix");
text.ShouldEndWith("suffix");
text.ShouldBeNullOrWhiteSpace();
text.ShouldNotBeNullOrWhiteSpace();
text.ShouldMatch("regex");

// Collections
list.ShouldBeEmpty();
list.ShouldNotBeEmpty();
list.ShouldContain(item);
list.ShouldNotContain(item);
list.Count().ShouldBe(3);
list.ShouldHaveSingleItem();          // returns the item
list.ShouldAllBe(x => x.IsValid);
list.ShouldBeUnique();
list.ShouldBeInOrder(SortDirection.Ascending);

// Type checks (return the cast value)
actual.ShouldBeOfType<MyType>();       // returns MyType
actual.ShouldBeAssignableTo<IFoo>();

// Exceptions — sync
Action act = () => service.DoThing();
Should.Throw<ArgumentException>(act);
Should.Throw<ArgumentException>(act).Message.ShouldContain("param");

// Exceptions — async
Func<Task> act = async () => await service.DoThingAsync();
var ex = await Should.ThrowAsync<InvalidOperationException>(act);
ex.Message.ShouldContain("failed");

// Numeric comparisons
count.ShouldBeGreaterThan(0);
count.ShouldBeLessThanOrEqualTo(10);
value.ShouldBeInRange(1, 100);

// Dictionary
dict.ShouldContainKey("key");
dict.ShouldNotContainKey("key");
dict["key"].ShouldBe("value");
```

### Patterns to Avoid

```csharp
// DON'T — FluentAssertions syntax (removed)
actual.Should().Be(expected);
act.Should().ThrowAsync<T>().WithMessage("*text*");

// DON'T — var for lambda assertions (type inference fails)
var act = () => service.DoThing();     // won't resolve to Action
act.ShouldThrow<T>();                  // compile error

// DO — explicit Action/Func<Task> type
Action act = () => service.DoThing();
Should.Throw<T>(act);
```

### Key Differences from FluentAssertions

| FluentAssertions | Shouldly |
|---|---|
| `.Should().Be(x)` | `.ShouldBe(x)` |
| `.Should().Contain(x)` | `.ShouldContain(x)` |
| `.Should().HaveCount(n)` | `.Count().ShouldBe(n)` |
| `.Should().ContainSingle()` | `.ShouldHaveSingleItem()` |
| `.Should().BeEquivalentTo(x)` | `.ShouldBe(x)` |
| `.Should().ThrowAsync<T>()` | `await Should.ThrowAsync<T>(act)` |
| `.WithMessage("*text*")` | `.Message.ShouldContain("text")` |
| `.And.Contain("x")` | Separate assertion statement |
| `.Which.Property` | Chain directly: `.ShouldHaveSingleItem().Property` |
| `.Subject` | Use the original variable |
| `.As<T>()` | `.ShouldBeOfType<T>()` |

### Nullable bool

Use `bool?.ShouldBeTrue()` and `bool?.ShouldBeFalse()` — custom extensions are provided via `NullableBoolShouldlyExtensions.cs` (auto-included by `Directory.Build.props`).

### Collection Ordering

`ShouldBe` on collections is **order-dependent**. If order doesn't matter, sort both sides first or use individual `ShouldContain` calls.

## Test Infrastructure

### Isolation

All tests run with `BOTNEXUS_HOME` set to a temp directory via `test.runsettings` (applied automatically by `Directory.Build.props`). Tests never touch `~/.botnexus/`.

### Shared Dependencies

`Directory.Build.props` adds these to every test project automatically:
- **Shouldly 4.3.0** — assertion library
- **Global using Shouldly** — no per-file import needed
- **NullableBoolShouldlyExtensions.cs** — `bool?` assertion support

### Build Shortcuts

Run from your task worktree. Local compilation is safe; local test execution is not permitted on a host with a live gateway.

```powershell
# Compile production projects without traversing tests
dotnet build src/dirs.proj

# Preview impacted selection only; do not remove -DryRun on a live-gateway host
scripts/repo/test-impacted.ps1 -DryRun

# Normal candidate gate (remote by default)
scripts/repo/Validate-PreCommit.ps1

# Explicit remote non-browser CORE execution for this worktree
scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <worktree>

# Documentation-only gate (npm ci first if dependencies are not installed)
npm run docs:build
```

Replace `<worktree>` with the task worktree path. `src/dirs.proj` and `tests/dirs.proj` discover `.csproj` files recursively, excluding `bin` and `obj`; root `dirs.proj` composes both plus tooling. Do not maintain a separate `.slnx` project list for this traversal.

Normal code validation requires the remote non-browser core suite. Browser-emulated E2E remains quarantined under #3601; `-Mode full` and `-Mode playwright` are explicit diagnostics, not normal gates or proof that the quarantine is cleared. Documentation-only changes normally require the documentation build, not remote tests; an explicit task requirement to run remote CORE still applies. Never substitute local `dotnet test`, `test.ps1`, or executing `test-impacted.ps1` on a live-gateway host. Follow the root `AGENTS.md` for strict validation, receipts, and the explicit local fallback when remote infrastructure is genuinely unavailable.

## Test Conventions

- **xUnit** — all test projects use xUnit with `[Fact]` and `[Theory]`
- **NSubstitute** — for mocking (where needed)
- **No test skipping** — don't skip or disable tests to make the suite pass
- **No warning suppression** — fix nullable and async warnings instead of using `#nullable disable` or `#pragma warning disable`
- **Isolation** — tests must not depend on external services or user config
- **Naming** — `MethodUnderTest_Scenario_ExpectedResult`

### Asynchronous Coordination

- Prefer explicit completion/readiness signals. Use `TaskCompletionSource<T>` with `RunContinuationsAsynchronously`, and apply `Task.WaitAsync` only as a diagnostic deadline around that signal.
- Use `TestAwait.EventuallyAsync` from `BotNexus.Testing` only for a positive condition exposed by a boundary that cannot signal completion directly. It provides consistent timeout, cancellation, polling, and diagnostics.
- Never prove a negative by sleeping and observing that nothing happened. First reach a deterministic boundary, then assert synchronously, await a drain/quiescence signal, or test the invariant at a synchronous core method.
- When elapsed time is behavior, inject `TimeProvider`. Use `ManualTimeProvider` for clock reads; timer and delay behavior requires a scheduler-aware fake or an injected delay delegate.
- Do not add finite `Task.Delay` or `Thread.Sleep` calls to coordinate test execution, and do not create project-local `WaitUntilAsync`, `WaitForAsync`, `EventuallyAsync`, or `PollUntilAsync` loops.
- Infinite delays that model work until cancellation are valid sentinels.
