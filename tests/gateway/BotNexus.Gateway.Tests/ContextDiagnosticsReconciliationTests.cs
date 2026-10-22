using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>Exercises the live diagnostic path without starting a host, accessing stores, or calling a provider.</summary>
public sealed class ContextDiagnosticsReconciliationTests
{
    [Theory]
    [InlineData("lookup", "private description", "{\"type\":\"object\"}")]
    [InlineData("検索", "漢字かな説明", "{\"description\":\"検索条件漢字\"}")]
    [InlineData("lookup", null, "{}")]
    [InlineData("x", "", "{}")]
    public async Task GetContextDiagnostics_ToolSubtotalIncludesNameDescriptionAndScriptWeightedSchema(
        string name, string? description, string schema)
    {
        var tool = ToolFor(name, description, schema);
        await using var handle = CreateHandle(tools: [tool]);
        var diag = handle.GetContextDiagnostics().ShouldNotBeNull();
        var units = TokenEstimator.WeightedCharUnits(name) + TokenEstimator.WeightedCharUnits(description)
            + TokenEstimator.WeightedCharUnits(schema);

        diag.ToolDefinitionChars.ShouldBe(name.Length + (description?.Length ?? 0) + schema.Length);
        diag.ToolDefinitionTokens.ShouldBe(TokenEstimator.TokensFromUnits(units));
        diag.TotalEstimatedTokens.ShouldBe(diag.ToolDefinitionTokens);
        diag.Tools.ShouldHaveSingleItem().SchemaChars.ShouldBe(schema.Length);
        diag.Tools[0].Description.ShouldBe(description);
        diag.ToolCount.ShouldBe(1);
        if (name == "検索")
            diag.ToolDefinitionTokens.ShouldBeGreaterThan(TokenEstimator.EstimateTokensFromCharCount(diag.ToolDefinitionChars));
    }

