using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Behaviour pinned for the Phase 3 value objects introduced/migrated in #502:
/// <see cref="ToolName"/>, <see cref="WorkingDir"/> and <see cref="ConversationTitle"/>.
/// </summary>
/// <remarks>
/// Every type gets the same four-part contract - construction succeeds for a legal value, throws
/// for each illegal shape, normalises deterministically, and round-trips through System.Text.Json
/// as a bare string. The sad paths are the point: a value object whose only tests are happy paths
/// proves nothing that a bare <c>string</c> would not also satisfy.
/// </remarks>
public sealed class Phase3ValueObjectTests
{
    // ---------- ToolName ----------

    [Fact]
    public void ToolName_IsAVogenValueObject()
    {
        // The migration away from the hand-rolled record struct is the whole point of clause 1;
        // asserting only the behaviour below would stay green on the pre-#502 implementation.
        typeof(ToolName).GetCustomAttributes(inherit: false)
            .ShouldContain(
                a => a.GetType().Name.StartsWith("ValueObjectAttribute", StringComparison.Ordinal),
                "ToolName must be a Vogen value object so construction, JSON and equality are generated.");
    }

    [Fact]
    public void ToolName_From_TrimsAndLowercases()
    {
        ToolName.From("  Read_File  ").Value.ShouldBe("read_file");
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("mcp__server__tool")]
    [InlineData("tool.with.dots")]
    [InlineData("tool-with-dashes")]
    public void ToolName_From_AcceptsTheDispatchNameShapesInUse(string value)
    {
        ToolName.From(value).Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n ")]
    public void ToolName_From_RejectsBlank(string? value)
    {
        Should.Throw<ValueObjectValidationException>(() => ToolName.From(value!));
    }

    [Fact]
    public void ToolName_Equality_RemainsCaseInsensitive()
    {
        // The retired hand-rolled struct implemented this with an OrdinalIgnoreCase Equals override.
        // Vogen generates equality from the primitive, so the same contract is now carried by the
        // normaliser. This test is the behaviour-parity anchor for that swap.
        ToolName.From("Read_File").ShouldBe(ToolName.From("read_file"));
        ToolName.From("TOOL.EXEC").ShouldBe(ToolName.From("tool.exec"));
    }

    [Fact]
    public void ToolName_Equality_StillSeparatesDifferentTools()
    {
        ToolName.From("tool.exec").ShouldNotBe(ToolName.From("tool.search"));
    }

    [Fact]
    public void ToolName_JsonRoundTrip_IsABareString()
    {
        var original = ToolName.From("mcp__server__tool_name");
        var json = JsonSerializer.Serialize(original);

        // The wire shape must be unchanged from the retired ToolNameJsonConverter - a quoted string,
        // not an object. Asserting only the round trip would pass for an object wrapper too.
        json.ShouldBe("\"mcp__server__tool_name\"");
        JsonSerializer.Deserialize<ToolName>(json).ShouldBe(original);
    }

    // ---------- WorkingDir ----------

    [Theory]
    [InlineData("/home/agent/workspace")]
    [InlineData("workspace/nested")]
    [InlineData("./relative")]
    public void WorkingDir_From_AcceptsValidPathShapes(string value)
    {
        WorkingDir.From(value).Value.ShouldBe(value);
    }

    [Fact]
    public void WorkingDir_From_TrimsSurroundingWhitespace()
    {
        WorkingDir.From("  /home/agent  ").Value.ShouldBe("/home/agent");
    }

    [Fact]
    public void WorkingDir_From_PreservesTrailingSeparator()
    {
        // Trimming it would turn "C:\" into "C:", which resolves to a *different* directory on
        // Windows (the drive-relative current directory). Normalisation stops at whitespace.
        WorkingDir.From("C:\\").Value.ShouldBe("C:\\");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkingDir_From_RejectsBlank(string? value)
    {
        Should.Throw<ValueObjectValidationException>(() => WorkingDir.From(value!));
    }

    [Fact]
    public void WorkingDir_From_RejectsAnEmbeddedNullCharacter()
    {
        // A null byte terminates the path in the native APIs, so an unvalidated value would be
        // silently truncated to a different - possibly escaping - directory.
        Should.Throw<ValueObjectValidationException>(() => WorkingDir.From("/home/agent\0/etc"));
    }

    [Fact]
    public void WorkingDir_From_RejectsAValueOverTheLengthCeiling()
    {
        var tooLong = "/" + new string('a', WorkingDir.MaxLength);
        Should.Throw<ValueObjectValidationException>(() => WorkingDir.From(tooLong));
    }

    [Fact]
    public void WorkingDir_From_AcceptsAValueExactlyAtTheLengthCeiling()
    {
        // The boundary test that makes the rejection above meaningful: an off-by-one guard would
        // fail here, and a test that only checked the too-long case could not tell the difference.
        var atLimit = new string('a', WorkingDir.MaxLength);
        WorkingDir.From(atLimit).Value.Length.ShouldBe(WorkingDir.MaxLength);
    }

    [Fact]
    public void WorkingDir_JsonRoundTrip_IsABareString()
    {
        var original = WorkingDir.From("/home/agent/workspace");
        var json = JsonSerializer.Serialize(original);

        json.ShouldBe("\"/home/agent/workspace\"");
        JsonSerializer.Deserialize<WorkingDir>(json).ShouldBe(original);
    }

    // ---------- ConversationTitle ----------

    [Fact]
    public void ConversationTitle_From_TrimsSurroundingWhitespace()
    {
        ConversationTitle.From("  Release planning  ").Value.ShouldBe("Release planning");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConversationTitle_From_RejectsBlank(string? value)
    {
        // Deliberately a rejection, not a substituted default: a placeholder chosen here would
        // surface at an arbitrary later point instead of at the caller that omitted the title.
        Should.Throw<ValueObjectValidationException>(() => ConversationTitle.From(value!));
    }

    [Fact]
    public void ConversationTitle_From_RejectsAValueOverTheLengthCeiling()
    {
        Should.Throw<ValueObjectValidationException>(
            () => ConversationTitle.From(new string('x', ConversationTitle.MaxLength + 1)));
    }

    [Fact]
    public void ConversationTitle_From_AcceptsAValueExactlyAtTheLengthCeiling()
    {
        var atLimit = new string('x', ConversationTitle.MaxLength);
        ConversationTitle.From(atLimit).Value.Length.ShouldBe(ConversationTitle.MaxLength);
    }

    [Fact]
    public void ConversationTitle_LengthIsMeasuredAfterTrimming()
    {
        // Whitespace padding must not consume the budget - otherwise a title the user can legally
        // type is rejected purely for the spaces around it.
        var padded = "  " + new string('x', ConversationTitle.MaxLength) + "  ";
        ConversationTitle.From(padded).Value.Length.ShouldBe(ConversationTitle.MaxLength);
    }

    [Fact]
    public void ConversationTitle_JsonRoundTrip_IsABareString()
    {
        var original = ConversationTitle.From("Release planning");
        var json = JsonSerializer.Serialize(original);

        json.ShouldBe("\"Release planning\"");
        JsonSerializer.Deserialize<ConversationTitle>(json).ShouldBe(original);
    }

    // ---------- cross-cutting ----------

    [Fact]
    public void Phase3ValueObjects_ExposeNoImplicitStringConversions()
    {
        // The hand-rolled ToolName had BOTH an implicit operator to string and an explicit one from
        // it, which is exactly the silent-cast hole the strongly-typed-ID convention exists to close.
        foreach (var type in new[] { typeof(ToolName), typeof(WorkingDir), typeof(ConversationTitle) })
        {
            var implicitOperators = type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "op_Implicit")
                .Select(m => $"{m.ReturnType.Name}<-{m.GetParameters()[0].ParameterType.Name}")
                .ToArray();

            implicitOperators.ShouldBeEmpty(
                $"{type.Name} must not expose implicit string conversions. Found: " +
                string.Join(", ", implicitOperators));
        }
    }
}
