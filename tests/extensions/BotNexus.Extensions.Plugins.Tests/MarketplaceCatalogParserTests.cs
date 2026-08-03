using BotNexus.Extensions.Plugins;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the marketplace catalog contract (AC2) and the same no-drift property as the manifest:
/// the schema file, not C#, declares which catalog fields are required.
/// </summary>
public sealed class MarketplaceCatalogParserTests
{
    private readonly PluginManifestParser _parser = new();

    [Fact]
    public void MinimalCatalogWithNameOwnerAndPluginsParses()
    {
        var result = _parser.ParseMarketplace("""
            {
              "name": "core-marketplace",
              "owner": { "name": "BotNexus Team" },
              "plugins": []
            }
            """);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("core-marketplace", result.Value!.Name);
        Assert.Equal("BotNexus Team", result.Value.Owner.Name);
        Assert.Empty(result.Value.Plugins);
    }

    [Fact]
    public void CatalogEntriesProjectToTypedPluginEntries()
    {
        var result = _parser.ParseMarketplace("""
            {
              "name": "core-marketplace",
              "owner": { "name": "BotNexus Team", "url": "https://example.com" },
              "description": "Curated plugins",
              "plugins": [
                {
                  "name": "hello-world",
                  "source": "https://example.com/hello.git",
                  "description": "Says hello",
                  "version": "1.0.0",
                  "keywords": ["demo"]
                }
              ]
            }
            """);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        var entry = Assert.Single(result.Value!.Plugins);
        Assert.Equal("hello-world", entry.Name);
        Assert.Equal("https://example.com/hello.git", entry.Source);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(["demo"], entry.Keywords);
    }

    [Fact]
    public void CatalogMissingPluginsIsRejectedNamingTheField()
    {
        var result = _parser.ParseMarketplace("""
            { "name": "core-marketplace", "owner": { "name": "BotNexus Team" } }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("plugins", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogMissingOwnerIsRejectedNamingTheField()
    {
        var result = _parser.ParseMarketplace("""
            { "name": "core-marketplace", "plugins": [] }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("owner", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogEntryMissingSourceIsRejectedNamingTheField()
    {
        var result = _parser.ParseMarketplace("""
            {
              "name": "core-marketplace",
              "owner": { "name": "BotNexus Team" },
              "plugins": [ { "name": "hello-world" } ]
            }
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("source", StringComparison.Ordinal));
    }

    // AC6 twin - schema file and parser must agree on the catalog required-field set.
    [Fact]
    public void SchemaAndParserAgreeOnRequiredMarketplaceFields()
    {
        var fromSchemaFile = PluginManifestParserTests.RequiredFieldsFromSchemaText(
            PluginManifestParser.MarketplaceSchemaJson);

        Assert.Equal(fromSchemaFile, _parser.RequiredMarketplaceFields.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void EveryRequiredMarketplaceFieldIsEnforcedByTheParser()
    {
        const string ValidCatalog = """
            { "name": "core-marketplace", "owner": { "name": "BotNexus Team" }, "plugins": [] }
            """;

        foreach (var required in _parser.RequiredMarketplaceFields)
        {
            using var document = System.Text.Json.JsonDocument.Parse(ValidCatalog);
            var trimmed = document.RootElement.EnumerateObject()
                .Where(p => !string.Equals(p.Name, required, StringComparison.Ordinal))
                .ToDictionary(p => p.Name, p => p.Value.Clone());

            var result = _parser.ParseMarketplace(System.Text.Json.JsonSerializer.Serialize(trimmed));

            Assert.False(result.IsValid, $"Removing required field '{required}' should have been rejected.");
            Assert.Contains(result.Errors, e => e.Message.Contains(required, StringComparison.Ordinal));
        }
    }
}