    [Fact]
    public async Task GetContextDiagnostics_EmptyStateAndEmptyContentHaveZeroSubtotals()
    {
        await using var handle = CreateHandle();
        var diag = handle.GetContextDiagnostics().ShouldNotBeNull();
        diag.SystemPromptChars.ShouldBe(0);
        diag.ToolCount.ShouldBe(0);
        diag.ToolDefinitionChars.ShouldBe(0);
        diag.ToolDefinitionTokens.ShouldBe(0);
        diag.Tools.ShouldBeEmpty();
        diag.HistoryEntryCount.ShouldBe(0);
        diag.HistoryChars.ShouldBe(0);
        diag.HistoryTokens.ShouldBe(0);
        diag.UserAssistantTokens.ShouldBe(0);
        diag.ToolResultTokens.ShouldBe(0);
        diag.TotalEstimatedTokens.ShouldBe(0);

        using var response = Response(diag);
        var sections = response.RootElement.GetProperty("sections");
        sections.GetProperty("userAssistant").GetProperty("chars").GetInt32().ShouldBe(0);
        sections.GetProperty("toolResult").GetProperty("tokens").GetInt32().ShouldBe(0);
        response.RootElement.GetProperty("contextWindowTokens").ValueKind.ShouldBe(JsonValueKind.Null);
        response.RootElement.GetProperty("usagePercent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetContextDiagnostics_MixedHistoryReconcilesWithoutCountingEmbeddedSkillsAgain()
    {
        const string system = "SECRET_PROMPT <skills>embedded skill</skills>漢";
        var tool = ToolFor("SECRET_TOOL", "SECRET_DESCRIPTION", "{\"description\":\"SECRET_SCHEMA漢\"}");
        var completion = new SubAgentCompletionMessage
        {
            SubAgentId = "child", Status = "done", Summary = "SECRET_COMPLETION漢"
        };
        string[] text = ["SECRET_USER漢", "SECRET_ASSISTANT", "SECRET_SYSTEM", completion.Content];
        string[] results = ["SECRET_RESULT漢", "<skills>SECRET_SKILL</skills>"];
        AgentMessage[] messages =
        [
            new BotNexus.Agent.Core.Types.UserMessage(text[0]), new AssistantAgentMessage(text[1]),
            new SystemAgentMessage(text[2]), completion,
            new ToolResultAgentMessage("private-call", tool.Name,
                new AgentToolResult(results.Select(value => new AgentToolContent(AgentToolContentType.Text, value)).ToArray()))
        ];
        await using var handle = CreateHandle(system, [tool], messages);
        var diag = handle.GetContextDiagnostics().ShouldNotBeNull();
        var textUnits = text.Sum(TokenEstimator.WeightedCharUnits);
        var resultUnits = results.Sum(TokenEstimator.WeightedCharUnits);
        var toolUnits = TokenEstimator.WeightedCharUnits(tool.Name)
            + TokenEstimator.WeightedCharUnits(tool.Definition.Description)
            + TokenEstimator.WeightedCharUnits(tool.Definition.Parameters.GetRawText());
        diag.SystemPromptChars.ShouldBe(system.Length);
        diag.UserAssistantChars.ShouldBe(text.Sum(value => value.Length));
        diag.ToolResultChars.ShouldBe(results.Sum(value => value.Length));
        diag.HistoryChars.ShouldBe(diag.UserAssistantChars + diag.ToolResultChars);
        diag.UserAssistantTokens.ShouldBe(TokenEstimator.TokensFromUnits(textUnits));
        diag.ToolResultTokens.ShouldBe(TokenEstimator.TokensFromUnits(resultUnits));
        diag.HistoryTokens.ShouldBe(TokenEstimator.TokensFromUnits(textUnits + resultUnits));
        diag.TotalEstimatedTokens.ShouldBe(TokenEstimator.TokensFromUnits(
            TokenEstimator.WeightedCharUnits(system) + toolUnits + textUnits + resultUnits));
        diag.HistoryEntryCount.ShouldBe(5);

        using var response = Response(diag, 1000);
        var root = response.RootElement;
        root.GetProperty("agentId").GetString().ShouldBe("agent-4982");
        root.GetProperty("sessionId").GetString().ShouldBe("session-4982");
        root.GetProperty("totalEstimatedTokens").GetInt32().ShouldBe(diag.TotalEstimatedTokens);
        root.GetProperty("contextWindowTokens").GetInt32().ShouldBe(1000);
        root.GetProperty("usagePercent").GetDouble().ShouldBe(Math.Round(diag.TotalEstimatedTokens / 10d, 1));
        var sections = root.GetProperty("sections");
        sections.EnumerateObject().Select(property => property.Name).Order().ShouldBe(
            new[] { "conversationHistory", "systemPrompt", "toolDefinitions", "toolResult", "userAssistant" });
        AssertSection(sections, "systemPrompt", diag.SystemPromptTokens, "chars", diag.SystemPromptChars);
        AssertSection(sections, "toolDefinitions", diag.ToolDefinitionTokens, "toolCount", 1);
        AssertSection(sections, "conversationHistory", diag.HistoryTokens, "entryCount", 5);
        AssertSection(sections, "userAssistant", diag.UserAssistantTokens, "chars", diag.UserAssistantChars);
        AssertSection(sections, "toolResult", diag.ToolResultTokens, "chars", diag.ToolResultChars);
        var json = root.GetRawText();
        json.ShouldNotContain("SECRET");
        json.ShouldNotContain("embedded skill");
        json.ShouldNotContain("private-call");
        json.ShouldNotContain("description");
        json.ShouldNotContain("schema");
        json.ShouldNotContain("skills");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GetContextDiagnostics_IndependentTruncationIsBoundedNotForcedToSum(int fragmentLength)
    {
        var fragment = new string('a', fragmentLength);
        // Name + empty description + {} makes this category have the same remainder as the others.
        var tool = ToolFor(new string('t', fragmentLength + 2), "", "{}");
        await using var handle = CreateHandle(fragment, [tool],
        [
            new BotNexus.Agent.Core.Types.UserMessage(fragment),
            new ToolResultAgentMessage("call", tool.Name,
                new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, fragment)]))
        ]);
        var diag = handle.GetContextDiagnostics().ShouldNotBeNull();
        // Three independently truncated top-level sections can under-sum the total by at most 2.
        var topLevel = diag.SystemPromptTokens + diag.ToolDefinitionTokens + diag.HistoryTokens;
        (diag.TotalEstimatedTokens - topLevel).ShouldBeInRange(0, 2);
        // Four disjoint leaves can under-sum by at most 3, not including the history parent again.
        var leaves = diag.SystemPromptTokens + diag.ToolDefinitionTokens + diag.UserAssistantTokens + diag.ToolResultTokens;
        (diag.TotalEstimatedTokens - leaves).ShouldBeInRange(0, 3);
        (diag.HistoryTokens - diag.UserAssistantTokens - diag.ToolResultTokens).ShouldBeInRange(0, 1);
        diag.TotalEstimatedTokens.ShouldBe(fragmentLength + 1);
        (diag.TotalEstimatedTokens - leaves).ShouldBe(fragmentLength);
    }

