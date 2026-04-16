# DDD Patterns Guide

**Version:** 1.0  
**Last Updated:** 2026-04-12  
**Status:** Phase 1 — Foundation

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Value Object Pattern](#2-value-object-pattern)
   - [When to Use](#when-to-use-value-objects)
   - [Code Pattern](#code-pattern)
   - [JsonConverter Implementation](#jsonconverter-implementation)
   - [Conversion Semantics](#conversion-semantics)
   - [Dos and Don'ts](#dos-and-donts)
3. [Smart Enum Pattern](#3-smart-enum-pattern)
   - [When to Use](#when-to-use-smart-enums)
   - [Code Pattern](#code-pattern-1)
   - [Registry-Based Extensibility](#registry-based-extensibility)
   - [Thread Safety](#thread-safety)
   - [Extension Registration](#extension-registration)
4. [Migration Guide](#4-migration-guide)
5. [Type Catalog](#5-type-catalog)
6. [Related Documentation](#related-documentation)

---

## 1. Introduction

BotNexus is adopting Domain-Driven Design (DDD) patterns to solve three critical problems:

### Problem: Primitive Obsession
The codebase uses raw strings for domain concepts—`AgentId = "test-agent"`, `ChannelType = "signalr"`, `Role = "user"`. This makes it easy to:
- Accidentally swap arguments (`SenderId` and `ReceiverId` both strings)
- Pass invalid values (empty strings, whitespace)
- Lose domain intent (what does this string represent?)

### Problem: Lack of Type Safety
Domain logic scattered across 13 projects without shared vocabulary. Session stores normalize keys differently. Each project reimplements string validation.

### Problem: Domain Language Misalignment
Code doesn't speak the domain. The older codebase used `SessionStatus.Closed` when it meant "sealed by expiration" — the name suggested deletion when the intent was permanent archival (now corrected to `SessionStatus.Sealed`). Identity generation for sub-agents uses string concatenation (`"agent::subagent::123"`) instead of a first-class concept.

### Solution: Value Objects and Smart Enums
- **Value Objects** — Stack-allocated, type-safe wrappers around validated primitives (AgentId, SessionId, ChannelKey)
- **Smart Enums** — Extensible class-based enumerations that support custom values registered at runtime (MessageRole, SessionType)

These patterns give BotNexus:
- **Type Safety** — Compile errors for swapped arguments
- **Validation** — Rules enforced at construction time
- **Domain Clarity** — Code reads like the domain, not the implementation
- **Backward Compatibility** — Implicit string conversions let existing code compile without changes

---

## 2. Value Object Pattern

### When to Use Value Objects

Use a value object when:
- The value represents a **domain concept** with meaning (not just any string)
- The value has **validation rules** (non-empty, specific format, normalized)
- Multiple places in the code use the value, risking **argument confusion** (swap bugs)
- The value **travels across layer boundaries** (serialized to JSON/SQLite)

**High-ROI value objects:**
- **AgentId** — Every agent reference. 100+ usages. P0.
- **SessionId** — Every session reference. Needs factory methods for sub-agents. P0.
- **ChannelKey** — Eliminates `NormalizeChannelKey()` duplication x5. P0.
- **ConversationId**, **SenderId** — Prevent accidental swap bugs. P1.
- **ToolName** — Case-insensitive equality, everywhere tool names are referenced. P1.

### Code Pattern

```csharp
using System.Text.Json.Serialization;

// Recommended pattern: readonly record struct with string backing
[JsonConverter(typeof(AgentIdJsonConverter))]
public readonly record struct AgentId(string Value) : IComparable<AgentId>
{
    // Factory method with validation
    public static AgentId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("AgentId cannot be empty", nameof(value))
            : new(value.Trim());

    // Implicit conversion for backward compatibility
    // Use this when existing code passes string and you want it to compile without changes
    public static implicit operator string(AgentId id) => id.Value;
    
    // Explicit conversion for type-safe migration
    // Use this when you want callers to opt-in to the new type
    public static explicit operator AgentId(string value) => From(value);

    public override string ToString() => Value;
    
    // Comparison support (optional, but recommended for identifiers)
    public int CompareTo(AgentId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
}
```

**Key components:**
- `readonly record struct` — Stack-allocated, immutable, compiler-generated equality
- `[JsonConverter(...)]` — Controls JSON serialization
- `From()` factory method — Enforces validation
- Operators — Control implicit vs explicit conversion semantics

### JsonConverter Implementation

All value objects need JSON serialization (REST APIs, config files, SQLite storage). Implement a generic converter:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class AgentIdJsonConverter : JsonConverter<AgentId>
{
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value == null ? throw new JsonException("AgentId cannot be null") : AgentId.From(value);
    }

    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
```

**Pattern notes:**
- Converters must be registered in `JsonSerializerOptions` or marked with `[JsonConverter]` on the type
- Read() should call the value object's `From()` factory to enforce validation during deserialization
- Write() simply outputs the string value
- The converter handles the round-trip: string ↔ JSON ↔ value object

### Conversion Semantics

**Implicit Conversion** (use during migration phase):
```csharp
// Old code: AgentId as string
string agentId = "my-agent";

// With implicit operator:
AgentId id = new AgentId(agentId);  // Compiles without change

// Reverse:
string result = id;  // Implicit to string, returns "my-agent"
```

When to use implicit:
- Existing tests use string patterns (`AgentId = "test-agent"`)
- APIs still accept string parameters
- Allows ~2,000 tests to compile without changes
- Gradually migrate to explicit conversion over phases

**Explicit Conversion** (use for new code):
```csharp
// New code: type-safe from the start
var id = (AgentId)"my-agent";  // Requires explicit cast, more visible
```

When to use explicit:
- New code paths should declare intent explicitly
- Prevents accidental string → value object conversions
- Makes call sites easier to audit in code review

### Dos and Don'ts

✅ **DO:**
- Validate in the `From()` factory method, not in the constructor
- Use `readonly record struct` for value objects (stack allocation, immutability)
- Implement comparison operators (`IComparable<T>`) for identifiers
- Return the same immutable instance from conversion operators
- Round-trip serialize → deserialize and verify the result equals the original

❌ **DON'T:**
- Add business logic to value objects (they're data carriers with validation, not entities)
- Use mutable collections inside value objects
- Implement `SetValue()` or other mutators
- Forget to implement `IEquatable<T>` if you override `Equals()`
- Omit `[JsonConverter]` attributes; JSON will fail to round-trip

---

## 3. Smart Enum Pattern

### When to Use Smart Enums

Use a smart enum when:
- The value is a **discriminator** with known variants (but might be extended)
- The value needs **serialization** (JSON, config files)
- The value might be extended by **plugins or extensions** without modifying core code
- The value has **semantic meaning** (not just any string)

**Smart enum candidates:**
- **MessageRole** — User, Assistant, System, Tool (+ extensions can add custom roles)
- **SessionStatus** — Active, Suspended, Sealed (+ lifecycle variants)
- **SessionType** — UserAgent, WorkerAgent, SystemAgent (+ custom archetypes)
- **ExecutionStrategy** — Sequential, Parallel, PriorityQueue (+ domain-specific strategies)

### Code Pattern

```csharp
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

[JsonConverter(typeof(MessageRoleJsonConverter))]
public sealed class MessageRole : IEquatable<MessageRole>
{
    // Static instances for known values
    public static readonly MessageRole User = new("user");
    public static readonly MessageRole Assistant = new("assistant");
    public static readonly MessageRole System = new("system");
    public static readonly MessageRole Tool = new("tool");

    // Registry for extensibility: maps string → instance (case-insensitive)
    private static readonly ConcurrentDictionary<string, MessageRole> _registry = 
        new(StringComparer.OrdinalIgnoreCase);

    public string Value { get; }

    // Private constructor — only called during static initialization and FromString()
    private MessageRole(string value)
    {
        Value = value;
        // Register this instance so FromString() can retrieve it
        _registry.TryAdd(value, this);
    }

    // Factory method: returns existing instance if found, creates new one if not
    public static MessageRole FromString(string value) =>
        _registry.GetOrAdd(
            value.Trim().ToLowerInvariant(), 
            v => new MessageRole(v)
        );

    // Implicit operator for backward compatibility with string API
    public static implicit operator string(MessageRole role) => role.Value;

    // Equality comparison (used by == operator and collections)
    public bool Equals(MessageRole? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is MessageRole other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;
}
```

**Key components:**
- `sealed class` — Prevents accidental inheritance
- Static readonly instances — Known variants are singletons
- `ConcurrentDictionary<string, MessageRole>` — Thread-safe registry for extensibility
- `FromString()` factory — Normalizes input (trim, lowercase) and returns or creates instance
- `IEquatable<T>` — Enables equality comparison
- Implicit `operator string` — Backward compatibility

### Registry-Based Extensibility

The registry pattern allows **plugins and extensions** to register custom values without modifying core code:

```csharp
// Plugin code (separate assembly)
public class MyPluginExtension
{
    public static void Initialize()
    {
        // Register a custom message role at runtime
        var customRole = MessageRole.FromString("plugin-role");
        
        // Any future calls to FromString("plugin-role") return this same instance
        var retrieved = MessageRole.FromString("plugin-role");
        Assert.Same(customRole, retrieved);  // Same object reference
    }
}
```

**How it works:**
1. First call to `FromString("plugin-role")` → Not in registry → Creates new instance → Stores in `_registry`
2. Second call to `FromString("plugin-role")` → Found in registry → Returns existing instance
3. Serialization uses the `Value` string (e.g., `"plugin-role"`)
4. No core code changes needed

### Thread Safety

The registry uses `ConcurrentDictionary` for thread-safe reads and writes:

```csharp
// Thread-safe: two threads calling FromString() simultaneously
Parallel.For(0, 100, _ => MessageRole.FromString("custom-role"));

// Result: exactly one instance in the registry, reference equality maintained
var role1 = MessageRole.FromString("custom-role");
var role2 = MessageRole.FromString("custom-role");
Assert.Same(role1, role2);  // Guaranteed by ConcurrentDictionary
```

**Key guarantee:** `GetOrAdd()` ensures that if two threads race to create the same value, only one instance is stored and both threads get a reference to the same instance.

### Extension Registration

Extensions can register custom values during plugin initialization:

```csharp
// Core code: available to plugins
public static void RegisterCustomValue(string value)
{
    MessageRole.FromString(value);  // Registers and returns instance
}

// Plugin code (e.g., BotNexus.Plugins.Custom/ExtensionHost.cs)
public static void OnLoadPlugin()
{
    // Register custom execution strategies
    ExecutionStrategy.FromString("machine-learning-pipeline");
    ExecutionStrategy.FromString("real-time-reactive");
    
    // These are now available throughout the application
}
```

---

## 4. Migration Guide

Migrating existing code from raw strings to value objects is **non-breaking** if you use implicit conversions. Follow this phase-wise approach:

### Step 1: Add New Type Alongside Old

Create the value object in `BotNexus.Domain.Primitives` without removing the old string usage.

```csharp
// NEW: In BotNexus.Domain.Primitives/AgentId.cs
[JsonConverter(typeof(AgentIdJsonConverter))]
public readonly record struct AgentId(string Value)
{
    public static AgentId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("AgentId cannot be empty", nameof(value))
            : new(value.Trim());

    public static implicit operator string(AgentId id) => id.Value;
    public static explicit operator AgentId(string value) => From(value);

    public override string ToString() => Value;
}

// OLD: Still exists in the codebase
public class Session
{
    public string AgentId { get; set; }  // Still accepts strings
}
```

**Result:** No breaking changes. Old code continues to work.

### Step 2: Use Implicit Conversion for Backward Compatibility

Existing code that passes strings continues to compile:

```csharp
// Existing test code (unchanged)
var session = new Session { AgentId = "test-agent" };

// New code using value objects
var agentId = AgentId.From("test-agent");
session.AgentId = agentId;  // Implicit operator: AgentId → string
```

### Step 3: Update Producers

Update code that **creates** or **assigns** values to use the value object type:

```csharp
// OLD: Producer creates a string
public GatewaySession CreateSession(string agentId, string sessionId)
{
    return new GatewaySession { AgentId = agentId, SessionId = sessionId };
}

// NEW: Producer creates value objects
public GatewaySession CreateSession(AgentId agentId, SessionId sessionId)
{
    return new GatewaySession { AgentId = agentId.Value, SessionId = sessionId.Value };
}
```

### Step 4: Update Consumers

Update code that **reads** or **uses** values to accept value objects:

```csharp
// OLD: Consumer works with strings
public async Task ProcessAgent(string agentId)
{
    var agent = await _agentRegistry.GetAsync(agentId);
    if (agentId == _currentAgent) { ... }  // String comparison
}

// NEW: Consumer works with value objects
public async Task ProcessAgent(AgentId agentId)
{
    var agent = await _agentRegistry.GetAsync(agentId);
    if (agentId == _currentAgent) { ... }  // Value object comparison
}
```

### Step 5: Remove Old Type (Deferred)

Only after all consumers and producers are updated:

```csharp
// OLD: Remove if no longer used
// public class Session { public string AgentId { get; set; } }

// NEW: Only this remains
public class Session { public AgentId AgentId { get; set; } }
```

**Timeline:** This happens in **Wave 2-3**, after producers/consumers have migrated. Don't rush it.

---

## 5. Type Catalog

This table tracks all DDD types being introduced in BotNexus. Follow this roadmap to understand dependencies and sequencing.

### Phase 1: Domain Foundation (Wave 1) — ✅ Implemented

| Type | Category | Pattern | Status | Phase Intro | Notes |
|------|----------|---------|--------|------------|-------|
| `AgentId` | Identity | Value Object | ✅ Done | 1.1a | Validates non-empty. Ordinal comparison. |
| `SessionId` | Identity | Value Object | ✅ Done | 1.1a | Factory methods: `Create()`, `ForSubAgent()`, `ForCrossAgent()`. |
| `ChannelKey` | Identity | Value Object | ✅ Done | 1.1a | Normalizes at construction (trim + lowercase + alias mapping). Eliminates `NormalizeChannelKey()` duplication. |
| `ConversationId` | Identity | Value Object | ✅ Done | 1.1a | Prevents `ConversationId`/`SenderId` swap bugs. |
| `SenderId` | Identity | Value Object | ✅ Done | 1.1a | Prevents `ConversationId`/`SenderId` swap bugs. |
| `AgentSessionKey` | Composite | Value Object | ✅ Done | 1.1a | Composes `AgentId` + `SessionId`. Replaces `MakeKey()` string concat. |
| `ToolName` | Identity | Value Object | ✅ Done | 1.1a | Case-insensitive equality. Everywhere tool names referenced. |
| `MessageRole` | Discriminator | Smart Enum | ✅ Done | 1.1a | Known values: User, Assistant, System, Tool. Extensible. |
| `SessionStatus` | Discriminator | Smart Enum | ✅ Done | 1.1a | Known values: Active, Suspended, Sealed (replaces "Closed"). |
| `SessionType` | Discriminator | Smart Enum | ✅ Done | 1.1a | Known values: UserAgent, AgentSelf, AgentSubAgent, AgentAgent, Soul, Cron. Extensible. |
| `ExecutionStrategy` | Discriminator | Smart Enum | ✅ Done | 1.1a | Known values: Sequential, Parallel, PriorityQueue. Extensible. |

### Phase 2: Session Model + Identity Fixes (Wave 2-3) — ✅ Implemented

| Type | Category | Pattern | Status | Phase Intro | Notes |
|------|----------|---------|--------|------------|-------|
| `SessionParticipant` | Domain Entity | Record | ✅ Done | 2.0 | Captures `ParticipantType` (User/Agent), participant ID, optional world ID, optional role. Replaces `CallerId`-only model. |
| `SubAgentArchetype` | Discriminator | Smart Enum | ✅ Done | 2.0 | Known values: Researcher, Coder, Planner, Reviewer, Writer, General. Extensible. |
| `TriggerType` | Discriminator | Smart Enum | ✅ Done | 2.0 | Known values: Cron, Soul, Heartbeat. Extensible. Now `IInternalTrigger` interface decouples Cron from `IChannelAdapter`. |

### Phase 3-4: Advanced (Deferred)

| Type | Category | Pattern | Status | Phase Intro | Notes |
|------|----------|---------|--------|------------|-------|
| `World` | Domain Entity | Sealed Record | Deferred | 4 | YAGNI — no consumer. Planned for future phases. |
| `AgentCapabilities` | Composite | Value Object | Planned | Future | Standardizes agent metadata across all discoverers. |

---

## Related Documentation

- **Design Spec:** [docs/planning/ddd-refactoring/design-spec.md](https://github.com/sytone/botnexus/blob/main/docs/planning/ddd-refactoring/design-spec.md) — Full DDD refactoring specification, phase breakdown, and impact analysis
- **Research:** [docs/planning/ddd-refactoring/research.md](https://github.com/sytone/botnexus/blob/main/docs/planning/ddd-refactoring/research.md) — Primitive obsession analysis, value object ROI, codebase evidence
- **Design Review:** [.squad/decisions/inbox/leela-ddd-design-review.md](https://github.com/sytone/botnexus/blob/main/.squad/decisions/inbox/leela-ddd-design-review.md) — Architectural decisions (D1-D8), wave plan, risk analysis
- **Architecture:** [docs/architecture/overview.md](../architecture/overview.md) — System design and module relationships
- **Migration Tracking:** [docs/planning/ddd-refactoring](https://github.com/sytone/botnexus/tree/main/docs/planning/ddd-refactoring) — Per-phase status and implementation notes

---

## 6. Wave 2-3 Implementation Summary

**Status:** ✅ Complete (2026-04-12)

Wave 2-3 delivered the full session model redesign and completed the DDD foundation layer. All 11 Phase 1 value objects and 3 Phase 2-3 smart enums are now implemented and in use across the codebase.

### Key Wave 2-3 Changes

**1. SessionStatus Rename: "Closed" → "Sealed"**
- Changed domain language to reflect semantics: session records are *sealed* (preserved, complete), not deleted
- Smart enum now supports: `Active`, `Suspended`, `Sealed`
- Impact: 47 files updated; migration handled via smart enum string normalization

**2. SessionType Discrimination (Complete)**
- Implemented all 6 session type variants: `UserAgent`, `AgentSelf`, `AgentSubAgent`, `AgentAgent`, `Soul`, `Cron`
- `IsInteractive` computed property: returns `true` only for `UserAgent` sessions
- Enables filtering interactive vs. programmatic sessions at compile time
- Smart enum allows custom types via `FromString()` registry

**3. SessionParticipant Model (Backward Compat)**
- New `SessionParticipant` record: `Type` (User/Agent), `Id`, `WorldId`, `Role`
- Replaces the old "CallerId string" model with structured, extensible approach
- `CallerId` property retained on `GatewaySession` for backward compatibility
- Participants list now canonical source of truth for session membership

**4. Cron Decoupled from IChannelAdapter**
- New `IInternalTrigger` interface isolates non-channel session creation (Cron, SystemTimer, custom)
- Cron no longer pretends to be a channel; it's now its own trigger system
- Enables clean separation: channels → user-triggered sessions; triggers → background/automated sessions
- TriggerType smart enum with extensibility for custom triggers

**5. ChannelKey and MessageRole Value Objects**
- `ChannelKey` normalizes at construction (trim, lowercase) — eliminates `NormalizeChannelKey()` duplication
- `MessageRole` smart enum enables extensible message roles (User, Assistant, System, Tool, + custom)
- Both travel through serialization with validation enforced at deserialization

**6. Typed AgentId and SessionId Across Full Stack**
- All 27 projects now use `AgentId` and `SessionId` value objects instead of strings
- Compile-time type safety prevents ID swap bugs
- Session stores, wire protocols, and all APIs updated

**7. Sub-Agent Archetype Identity**
- Sub-agents now have `SubAgentArchetype` smart enum (Researcher, Coder, Planner, Reviewer, Writer, General)
- Enables distinct child agent identities with specialized behavior
- Archetypes extensible via registry for custom sub-agent types

### Migration Pattern Established

Wave 2-3 validated the DDD migration process:
1. **Additive approach** — New types alongside old (no breaking changes)
2. **Implicit conversion** — Existing tests compile without modification
3. **Incremental migration** — Producers, then consumers, then old types removed
4. **Extensibility** — Smart enum registry allows plugins to add custom values

This pattern is now standard for all future DDD work (Phases 3-4).

### Impact Metrics

| Metric | Value |
|--------|-------|
| Files Updated | 47 |
| New Value Objects | 11 (all Phase 1) |
| New Smart Enums | 3 (Phase 2-3) |
| Backward Compatibility | 100% (implicit conversion) |
| Test Coverage | 891 tests passing |
| Code Churn | Minimal (targeted refactoring) |

---

## Quick Reference

**When should I use a value object?**
→ When a string represents a domain concept with validation (AgentId, SessionId, ChannelKey).

**Can I use implicit string conversion in new code?**
→ No. Use implicit only during migration for backward compatibility. New code should use explicit or direct value object construction.

**What if my value object needs custom equality?**
→ Override `Equals()` and `GetHashCode()`. For enums (smart enums), implement `IEquatable<T>`.

**How do I extend a smart enum with custom values?**
→ Call `FromString("custom-value")` at plugin initialization time. The registry handles storage and retrieval.

**Are value objects thread-safe?**
→ Yes. `readonly record struct` is immutable (heap-safe). Smart enums use `ConcurrentDictionary` (registry-safe).

**How do I serialize value objects?**
→ Use `[JsonConverter(typeof(MyTypeJsonConverter))]` on the type. The converter calls `From()` on deserialization (validation enforced).

**What about backward compatibility?**
→ Implicit operators preserve backward compatibility during migration. Existing tests compile without changes. Gradually migrate to explicit conversion in new code.
