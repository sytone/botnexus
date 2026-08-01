using System.Text.Json;
using BotNexus.Extensions.Plugins;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the contract of the schema-driven manifest parser. The load-bearing property is that
/// the schema FILE is the single source of truth: <see cref="SchemaAndParserAgreeOnRequiredManifestFields"/>
/// and its marketplace twin fail if anyone reintroduces a hand-written field list in C#.
/// </summary>
public sealed class PluginManifestParserTests
{
    private readonly PluginManifestParser _parser = new();

    // AC5 - convention-based component discovery needs no explicit paths.
    [Fact]
    public void ManifestWithOnlyNameParsesSuccessfully()
    {
        var result = _parser.ParseManifest("""{ "name": "hello-world" }""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Empty(result.Errors);
        Assert.Equal("hello-world", result.Value!.Name);
    }

    // AC5 - an omitted component list must stay null (discover by convention), never be
    // silently normalised to an empty list (which would mean "this plugin has no skills").
    [Fact]
    public void OmittedComponentListsRemainNullSoConventionDiscoveryApplies()
    {
        var result = _parser.ParseManifest("""{ "name": "hello-world" }""");

        Assert.True(result.IsValid);
        Assert.Null(result.Value!.Skills);
        Assert.Null(result.Value.Agents);
        Assert.Null(result.Value.Commands);
        Assert.Null(result.Value.Hooks);
        Assert.Null(result.Value.McpServers);
    }

    [Fact]
    public void FullyPopulatedManifestRoundTripsEveryDeclaredField()
    {
        var result = _parser.ParseManifest("""
            {
              "name": "kitchen-sink",
              "description": "Everything at once",
              "version": "1.2.3-beta.1",
              "author": { "name": "Ada", "email": "ada@example.com", "url": "https://example.com" },
              "homepage": "https://example.com/home",
              "repository": "https://example.com/repo.git",
              "license": "MIT",
              "keywords": ["a", "b"],
              "skills": ["skills/one"],
              "agents": ["agents/one.md"],
              "commands": ["commands/one.md"],
              "hooks": "hooks/hooks.json",
              "mcpServers": ".mcp.json"
            }
            """);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        var manifest = result.Value!;
        Assert.Equal("kitchen-sink", manifest.Name);
        Assert.Equal("1.2.3-beta.1", manifest.Version);
        Assert.Equal("Ada", manifest.Author!.Name);
        Assert.Equal("MIT", manifest.License);
        Assert.Equal(["skills/one"], manifest.Skills);
        Assert.Equal(["agents/one.md"], manifest.Agents);
        Assert.Equal(["commands/one.md"], manifest.Commands);
        Assert.Equal("hooks/hooks.json", manifest.Hooks);
        Assert.Equal(".mcp.json", manifest.McpServers);
    }

    // AC4 - rejection, with the offending field named.
    [Fact]
    public void ManifestMissingRequiredNameIsRejectedNamingTheField()
    {
        var result = _parser.ParseManifest("""{ "description": "no name here" }""");

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("name", StringComparison.Ordinal));
    }

    // AC4 - wrong type is a rejection, not a coercion to "7".
    [Fact]
    public void ManifestWithWrongTypeForNameIsRejectedNotCoerced()
    {
        var result = _parser.ParseManifest("""{ "name": 7 }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("name", StringComparison.Ordinal));
    }

    // AC4 - an unknown field is a rejection, because it usually means a typo that would
    // otherwise be silently ignored and leave the author's intent unimplemented.
    [Fact]
    public void ManifestWithUnknownFieldIsRejectedNamingTheOffendingField()
    {
        var result = _parser.ParseManifest("""{ "name": "ok", "skilz": ["typo"] }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("skilz", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestViolatingNamePatternIsRejectedNamingTheField()
    {
        var result = _parser.ParseManifest("""{ "name": "Not Kebab Case" }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("name", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestWithInvalidAuthorObjectIsRejected()
    {
        var result = _parser.ParseManifest("""{ "name": "ok", "author": { "nickname": "Ada" } }""");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void MalformedJsonIsRejectedWithoutThrowing()
    {
        var result = _parser.ParseManifest("{ this is not json");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void EmptyDocumentIsRejected()
    {
        var result = _parser.ParseManifest("   ");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // AC6 - the parser and the schema file must agree on the required-field set.
    [Fact]
    public void SchemaAndParserAgreeOnRequiredManifestFields()
    {
        var fromSchemaFile = RequiredFieldsFromSchemaText(PluginManifestParser.ManifestSchemaJson);

        Assert.Equal(fromSchemaFile, _parser.RequiredManifestFields.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }

    // AC6 - every required field is genuinely enforced: dropping it must be rejected.
    [Fact]
    public void EveryRequiredManifestFieldIsEnforcedByTheParser()
    {
        foreach (var required in _parser.RequiredManifestFields)
        {
            var document = new Dictionary<string, object?> { ["name"] = "valid-name" };
            document.Remove(required);

            var result = _parser.ParseManifest(JsonSerializer.Serialize(document));

            Assert.False(result.IsValid, $"Removing required field '{required}' should have been rejected.");
            Assert.Contains(result.Errors, e => e.Message.Contains(required, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ManifestSchemaFileDeclaresNameAsTheOnlyRequiredField()
    {
        Assert.Equal(["name"], _parser.RequiredManifestFields.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void ParsePluginDirectoryReadsTheConventionalManifestLocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-tests", Guid.NewGuid().ToString("N"));
        var metadata = Path.Combine(root, PluginManifestParser.PluginMetadataDirectoryName);
        Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, PluginManifestParser.ManifestFileName), """{ "name": "on-disk" }""");

            var result = _parser.ParsePluginDirectory(root);

            Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
            Assert.Equal("on-disk", result.Value!.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsePluginDirectoryRejectsADirectoryWithNoManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = _parser.ParsePluginDirectory(root);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Message.Contains(PluginManifestParser.ManifestFileName, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static List<string> RequiredFieldsFromSchemaText(string schemaText)
    {
        using var document = JsonDocument.Parse(schemaText);
        return [.. document.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .OrderBy(f => f, StringComparer.Ordinal)];
    }
}
