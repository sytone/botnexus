using BotNexus.Extensions.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

public sealed class GeneratedMarketplaceCatalogAdapterTests
{
    private readonly GeneratedMarketplaceCatalogAdapter _adapter = new();

    [Fact]
    public void VersionOneProjectsPaginatedComponentsAndImmutableInstallReceipt()
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            """
            {
              "version": 1,
              "name": "sample-marketplace",
              "owner": "Sample Publisher",
              "totalItems": 2,
              "pages": ["pages/1.json", "pages/2.json"]
            }
            """,
            new Dictionary<string, string>
            {
                ["pages/1.json"] = """
                    {
                      "version": 1,
                      "page": 1,
                      "totalPages": 2,
                      "items": [
                        {
                          "id": "skills-pack",
                          "source": "repositories/skills-pack.git",
                          "reference": "8b137891791fe96927ad78e64b0aad7bded08bdc",
                          "skills": ["summarize"],
                          "agents": []
                        }
                      ]
                    }
                    """,
                ["pages/2.json"] = """
                    {
                      "version": 1,
                      "page": 2,
                      "totalPages": 2,
                      "items": [
                        {
                          "id": "mixed-pack",
                          "source": "repositories/mixed-pack.git",
                          "reference": "v2.1.0",
                          "skills": ["review"],
                          "agents": ["reviewer"]
                        }
                      ]
                    }
                    """,
            });

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("sample-marketplace", result.Value!.Catalog.Name);
        Assert.Equal(2, result.Value.Catalog.Plugins.Count);

        var skills = result.Value.Catalog.Plugins[0];
        Assert.Equal(["summarize"], skills.Components.Skills);
        Assert.Equal([MarketplaceComponentKind.Skill], skills.Components.Kinds);

        var mixed = result.Value.Catalog.Plugins[1];
        Assert.Equal([MarketplaceComponentKind.Skill, MarketplaceComponentKind.Agent], mixed.Components.Kinds);

        PluginInstallRequest receipt = result.Value.ResolveInstallRequest("mixed-pack");
        Assert.Equal("https://catalog.example/marketplace/repositories/mixed-pack.git", receipt.Source);
        Assert.Equal("v2.1.0", receipt.Reference);
        Assert.Equal("mixed-pack", receipt.Name);
        Assert.False(receipt.UpdatesEnabled);
    }

    [Theory]
    [InlineData(2, "version")]
    [InlineData(0, "version")]
    public void UnknownManifestVersionIsRejected(int version, string expectedField)
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            $$"""
            { "version": {{version}}, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 0, "pages": [] }
            """,
            new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field.Contains(expectedField, StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatePluginIdentityAcrossPagesFailsWithoutPartialCatalog()
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            """
            { "version": 1, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 2, "pages": ["p1.json", "p2.json"] }
            """,
            new Dictionary<string, string>
            {
                ["p1.json"] = """{ "version": 1, "page": 1, "totalPages": 2, "items": [{ "id": "same", "source": "repositories/a.git", "reference": "v1", "skills": [], "agents": [] }] }""",
                ["p2.json"] = """{ "version": 1, "page": 2, "totalPages": 2, "items": [{ "id": "same", "source": "repositories/b.git", "reference": "v1", "skills": [], "agents": [] }] }""",
            });

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Message.Contains("Duplicate plugin identity", StringComparison.Ordinal));
    }

    [Fact]
    public void InconsistentTotalsFailAsIncomplete()
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            """
            { "version": 1, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 2, "pages": ["p1.json"] }
            """,
            new Dictionary<string, string>
            {
                ["p1.json"] = """{ "version": 1, "page": 1, "totalPages": 1, "items": [] }""",
            });

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("https://other.example/page.json")]
    [InlineData("pages/1.json?token=secret")]
    [InlineData("pages/1.json#fragment")]
    public void UnsafePageReferenceFailsClosed(string pageReference)
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            $$"""
            { "version": 1, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 0, "pages": ["{{pageReference}}"] }
            """,
            new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field.Contains("pages", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside.git")]
    [InlineData("https://other.example/plugin.git")]
    [InlineData("repositories/plugin.git?token=secret")]
    [InlineData("repositories/plugin.git#fragment")]
    public void UnsafePluginSourceFailsClosed(string source)
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            """
            { "version": 1, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 1, "pages": ["p1.json"] }
            """,
            new Dictionary<string, string>
            {
                ["p1.json"] = $$"""{ "version": 1, "page": 1, "totalPages": 1, "items": [{ "id": "plugin", "source": "{{source}}", "reference": "v1", "skills": [], "agents": [] }] }""",
            });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Message.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyCatalogProjectsWithoutFabricatingComponents()
    {
        var result = _adapter.Project(
            "https://catalog.example/marketplace/",
            """
            { "version": 1, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 0, "pages": [] }
            """,
            new Dictionary<string, string>());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Empty(result.Value!.Catalog.Plugins);
    }

    [Fact]
    public void NativeParserRemainsStrictForGeneratedShape()
    {
        var result = new PluginManifestParser().ParseMarketplace("""
            { "version": 1, "name": "sample-marketplace", "owner": "Publisher", "totalItems": 0, "pages": [] }
            """);

        Assert.False(result.IsValid);
    }
}