    private static void AssertSection(JsonElement sections, string name, int tokens, string countName, int count)
    {
        var section = sections.GetProperty(name);
        section.EnumerateObject().Count().ShouldBe(2);
        section.GetProperty("tokens").GetInt32().ShouldBe(tokens);
        section.GetProperty(countName).GetInt32().ShouldBe(count);
    }

    private static JsonDocument Response(ContextDiagnostics diagnostics, int? window = null)
        => JsonDocument.Parse(JsonSerializer.Serialize(AgentsController.BuildContextResponse(
            "agent-4982", "session-4982", diagnostics, window), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static FakeTool ToolFor(string name, string? description, string schema)
    {
        // Deserialization models null descriptions observed at runtime despite Tool's non-null annotation.
        var definition = JsonSerializer.Deserialize<Tool>(JsonSerializer.Serialize(new
        {
            Name = name, Description = description, Parameters = new { }
        })).ShouldNotBeNull();
        using var parameters = JsonDocument.Parse(schema);
        return new FakeTool(definition with { Parameters = parameters.RootElement.Clone() });
    }

    private static InProcessAgentHandle CreateHandle(string system = "", IReadOnlyList<IAgentTool>? tools = null,
        IReadOnlyList<AgentMessage>? messages = null)
    {
        var model = new LlmModel("test-model", "test-model", "test-api", "test-provider", "https://example.invalid",
            false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
        var models = new ModelRegistry();
        models.Register(model.Provider, model);
        var providers = new ApiProviderRegistry();
        providers.Register(new UnusedProvider());
        var options = new AgentOptions(
            new AgentInitialState(SystemPrompt: system, Model: model, Tools: tools, Messages: messages), model,
            new LlmClient(providers, models), null, null,
            (_, _) => Task.FromResult<ProviderExecutionOptions?>(null), null, null,
            ToolExecutionMode.Parallel, null, null, new GenerationOptions(), QueueMode.All, QueueMode.All,
            SessionId: "session-4982");
        return new InProcessAgentHandle(new BotNexus.Agent.Core.Agent(options), AgentId.From("agent-4982"),
            SessionId.From("session-4982"), NullLogger.Instance, tools: tools);
    }

    private sealed class UnusedProvider : IApiProvider
    {
        public string Api => "test-api";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => throw new InvalidOperationException("Diagnostics must not invoke a provider.");
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
            => throw new InvalidOperationException("Diagnostics must not invoke a provider.");
    }

    private sealed class FakeTool(Tool definition) : IAgentTool
    {
        public string Name => definition.Name;
        public string Label => Name;
        public Tool Definition => definition;
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Diagnostics must not prepare tools.");
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => throw new InvalidOperationException("Diagnostics must not execute tools.");
    }
}
