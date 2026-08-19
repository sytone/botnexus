namespace BotNexus.SourceGenerators.Tests;

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Shouldly;

/// <summary>
/// Covers the tool-schema generator (#3320): the schema it renders, the prepare-stage table it emits,
/// and the three malformed-declaration cases that must be build ERRORS rather than silence.
/// </summary>
public class ToolSchemaSourceGeneratorTests
{
    /// <summary>
    /// The exact schema literal <c>GrepTool</c> carried before conversion, reproduced here as the
    /// reference for AC2. If the generator's output ever drifts from this, the claim that the
    /// model-visible schema was unchanged stops being true and this test says so.
    /// </summary>
    private const string HandWrittenGrepSchema = """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Search pattern (supports regex)" },
            "path": { "type": "string", "description": "Directory or file to search (default: working directory)" },
            "glob": { "type": "string", "description": "Glob pattern to include files (e.g., *.cs, *.ts)" },
            "ignore_case": { "type": "boolean", "description": "Perform case-insensitive matching (default: false)" },
            "ignoreCase": { "type": "boolean", "description": "Case-insensitive matching alias." },
            "literal": { "type": "boolean", "description": "Treat pattern as literal string (default: false)" },
            "context": { "type": "integer", "description": "Number of lines to show before and after each match (default: 0)" },
            "limit": { "type": "integer", "description": "Maximum results to return (default: 100)" }
          },
          "required": ["pattern"]
        }
        """;

    // ── AC2: the generated schema is byte-identical to the hand-written one ────────────────

    [Fact]
    public void GeneratedGrepSchema_IsByteIdenticalToTheHandWrittenLiteral()
    {
        // The single most load-bearing claim of the spike. #3320 puts "changing any tool's observable
        // schema" explicitly out of scope, so this is not a formatting preference - it is the
        // out-of-scope guard, asserted rather than argued.
        ToolSchemaCodeGenerator.BuildSchemaJson(GrepParameters())
            .Replace("\r\n", "\n")
            .ShouldBe(HandWrittenGrepSchema.Replace("\r\n", "\n"));
    }

    [Fact]
    public void HiddenParameters_AreOmittedFromTheSchemaButKeptInTheTable()
    {
        // include/max_results are tolerated caller spellings, not documented surface. Advertising
        // them would enlarge what the model sees; dropping them would break existing callers.
        var schema = ToolSchemaCodeGenerator.BuildSchemaJson(GrepParameters());

        // Asserts the quoted KEY, not the bare word: "include" also occurs inside the glob parameter's
        // description ("Glob pattern to include files"), so a substring check on the bare word would
        // fail for a schema that is entirely correct.
        schema.ShouldNotContain("\"include\":");
        schema.ShouldNotContain("\"max_results\":");
        GrepParameters().Count(parameter => parameter.HiddenFromSchema).ShouldBe(2);
    }

    [Fact]
    public void AliasParameters_ResolveToTheirCanonicalTargetKey()
    {
        var byName = GrepParameters().ToDictionary(parameter => parameter.Name);

        byName["ignoreCase"].TargetKey.ShouldBe("ignore_case");
        byName["include"].TargetKey.ShouldBe("glob");
        byName["max_results"].TargetKey.ShouldBe("limit");
        byName["pattern"].TargetKey.ShouldBe("pattern", "a non-alias parameter targets its own key");
    }

    // ── AC3 at the generator level: one declaration produces BOTH representations ──────────

    [Fact]
    public void AddingAParameterToTheDeclaration_ReachesBothSchemaAndPrepareTable()
    {
        // This is the #2641 defect made unrepresentable. Before, the schema and the copy list were two
        // hand-maintained lists and a parameter could reach one without the other, silently. Here the
        // second list does not exist, so a single added declaration necessarily reaches both.
        var extended = GrepParameters().ToList();
        extended.Add(new ToolParameterModel
        {
            Name = "windowDays",
            JsonType = "integer",
            Description = "Days of history.",
            AliasOf = string.Empty
        });

        var generated = ToolSchemaCodeGenerator.Generate(new ToolSchemaModel
        {
            Namespace = "Sample",
            ContainerName = "SampleSchema",
            Parameters = extended
        });

        generated.ShouldContain("\"\"windowDays\"\": { \"\"type\"\": \"\"integer\"\"");
        generated.ShouldContain("new GeneratedToolParameter(\"windowDays\", \"integer\", \"windowDays\"");
    }

