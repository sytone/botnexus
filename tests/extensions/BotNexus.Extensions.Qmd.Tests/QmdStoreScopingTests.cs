using BotNexus.Agent.Core.Types;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class QmdStoreScopingTests
{
    private readonly InMemoryQmdBackend _backend = new();

    private static string GetText(AgentToolResult result) =>
        result.Content[0].Value;

    // --- QmdConfig.IsStoreAllowed ---

    [Fact]
    public void IsStoreAllowed_NullAllowedStores_ReturnsTrue()
    {
        var config = new QmdConfig { AllowedStores = null };
        Assert.True(config.IsStoreAllowed("any-store"));
    }

    [Fact]
    public void IsStoreAllowed_EmptyAllowedStores_ReturnsTrue()
    {
        var config = new QmdConfig { AllowedStores = [] };
        Assert.True(config.IsStoreAllowed("any-store"));
    }

    [Fact]
    public void IsStoreAllowed_StoreInList_ReturnsTrue()
    {
        var config = new QmdConfig { AllowedStores = ["projects", "resources"] };
        Assert.True(config.IsStoreAllowed("projects"));
    }

    [Fact]
    public void IsStoreAllowed_StoreNotInList_ReturnsFalse()
    {
        var config = new QmdConfig { AllowedStores = ["projects", "resources"] };
        Assert.False(config.IsStoreAllowed("secrets"));
    }

    [Fact]
    public void IsStoreAllowed_CaseInsensitive()
    {
        var config = new QmdConfig { AllowedStores = ["Projects"] };
        Assert.True(config.IsStoreAllowed("projects"));
    }

    // --- KnowledgeSearchTool access control ---

    [Fact]
    public async Task Search_DeniedStore_ReturnsAccessDenied()
    {
        var config = new QmdConfig { AllowedStores = ["projects"] };
        var tool = new KnowledgeSearchTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1",
            new Dictionary<string, object?> { ["query"] = "test", ["store"] = "secrets" });

        var text = GetText(result);
        Assert.Contains("Access denied", text);
        Assert.Contains("secrets", text);
    }

    [Fact]
    public async Task Search_AllowedStore_Succeeds()
    {
        var config = new QmdConfig { AllowedStores = ["projects"] };
        var tool = new KnowledgeSearchTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1",
            new Dictionary<string, object?> { ["query"] = "test", ["store"] = "projects" });

        var text = GetText(result);
        Assert.DoesNotContain("Access denied", text);
    }

    [Fact]
    public async Task Search_NoStoreSpecified_NoRestriction()
    {
        var config = new QmdConfig { AllowedStores = ["projects"] };
        var tool = new KnowledgeSearchTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1",
            new Dictionary<string, object?> { ["query"] = "test" });

        var text = GetText(result);
        Assert.DoesNotContain("Access denied", text);
    }

    // --- KnowledgeStoresTool filtering ---

    [Fact]
    public async Task Stores_WithAllowedStores_FiltersOutput()
    {
        _backend.SetStores([
            new QmdStoreInfo("projects", "/p", null, 5, null, true),
            new QmdStoreInfo("secrets", "/s", null, 3, null, true),
            new QmdStoreInfo("resources", "/r", null, 8, null, true)
        ]);

        var config = new QmdConfig { AllowedStores = ["projects", "resources"] };
        var tool = new KnowledgeStoresTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>());

        var text = GetText(result);
        Assert.Contains("projects", text);
        Assert.Contains("resources", text);
        Assert.DoesNotContain("secrets", text);
    }

    [Fact]
    public async Task Stores_NoAllowedStores_ShowsAll()
    {
        _backend.SetStores([
            new QmdStoreInfo("projects", "/p", null, 5, null, true),
            new QmdStoreInfo("secrets", "/s", null, 3, null, true)
        ]);

        var config = new QmdConfig { AllowedStores = null };
        var tool = new KnowledgeStoresTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>());

        var text = GetText(result);
        Assert.Contains("projects", text);
        Assert.Contains("secrets", text);
    }

    // --- KnowledgeGetTool access control ---

    [Fact]
    public async Task Get_DeniedStore_ReturnsAccessDenied()
    {
        _backend.SetDocuments([
            new QmdDocument("#abc", "secrets", "secrets/file.md", "Secret", "content")
        ]);

        var config = new QmdConfig { AllowedStores = ["projects"] };
        var tool = new KnowledgeGetTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1",
            new Dictionary<string, object?> { ["id"] = "#abc" });

        var text = GetText(result);
        Assert.Contains("Access denied", text);
        Assert.Contains("secrets", text);
    }

    [Fact]
    public async Task Get_AllowedStore_Succeeds()
    {
        _backend.SetDocuments([
            new QmdDocument("#abc", "projects", "projects/file.md", "Project", "content")
        ]);

        var config = new QmdConfig { AllowedStores = ["projects"] };
        var tool = new KnowledgeGetTool(_backend, config);

        var result = await tool.ExecuteAsync("tc1",
            new Dictionary<string, object?> { ["id"] = "#abc" });

        var text = GetText(result);
        Assert.DoesNotContain("Access denied", text);
        Assert.Contains("content", text);
    }
}
