using System.Reflection;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions for BotNexus.Domain. These tests structurally enforce
/// the value-object conventions documented in <c>docs/architecture/domain-model.md</c> and
/// <c>AGENTS.md</c>. They are the antidote to "docs said one thing, code said another" —
/// if a future change violates a convention, the build fails before it lands.
/// </summary>
public sealed class DomainArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(AgentId).Assembly;

    /// <summary>
    /// Domain has no project references — it is the leaf of the dependency graph. Source-generator
    /// package references (e.g. Vogen) are allowed because they contribute generated source rather
    /// than runtime dependencies.
    /// </summary>
    [Fact]
    public void Domain_HasNoProjectReferences()
    {
        var referencedAssemblyNames = DomainAssembly
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => !IsFrameworkAssembly(name))
            .ToArray();

        referencedAssemblyNames.ShouldBeEmpty(
            "BotNexus.Domain must remain a leaf node. Found unexpected references: " +
            string.Join(", ", referencedAssemblyNames));
    }

    /// <summary>
    /// AgentId is the canonical Vogen value object for an agent identifier. It must carry the
    /// <see cref="ValueObjectAttribute{T}"/> so the analyser, JSON converter and equality
    /// semantics are generated rather than hand-rolled.
    /// </summary>
    [Fact]
    public void AgentId_IsVogenValueObject()
    {
        var attribute = typeof(AgentId).GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal));

        attribute.ShouldNotBeNull(
            "AgentId must be annotated with [ValueObject<string>] so Vogen generates the converters and analyser checks.");
    }

    /// <summary>
    /// AgentId must not expose an implicit conversion to/from string. The hand-rolled era allowed
    /// silent casts that defeated the entire point of a strong identifier (see F-13 in the domain
    /// model review).
    /// </summary>
    [Fact]
    public void AgentId_HasNoImplicitStringConversion()
    {
        var implicitOperators = typeof(AgentId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit")
            .Select(m => $"{m.ReturnType.Name}<-{m.GetParameters()[0].ParameterType.Name}")
            .ToArray();

        implicitOperators.ShouldBeEmpty(
            "AgentId must not expose implicit conversions to/from string. Use .Value and .From() explicitly. " +
            "Implicit operators found: " + string.Join(", ", implicitOperators));
    }

    /// <summary>
    /// No two public types in the Domain assembly may share the same simple name. The legacy
    /// duplicate <c>SessionStatus</c> (one in Domain.Primitives, one in Gateway.Abstractions.Models)
    /// caused widespread alias usage and bugs — this rule catches recurrences early.
    /// </summary>
    [Fact]
    public void Domain_HasNoTwoPublicTypesWithTheSameSimpleName()
    {
        var duplicates = DomainAssembly
            .GetExportedTypes()
            .GroupBy(t => t.Name)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(", ", group.Select(t => t.FullName))})")
            .ToArray();

        duplicates.ShouldBeEmpty(
            "BotNexus.Domain must not contain two public types with the same simple name. Duplicates: " +
            string.Join("; ", duplicates));
    }

    /// <summary>
    /// All Vogen value objects in <c>BotNexus.Domain.Primitives</c> use a struct backing form.
    /// Source-generated underscore-prefixed types created by Vogen are filtered out so they
    /// do not trip the rule.
    /// </summary>
    [Fact]
    public void DomainPrimitives_VogenValueObjectsAreStructs()
    {
        var vogenTypes = DomainAssembly
            .GetExportedTypes()
            .Where(t => t.Namespace == "BotNexus.Domain.Primitives")
            .Where(t => t.GetCustomAttributes()
                .Any(a => a.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal)))
            .ToArray();

        vogenTypes.ShouldNotBeEmpty("Expected at least one Vogen value object in BotNexus.Domain.Primitives.");

        var nonStructs = vogenTypes.Where(t => !t.IsValueType).ToArray();

        nonStructs.ShouldBeEmpty(
            "Vogen value objects in Domain.Primitives must be structs. Offending types: " +
            string.Join(", ", nonStructs.Select(t => t.FullName)));
    }

    /// <summary>
    /// ConversationId is the canonical Vogen value object for a conversation identifier. Mirrors the
    /// AgentId rule — Vogen owns construction, JSON, and equality.
    /// </summary>
    [Fact]
    public void ConversationId_IsVogenValueObject()
    {
        var attribute = typeof(ConversationId).GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal));

        attribute.ShouldNotBeNull(
            "ConversationId must be annotated with [ValueObject<string>] so Vogen generates the converters and analyser checks.");
    }

    /// <summary>
    /// ConversationId must not expose an implicit conversion to/from string. Same rationale as the
    /// AgentId rule — silent casts on hot identifiers defeat the entire point of strong typing.
    /// </summary>
    [Fact]
    public void ConversationId_HasNoImplicitStringConversion()
    {
        var implicitOperators = typeof(ConversationId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit")
            .Select(m => $"{m.ReturnType.Name}<-{m.GetParameters()[0].ParameterType.Name}")
            .ToArray();

        implicitOperators.ShouldBeEmpty(
            "ConversationId must not expose implicit conversions to/from string. Use .Value and .From() explicitly. " +
            "Implicit operators found: " + string.Join(", ", implicitOperators));
    }

    /// <summary>
    /// SessionId is the canonical Vogen value object for a session identifier. Mirrors the
    /// AgentId rule — Vogen owns construction, JSON, and equality.
    /// </summary>
    [Fact]
    public void SessionId_IsVogenValueObject()
    {
        var attribute = typeof(SessionId).GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal));

        attribute.ShouldNotBeNull(
            "SessionId must be annotated with [ValueObject<string>] so Vogen generates the converters and analyser checks.");
    }

    /// <summary>
    /// SessionId must not expose an implicit conversion to/from string. The legacy hand-rolled SessionId
    /// did expose implicit operators and that was the worst offender — the seed PR closed the door, and
    /// this rule keeps it closed.
    /// </summary>
    [Fact]
    public void SessionId_HasNoImplicitStringConversion()
    {
        var implicitOperators = typeof(SessionId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit")
            .Select(m => $"{m.ReturnType.Name}<-{m.GetParameters()[0].ParameterType.Name}")
            .ToArray();

        implicitOperators.ShouldBeEmpty(
            "SessionId must not expose implicit conversions to/from string. Use .Value and .From() explicitly. " +
            "Implicit operators found: " + string.Join(", ", implicitOperators));
    }

    /// <summary>
    /// UserId is the canonical Vogen value object for a human user identifier introduced in Phase 1.5
    /// alongside the Citizen abstraction. Mirrors the AgentId rule — Vogen owns construction, JSON,
    /// and equality.
    /// </summary>
    [Fact]
    public void UserId_IsVogenValueObject()
    {
        var attribute = typeof(UserId).GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal));

        attribute.ShouldNotBeNull(
            "UserId must be annotated with [ValueObject<string>] so Vogen generates the converters and analyser checks.");
    }

    /// <summary>
    /// UserId must not expose an implicit conversion to/from string. Phase 1.5 introduced UserId as
    /// a typed identity for the User species; allowing implicit string conversions would defeat the
    /// purpose just like it did for the original hand-rolled AgentId / SessionId.
    /// </summary>
    [Fact]
    public void UserId_HasNoImplicitStringConversion()
    {
        var implicitOperators = typeof(UserId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit")
            .Select(m => $"{m.ReturnType.Name}<-{m.GetParameters()[0].ParameterType.Name}")
            .ToArray();

        implicitOperators.ShouldBeEmpty(
            "UserId must not expose implicit conversions to/from string. Use .Value and .From() explicitly. " +
            "Implicit operators found: " + string.Join(", ", implicitOperators));
    }

    private static bool IsFrameworkAssembly(string assemblyName)
        => assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal)
        || assemblyName.StartsWith("netstandard", StringComparison.Ordinal)
        || assemblyName == "mscorlib"
        || assemblyName == "System"
        || assemblyName == "Vogen.SharedTypes"
        || assemblyName == "Vogen";
}
