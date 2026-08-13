using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Contract tests for <see cref="ToolName"/>.
/// </summary>
/// <remarks>
/// Re-pointed in #502 when ToolName migrated from a hand-rolled <c>readonly record struct</c> to a
/// Vogen value object. Three assertions changed shape and none were dropped:
/// <list type="bullet">
/// <item>the invalid-input exception is now <see cref="ValueObjectValidationException"/> (which
/// derives from <see cref="Exception"/>, not <see cref="ArgumentException"/>) - the contract "a
/// blank tool name is refused at construction" is unchanged and still asserted;</item>
/// <item>the implicit/explicit string conversions are gone by design, so the two cast tests now
/// assert the replacement API (<c>.Value</c> / <c>.From</c>) that callers must use;</item>
/// <item>case-insensitive equality is now carried by the normaliser rather than an <c>Equals</c>
/// override, so the test additionally pins the canonical stored value.</item>
/// </list>
/// </remarks>
public sealed class ToolNameTests
{
    [Fact]
    public void ToolName_From_WhenValueIsValid_ShouldCreateInstance()
    {
        var result = ToolName.From(" tool.exec ");
        result.Value.ShouldBe("tool.exec");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ToolName_From_WhenValueIsEmpty_ShouldThrow(string? value)
    {
        Action action = () => ToolName.From(value!);
        action.ShouldThrow<ValueObjectValidationException>();
    }

    [Fact]
    public void ToolName_Equals_WhenValuesMatchCaseInsensitively_ShouldBeTrue()
    {
        var left = ToolName.From("TOOL.EXEC");
        var right = ToolName.From("tool.exec");
        left.ShouldBe(right);

        // Vogen derives equality from the stored primitive, so the case-insensitive contract now
        // depends on the value being canonicalised. Pin that, or the equality above could silently
        // become case-SENSITIVE the moment the normaliser changes.
        left.Value.ShouldBe("tool.exec");
    }

    [Fact]
    public void ToolName_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = ToolName.From("tool.exec");
        var right = ToolName.From("tool.search");
        left.ShouldNotBe(right);
    }

    [Fact]
    public void ToolName_Value_WhenRead_ShouldReturnTheUnderlyingString()
    {
        // Replaces the retired implicit conversion to string: callers now read .Value explicitly.
        var toolName = ToolName.From("tool.exec");
        string value = toolName.Value;
        value.ShouldBe("tool.exec");
    }

    [Fact]
    public void ToolName_From_WhenGivenAString_ShouldCreateInstance()
    {
        // Replaces the retired explicit cast from string.
        var toolName = ToolName.From("tool.exec");
        toolName.Value.ShouldBe("tool.exec");
    }

    [Fact]
    public void ToolName_ToString_WhenCalled_ShouldReturnValue()
    {
        var toolName = ToolName.From("tool.exec");
        toolName.ToString().ShouldBe("tool.exec");
    }

    [Fact]
    public void ToolName_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var original = ToolName.From("tool.exec");
        var roundTrip = JsonSerializer.Deserialize<ToolName>(JsonSerializer.Serialize(original));
        roundTrip.ShouldBe(original);
    }
}
