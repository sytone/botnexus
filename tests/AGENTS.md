# Agent Guidelines — Tests

## Test Folder Structure

Test projects mirror the `src/` folder structure. Project-specific tests live under matching buckets:

```
tests/
  agent/            ← mirrors src/agent/
  domain/           ← mirrors src/domain/
  extensions/       ← mirrors src/extensions/
  gateway/          ← mirrors src/gateway/
```

Each test project folder and `.csproj` name must end in **`.Tests`** (e.g., `BotNexus.Gateway.Tests`, `BotNexus.Domain.Tests`).

Cross-cutting test projects (E2E, integration, conformance, conversation) remain at the `tests/` root until they have a clear single-bucket home.

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

```powershell
# Skip test compilation for faster production builds
dotnet build BotNexus.slnx /p:SkipTests=true
```

This uses `Directory.Build.targets` to strip all test project inputs when `SkipTests=true`.

## Test Conventions

- **xUnit** — all test projects use xUnit with `[Fact]` and `[Theory]`
- **NSubstitute** — for mocking (where needed)
- **No test skipping** — don't skip or disable tests to make the suite pass
- **No warning suppression** — fix nullable and async warnings instead of using `#nullable disable` or `#pragma warning disable`
- **Isolation** — tests must not depend on external services or user config
- **Naming** — `MethodUnderTest_Scenario_ExpectedResult`
