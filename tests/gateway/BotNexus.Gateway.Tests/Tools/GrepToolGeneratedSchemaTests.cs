using System.Text.Json;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Proves the #2641 defect class is unrepresentable for <c>GrepTool</c> after the #3320 conversion:
/// its JSON schema and its <c>PrepareArgumentsAsync</c> copy list are both projections of one
/// attribute declaration, so a parameter cannot reach one without reaching the other.
/// </summary>
/// <remarks>
/// #2641 was a parameter present in the schema but absent from the copy list. Nothing threw; the
/// caller's value was dropped and the default answered instead - a plausible number for the wrong
/// question. These tests therefore assert the EFFECTIVE prepared value, not merely that a key is
/// present, because presence was never the property that failed.
/// </remarks>
public sealed class GrepToolGeneratedSchemaTests
{
    private readonly GrepTool _tool = new(Path.GetTempPath());

    [Fact]
    public void Definition_DeclaresEveryParameterThePrepareStageCopies()
    {
        // The invariant #2641 violated, stated directly: every schema-advertised key must be one the
        // prepare stage will actually copy. Both sides are read from the same generated declaration,
        // so this can only fail if the generation itself regresses.
        var schemaKeys = _tool.Definition.Parameters
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        var preparedKeys = GrepToolSchema.Parameters
            .Where(parameter => !parameter.HiddenFromSchema)
            .Select(parameter => parameter.Name)
            .ToList();

        schemaKeys.ShouldBe(preparedKeys);
    }

    [Fact]
    public async Task EveryDeclaredParameter_ReachesThePreparedDictionaryWithItsEffectiveValue()
    {
        // Asserts the VALUE, not the key. A copy list that silently dropped a parameter would still
        // leave the schema intact, which is exactly how #2641 hid.
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["path"] = "sub/dir",
            ["glob"] = "*.cs",
            ["ignore_case"] = true,
            ["literal"] = true,
            ["context"] = 3,
            ["limit"] = 42
        });

        prepared["pattern"].ShouldBe("needle");
        prepared["path"].ShouldBe("sub/dir");
        prepared["glob"].ShouldBe("*.cs");
        prepared["ignore_case"].ShouldBe(true);
        prepared["literal"].ShouldBe(true);
        prepared["context"].ShouldBe(3);
        prepared["limit"].ShouldBe(42);
    }

    [Fact]
    public async Task UndocumentedAliases_StillReachTheirCanonicalKey()
    {
        // The alias coercion the issue requires to survive the conversion.
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["include"] = "*.md",
            ["max_results"] = 7,
            ["ignoreCase"] = true
        });

        prepared["glob"].ShouldBe("*.md");
        prepared["limit"].ShouldBe(7);
        prepared["ignore_case"].ShouldBe(true);
    }

    [Fact]
    public async Task CanonicalKeyWins_WhenBothItAndItsAliasAreSupplied()
    {
        // Declaration order decides precedence and the canonical key is declared first, matching the
        // pre-conversion if/else-if chain exactly.
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["glob"] = "*.cs",
            ["include"] = "*.md",
            ["limit"] = 5,
            ["max_results"] = 900
        });

        prepared["glob"].ShouldBe("*.cs");
        prepared["limit"].ShouldBe(5);
    }

    [Fact]
    public void GeneratedSchema_IsValidJsonAndRequiresPatternOnly()
    {
        var required = _tool.Definition.Parameters
            .GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        required.ShouldBe(["pattern"]);
        _tool.Definition.Parameters.GetProperty("type").GetString().ShouldBe("object");
    }

    [Fact]
    public void GeneratedSchema_DoesNotAdvertiseTheUndocumentedAliases()
    {
        // Advertising them would change the model-visible schema, which #3320 puts out of scope.
        var raw = _tool.Definition.Parameters.GetRawText();

        raw.ShouldNotContain("max_results");
        raw.ShouldNotContain("\"include\"");
    }

    [Fact]
    public async Task MissingRequiredParameter_StillThrows()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            _tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["path"] = "." }));
    }

    [Fact]
    public async Task StringifiedArguments_AreStillCoerced()
    {
        // The coercion layer defends against caller behaviour, not against drift, and the issue keeps
        // it explicitly in scope. A JsonElement-shaped argument must still land as a typed value.
        var json = JsonDocument.Parse("""{ "limit": "25", "ignore_case": "true" }""").RootElement;

        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["limit"] = json.GetProperty("limit"),
            ["ignore_case"] = json.GetProperty("ignore_case")
        });

        prepared["limit"].ShouldBe(25);
        prepared["ignore_case"].ShouldBe(true);
    }
}