    [Fact]
    public void RequiredParameters_AppearInTheRequiredArray()
    {
        var schema = ToolSchemaCodeGenerator.BuildSchemaJson(GrepParameters());

        schema.ShouldContain("\"required\": [\"pattern\"]");
    }

    [Fact]
    public void GeneratedSource_IsIdenticalAcrossRuns()
    {
        // Roslyn caches generator output; content varying between runs would invalidate that cache and
        // make two builds of one commit produce different source.
        var model = new ToolSchemaModel
        {
            Namespace = "Sample",
            ContainerName = "SampleSchema",
            Parameters = GrepParameters()
        };

        ToolSchemaCodeGenerator.Generate(model).ShouldBe(ToolSchemaCodeGenerator.Generate(model));
    }

    // ── Sad paths: a malformed declaration is a build ERROR, never silence ─────────────────

    [Fact]
    public void UnknownJsonType_IsABuildError()
    {
        var diagnostics = ToolSchemaSourceGenerator.Validate(
            [new ToolParameterModel { Name = "count", JsonType = "int", AliasOf = string.Empty }],
            Location.None);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe(ToolSchemaSourceGenerator.UnknownJsonTypeId);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("count");
        diagnostic.GetMessage().ShouldContain("int");
    }

    [Fact]
    public void DuplicateParameterName_IsABuildError()
    {
        var diagnostics = ToolSchemaSourceGenerator.Validate(
            [
                new ToolParameterModel { Name = "limit", JsonType = "integer", AliasOf = string.Empty },
                new ToolParameterModel { Name = "limit", JsonType = "integer", AliasOf = string.Empty }
            ],
            Location.None);

        diagnostics.ShouldContain(diagnostic => diagnostic.Id == ToolSchemaSourceGenerator.DuplicateParameterId);
        diagnostics.First(diagnostic => diagnostic.Id == ToolSchemaSourceGenerator.DuplicateParameterId)
            .Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    [Fact]
    public void AliasTargetingAnUndeclaredKey_IsABuildError()
    {
        // An alias whose target nothing declares would copy into a key no reader consults - the #2641
        // failure reintroduced through the alias path. It must not compile.
        var diagnostics = ToolSchemaSourceGenerator.Validate(
            [new ToolParameterModel { Name = "max_results", JsonType = "integer", AliasOf = "limit" }],
            Location.None);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe(ToolSchemaSourceGenerator.UnresolvedAliasId);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("max_results");
        diagnostic.GetMessage().ShouldContain("limit");
    }

    [Fact]
    public void AliasDeclaredAfterItsTarget_IsAccepted()
    {
        // Non-vacuity for the rule above: the ordering rule must accept the correct order, otherwise
        // the error test would pass even if the validator rejected everything.
        ToolSchemaSourceGenerator.Validate(
                [
                    new ToolParameterModel { Name = "limit", JsonType = "integer", AliasOf = string.Empty },
                    new ToolParameterModel { Name = "max_results", JsonType = "integer", AliasOf = "limit" }
                ],
                Location.None)
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheRealGrepDeclaration_ProducesNoDiagnostics()
    {
        ToolSchemaSourceGenerator.Validate(GrepParameters(), Location.None).ShouldBeEmpty();
    }

    /// <summary>The grep declaration as annotated on <c>GrepToolSchema</c>, in source order.</summary>
    private static List<ToolParameterModel> GrepParameters() =>
    [
        Parameter("pattern", "string", "Search pattern (supports regex)", required: true),
        Parameter("path", "string", "Directory or file to search (default: working directory)"),
        Parameter("glob", "string", "Glob pattern to include files (e.g., *.cs, *.ts)"),
        Parameter("ignore_case", "boolean", "Perform case-insensitive matching (default: false)"),
        Parameter("ignoreCase", "boolean", "Case-insensitive matching alias.", aliasOf: "ignore_case"),
        Parameter("literal", "boolean", "Treat pattern as literal string (default: false)"),
        Parameter("context", "integer", "Number of lines to show before and after each match (default: 0)"),
        Parameter("limit", "integer", "Maximum results to return (default: 100)"),
        Parameter("include", "string", string.Empty, aliasOf: "glob", hidden: true),
        Parameter("max_results", "integer", string.Empty, aliasOf: "limit", hidden: true)
    ];

    private static ToolParameterModel Parameter(
        string name,
        string jsonType,
        string description,
        bool required = false,
        string aliasOf = "",
        bool hidden = false) =>
        new()
        {
            Name = name,
            JsonType = jsonType,
            Description = description,
            Required = required,
            AliasOf = aliasOf,
            HiddenFromSchema = hidden
        };
}
