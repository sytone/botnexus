using System.Text.Json;
using BotNexus.Agent.Providers.Core.Validation;

namespace BotNexus.Agent.Providers.Core.Tests.Validation;

/// <summary>
/// Tests for the coerce-then-validate behaviour added for issue #1552. The validator
/// previously rejected losslessly-coercible argument shapes (string-encoded integers,
/// scalars supplied where an array is expected) even though the tools themselves already
/// tolerate them downstream. These tests pin the new lenient coercion and confirm that
/// genuinely-wrong shapes are still rejected with an improved diagnostic.
/// </summary>
public class ToolCallValidatorCoercionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Validate_WhenStringEncodedInteger_CoercesAndAccepts()
    {
        var arguments = Parse("""{ "timeout_seconds": "300" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "timeout_seconds": { "type": "integer" } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();
        coerced.GetProperty("timeout_seconds").ValueKind.ShouldBe(JsonValueKind.Number);
        coerced.GetProperty("timeout_seconds").GetInt32().ShouldBe(300);
    }

    [Fact]
    public void Validate_WhenStringEncodedNumber_CoercesAndAccepts()
    {
        var arguments = Parse("""{ "temperature": "0.5" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "temperature": { "type": "number" } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        coerced.GetProperty("temperature").ValueKind.ShouldBe(JsonValueKind.Number);
        coerced.GetProperty("temperature").GetDouble().ShouldBe(0.5);
    }

    [Fact]
    public void Validate_WhenStringEncodedBoolean_CoercesAndAccepts()
    {
        var arguments = Parse("""{ "allow_multiple": "true" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "allow_multiple": { "type": "boolean" } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        coerced.GetProperty("allow_multiple").ValueKind.ShouldBe(JsonValueKind.True);
    }

    [Fact]
    public void Validate_WhenScalarSuppliedForArray_WrapsInSingleElementArray()
    {
        var arguments = Parse("""{ "tags": "platform" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        var tags = coerced.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(1);
        tags[0].GetString().ShouldBe("platform");
    }

    [Fact]
    public void Validate_WhenCommaSeparatedStringForStringArray_SplitsIntoElements()
    {
        var arguments = Parse("""{ "tags": "alpha, beta ,gamma" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        var tags = coerced.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(3);
        tags[0].GetString().ShouldBe("alpha");
        tags[1].GetString().ShouldBe("beta");
        tags[2].GetString().ShouldBe("gamma");
    }

    [Fact]
    public void Validate_WhenNumberSuppliedForArray_WrapsInSingleElementArray()
    {
        var arguments = Parse("""{ "ids": 42 }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "ids": { "type": "array", "items": { "type": "integer" } } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        var ids = coerced.GetProperty("ids");
        ids.ValueKind.ShouldBe(JsonValueKind.Array);
        ids.GetArrayLength().ShouldBe(1);
        ids[0].GetInt32().ShouldBe(42);
    }

    [Fact]
    public void Validate_WhenObjectSuppliedForArray_StillRejectsWithReceivedKind()
    {
        var arguments = Parse("""{ "tags": { "nope": 1 } }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array" } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("Property 'tags' must be of type array");
        error.ShouldContain("received object");
    }

    [Fact]
    public void Validate_WhenNonNumericStringForInteger_StillRejectsWithReceivedValue()
    {
        var arguments = Parse("""{ "timeout_seconds": "fast" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "timeout_seconds": { "type": "integer" } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out _);

        isValid.ShouldBeFalse();
        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("Property 'timeout_seconds' must be of type integer");
        error.ShouldContain("received string");
    }

    [Fact]
    public void Validate_TwoArgOverload_AlsoCoercesStringInteger()
    {
        // Back-compat: the original 2-arg overload must now accept coercible shapes too,
        // so existing callers stop seeing spurious rejections.
        var arguments = Parse("""{ "timeout_seconds": "300" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "timeout_seconds": { "type": "integer" } }
            }
            """);

        var result = ToolCallValidator.Validate(arguments, schema);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WhenNothingToCoerce_ReturnsEquivalentArguments()
    {
        var arguments = Parse("""{ "query": "hello", "count": 3 }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string" },
                "count": { "type": "integer" }
              }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        coerced.GetProperty("query").GetString().ShouldBe("hello");
        coerced.GetProperty("count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void Validate_WhenIntegerSchemaButFloatString_RejectsNonIntegerValue()
    {
        // "3.5" round-trips as a number but is not an integer -> must still reject for integer schema.
        var arguments = Parse("""{ "count": "3.5" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "count": { "type": "integer" } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out _);

        isValid.ShouldBeFalse();
        errors.ShouldHaveSingleItem().ShouldContain("must be of type integer");
    }

    [Fact]
    public void Validate_WhenAlreadyCorrectArrayType_LeavesArrayUnchanged()
    {
        var arguments = Parse("""{ "tags": ["a", "b"] }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        var tags = coerced.GetProperty("tags");
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe("a");
        tags[1].GetString().ShouldBe("b");
    }
    [Fact]
    public void Validate_WhenJsonArrayStringForStringArray_ParsesIntoRealArray()
    {
        // A model serialised an array param as a JSON string. The validator must parse the
        // JSON array rather than wrapping the whole literal into a 1-element string array or
        // comma-splitting it. (Issue #1738 -- mirror of OpenClaw 9202dbb1b650.)
        var arguments = Parse("""{ "tags": "[\"a\",\"b\"]" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();
        var tags = coerced.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe("a");
        tags[1].GetString().ShouldBe("b");
    }

    [Fact]
    public void Validate_WhenJsonObjectStringForObject_ParsesIntoRealObject()
    {
        // A model serialised an object param as a JSON string. The validator must parse the
        // JSON object so the downstream tool receives a real object. (Issue #1738.)
        var arguments = Parse("""{ "config": "{\"enabled\":true}" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "config": { "type": "object" } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        errors.ShouldBeEmpty();
        var config = coerced.GetProperty("config");
        config.ValueKind.ShouldBe(JsonValueKind.Object);
        config.GetProperty("enabled").ValueKind.ShouldBe(JsonValueKind.True);
    }

    [Fact]
    public void Validate_WhenOversizedJsonArrayString_DoesNotCoerceAndRejects()
    {
        // Defends the in-process validator from unbounded synchronous JSON parsing on
        // model-controlled input: a JSON-array string larger than the 64 KB cap must NOT be
        // parsed -- it falls through to the reject path. (Issue #1738.)
        var oversized = "[\"" + new string('x', 70 * 1024) + "\"]";
        var arguments = Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["tags"] = oversized }));
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
            }
            """);

        var (isValid, errors) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        // Not silently coerced into a valid array.
        var tags = coerced.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.String);
        isValid.ShouldBeFalse();
        errors.ShouldHaveSingleItem().ShouldContain("Property 'tags' must be of type array");
    }

    [Fact]
    public void Validate_WhenNonJsonStringForStringArray_StillWrapsInSingleElementArray()
    {
        // Back-compat: a plain (non-JSON) string must keep the existing scalar->array
        // behaviour -- it does not parse as a JSON array/object, so it is still wrapped.
        var arguments = Parse("""{ "tags": "platform" }""");
        var schema = Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
            }
            """);

        var (isValid, _) = ToolCallValidator.Validate(arguments, schema, out var coerced);

        isValid.ShouldBeTrue();
        var tags = coerced.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(1);
        tags[0].GetString().ShouldBe("platform");
    }
}
