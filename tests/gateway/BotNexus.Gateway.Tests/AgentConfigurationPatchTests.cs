using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests;

public sealed class AgentConfigurationPatchTests
{
    // Reflection lets the first-written regression execute against the pre-fix assembly too.
    private static async Task<IActionResult> Patch(PlatformConfigWriter writer, string json, string id = "sample")
    {
        var method = typeof(AgentsController).GetMethod("PatchConfiguration");
        Assert.NotNull(method);
        var request = JsonSerializer.Deserialize(json, method.GetParameters()[1].ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var controller = new AgentsController(
            Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(),
            Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(),
            Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>());
        var invocation = method.Invoke(controller, [id, request, writer, CancellationToken.None]);
        var task = Assert.IsAssignableFrom<Task<IActionResult>>(invocation);
        return await task;
    }

    [Fact]
    public async Task Patch_PreservesUnknownsAndUneditedPolicies_AndFreshReadSeesAllEdits()
    {
        await WithConfig(async (writer, path) =>
        {
            var result = await Patch(writer, """{"toolTimeoutSeconds":91,"sessionAccess":{"level":"allowlist","allowedAgents":["peer"]},"conversationAccess":{"level":"all","allowedAgents":[]},"toolPolicy":{"alwaysApprove":["shell"],"neverApprove":["read"],"denied":["delete"]}}""");
            Assert.IsType<OkObjectResult>(result);
            // A later unrelated save must not replace the descriptor/policies.
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"displayName":"Renamed"}"""));
            var fresh = new PlatformConfigWriter(path, new FileSystem());
            var root = await fresh.ReadAsync();
            var agent = Assert.IsType<JsonObject>(root["agents"]?["sample"]);
            Assert.Equal("keep", agent["metadata"]?["unknown"]?.GetValue<string>());
            Assert.Equal("keep", agent["sessionAccess"]?["future"]?.GetValue<string>());
            Assert.Equal("deny", agent["toolPolicy"]?["askFallback"]?.GetValue<string>());
            var config = await fresh.ReadPlatformConfigAsync();
            var typed = (config.Agents ?? throw new InvalidOperationException())["sample"];
            Assert.Equal(91, typed.ToolTimeoutSeconds);
            Assert.Equal("allowlist", typed.SessionAccess?.Level);
            Assert.Equal(new[] { "peer" }, typed.SessionAccess?.AllowedAgents);
            Assert.Equal("all", typed.ConversationAccess?.Level);
            Assert.Empty(typed.ConversationAccess?.AllowedAgents ?? throw new InvalidOperationException());
            Assert.Equal(new[] { "shell" }, typed.ToolPolicy?.AlwaysApprove);
            Assert.Equal(new[] { "read" }, typed.ToolPolicy?.NeverApprove);
            Assert.Equal(new[] { "delete" }, typed.ToolPolicy?.Denied);
        });
    }

    [Fact]
    public async Task Patch_NullPersistsRuntimeFallback_EmptyListClears_RemoveDeletes_AbsentIsNoOp()
    {
        await WithConfig(async (writer, _) =>
        {
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"toolTimeoutSeconds":null,"sessionAccess":{"allowedAgents":[]},"conversationAccess":null,"toolPolicy":{"denied":null}}"""));
            var agent = Assert.IsType<JsonObject>((await writer.ReadAsync())["agents"]?["sample"]);
            Assert.True(agent.ContainsKey("toolTimeoutSeconds"));
            Assert.Null(agent["toolTimeoutSeconds"]);
            Assert.True(agent.ContainsKey("conversationAccess"));
            Assert.Null(agent["conversationAccess"]);
            Assert.Empty(Assert.IsType<JsonArray>(agent["sessionAccess"]?["allowedAgents"]));
            Assert.True(Assert.IsType<JsonObject>(agent["toolPolicy"]).ContainsKey("denied"));
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"inherit":["toolTimeoutSeconds","conversationAccess","toolPolicy.denied"]}"""));
            agent = Assert.IsType<JsonObject>((await writer.ReadAsync())["agents"]?["sample"]);
            Assert.False(agent.ContainsKey("toolTimeoutSeconds"));
            Assert.False(agent.ContainsKey("conversationAccess"));
            Assert.False(Assert.IsType<JsonObject>(agent["toolPolicy"]).ContainsKey("denied"));
        });
    }

    [Theory]
    [InlineData("{\"toolTimeoutSeconds\":-1}")]
    [InlineData("{\"toolTimeoutSeconds\":0}")]
    [InlineData("{\"thinking\":\"not-a-level\"}")]
    [InlineData("{\"displayName\":\" \"}")]
    [InlineData("{\"contextWindow\":0}")]
    [InlineData("{\"sessionAccess\":{\"level\":\"invalid\"}}")]
    [InlineData("{\"conversationAccess\":{\"allowedAgents\":[\"\"]}}")]
    [InlineData("{\"toolPolicy\":{\"denied\":[\"\"]}}")]
    public async Task Patch_InvalidBatchLeavesExactBytes(string invalid)
    {
        await WithConfig(async (writer, path) =>
        {
            var before = await File.ReadAllTextAsync(path);
            var payload = invalid.Insert(1, "\"displayName\":\"Must not save\",");
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, payload));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.IsType<NotFoundResult>(await Patch(writer, "{}", "missing"));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        });
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("conflicting")]
    public async Task Patch_SqliteOnly_FreshCanonicalReadAndConsumersIgnoreProjection(string projection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4974-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = Path.Combine(directory, "config.db");
        var path = Path.Combine(directory, "config.json");
        try
        {
            var store = new BotNexus.Gateway.Configuration.Store.SqliteConfigStore($"Data Source={db};Pooling=False");
            await store.WriteDocumentAsync(JsonNode.Parse("""{"agents":{"sample":{"provider":"test","model":"test","metadata":{"unknown":"keep"},"toolPolicy":{"askFallback":"deny","future":"keep"}}}}""")?.AsObject() ?? throw new InvalidOperationException());
            if (projection != "absent") await File.WriteAllTextAsync(path, projection == "corrupt" ? "not-json" : """{"agents":{"sample":{"model":"wrong","toolTimeoutSeconds":1}}}""");
            var projectionBefore = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            PlatformConfigWriter Writer() => new(path, new FileSystem(), null,
                new BotNexus.Gateway.Configuration.Writers.SqliteConfigurationWriter(store, db), store);
            Assert.IsType<OkObjectResult>(await Patch(Writer(), """{"toolTimeoutSeconds":91,"sessionAccess":{"level":"allowlist","allowedAgents":["peer"]},"conversationAccess":{"level":"allowlist","allowedAgents":["peer"]},"toolPolicy":{"alwaysApprove":["read"],"neverApprove":["shell"],"denied":["delete"]}}"""));
            Assert.IsType<OkObjectResult>(await Patch(Writer(), """{"description":"unrelated"}"""));
            var canonical = await Writer().ReadPlatformConfigAsync();
            var agent = (canonical.Agents ?? throw new InvalidOperationException())["sample"];
            Assert.Equal(91, agent.ToolTimeoutSeconds);
            Assert.Equal("test", agent.Model);
            var controller = new AgentsController(Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(), Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(), Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>());
            var get = typeof(AgentsController).GetMethod("GetConfiguration") ?? throw new InvalidOperationException();
            var read = await (Task<IActionResult>)(get.Invoke(controller, ["sample", Writer(), CancellationToken.None]) ?? throw new InvalidOperationException());
            var body = JsonNode.Parse(Assert.IsType<ContentResult>(read).Content ?? "")?.AsObject() ?? throw new InvalidOperationException();
            Assert.Equal(91, body["toolTimeoutSeconds"]?.GetValue<int>());
            Assert.Null(body["toolPolicy"]?["future"]);
            Assert.False(body.ContainsKey("metadata"));
            Assert.Equal(projectionBefore, File.Exists(path) ? await File.ReadAllTextAsync(path) : null);

            var monitor = new Moq.Mock<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
            monitor.Setup(m => m.CurrentValue).Returns(canonical);
            var policy = new BotNexus.Gateway.Security.DefaultToolPolicyProvider(monitor.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<BotNexus.Gateway.Security.DefaultToolPolicyProvider>.Instance);
            Assert.True(policy.RequiresApproval("read", "sample"));
            Assert.False(policy.RequiresApproval("shell", "sample"));
            Assert.False(policy.IsToolAvailable("delete", BotNexus.Domain.Primitives.AgentId.From("sample")));
            var source = new PlatformConfigAgentSource(monitor.Object, directory, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformConfigAgentSource>.Instance);
            var descriptor = Assert.Single(await source.LoadAsync());
            Assert.Equal(91, descriptor.Metadata["toolTimeoutSeconds"]);
            var sessions = new Moq.Mock<BotNexus.Gateway.Abstractions.Sessions.ISessionStore>();
            sessions.Setup(m => m.ListSummaryPageAsync(Moq.It.IsAny<BotNexus.Gateway.Abstractions.Sessions.SessionSummaryQuery>(), Moq.It.IsAny<CancellationToken>()))
                .ReturnsAsync(BotNexus.Gateway.Abstractions.Sessions.SessionSummaryPage.Empty);
            var sessionTool = new BotNexus.Gateway.Tools.SessionTool(sessions.Object, descriptor.AgentId,
                Enum.Parse<BotNexus.Gateway.Tools.SessionAccessLevel>(descriptor.SessionAccessLevel, true), descriptor.SessionAllowedAgents);
            await sessionTool.ExecuteAsync("allowed", new Dictionary<string, object?> { ["action"] = "list", ["agentId"] = "peer" });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessionTool.ExecuteAsync("denied", new Dictionary<string, object?> { ["action"] = "list", ["agentId"] = "stranger" }));
            var conversations = new Moq.Mock<BotNexus.Gateway.Abstractions.Conversations.IConversationStore>();
            conversations.Setup(m => m.ListAsync(Moq.It.IsAny<BotNexus.Domain.Primitives.AgentId>(), Moq.It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var conversationTool = new BotNexus.Gateway.Tools.ConversationTool(conversations.Object, descriptor.AgentId, accessLevel:
                Enum.Parse<BotNexus.Gateway.Tools.ConversationAccessLevel>(descriptor.ConversationAccessLevel, true), allowedAgents: descriptor.ConversationAllowedAgents);
            await conversationTool.ExecuteAsync("allowed", new Dictionary<string, object?> { ["action"] = "list", ["agentId"] = "peer" });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => conversationTool.ExecuteAsync("denied", new Dictionary<string, object?> { ["action"] = "list", ["agentId"] = "stranger" }));
            // Invalid batches must not write even one row; a no-op must preserve all states.
            var before = (await Writer().ReadAsync()).ToJsonString();
            Assert.IsType<BadRequestObjectResult>(await Patch(Writer(), """{"description":"bad","toolTimeoutSeconds":-1}"""));
            Assert.IsType<BadRequestObjectResult>(await Patch(Writer(), """{"description":"bad","thinking":"not-a-level"}"""));
            Assert.IsType<BadRequestObjectResult>(await Patch(Writer(), """{"toolTimeoutSeconds":0}"""));
            Assert.IsType<OkObjectResult>(await Patch(Writer(), "{}"));
            Assert.Equal(before, (await Writer().ReadAsync()).ToJsonString());
            Assert.IsType<OkObjectResult>(await Patch(Writer(), """{"toolTimeoutSeconds":null,"conversationAccess":null,"sessionAccess":{"allowedAgents":[]},"toolPolicy":{"denied":null}}"""));
            var rows = await store.ReadEntriesAsync();
            Assert.Equal(BotNexus.Gateway.Configuration.Store.ConfigValueState.ExplicitNull, rows["agents.sample.toolTimeoutSeconds"].State);
            Assert.Equal(BotNexus.Gateway.Configuration.Store.ConfigValueState.ExplicitNull, rows["agents.sample.conversationAccess"].State);
            var cleared = Assert.IsType<JsonObject>((await Writer().ReadAsync())["agents"]?["sample"]);
            Assert.Empty(Assert.IsType<JsonArray>(cleared["sessionAccess"]?["allowedAgents"]));
            Assert.IsType<OkObjectResult>(await Patch(Writer(), """{"inherit":["toolTimeoutSeconds","conversationAccess","toolPolicy.denied"]}"""));
            rows = await store.ReadEntriesAsync();
            Assert.False(rows.ContainsKey("agents.sample.toolTimeoutSeconds"));
            Assert.False(rows.ContainsKey("agents.sample.conversationAccess"));
            Assert.False(rows.ContainsKey("agents.sample.toolPolicy.denied"));
            Assert.Equal(projectionBefore, File.Exists(path) ? await File.ReadAllTextAsync(path) : null);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Patch_RejectsUnknownFieldsAndConflictingInherit_AndNormalizesAgentIdCasing()
    {
        var requestType = typeof(AgentsController).GetMethod("PatchConfiguration")?.GetParameters()[1].ParameterType ?? throw new InvalidOperationException();
        foreach (var json in new[] { """{"metadata":{"unknown":"overwrite"}}""", """{"toolPolicy":{"unknown":[]}}""", """{"toolTimeoutSeconds":"91"}""", """{"enabled":"false"}""", """{"systemPrompt":"retired"}""", """{"memory":{"search":{"defaultTopK":"10"}}}""" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, requestType, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await WithConfig(async (writer, path) =>
        {
            await writer.MutateAsync(root =>
            {
                var agents = Assert.IsType<JsonObject>(root["agents"]);
                var stored = agents["sample"];
                agents.Remove("sample");
                agents["SaMpLe"] = stored;
            }, "test");
            var before = await File.ReadAllTextAsync(path);
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"toolTimeoutSeconds":5,"inherit":["toolTimeoutSeconds"]}"""));
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"inherit":["metadata.unknown"]}"""));
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"inherit":["toolPolicy"],"toolPolicy":{"denied":[]}}"""));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"toolTimeoutSeconds":1}""", "SAMPLE"));
            var root = await writer.ReadAsync();
            Assert.Equal(1, root["agents"]?["SaMpLe"]?["toolTimeoutSeconds"]?.GetValue<int>());
            Assert.Equal("SaMpLe", Assert.Single(Assert.IsType<JsonObject>(root["agents"])).Key);
            var controller = new AgentsController(Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(),
                Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(), Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>());
            var read = Assert.IsType<ContentResult>(await controller.GetConfiguration("sample", writer, CancellationToken.None));
            Assert.Equal(1, JsonNode.Parse(read.Content ?? "")?["toolTimeoutSeconds"]?.GetValue<int>());
            var config = await writer.ReadPlatformConfigAsync();
            Assert.Empty(PlatformConfigAgentSource.ValidateCandidate(config, BotNexus.Domain.Primitives.AgentId.From("SaMpLe")));
            var monitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
            monitor.Setup(m => m.CurrentValue).Returns(config);
            var source = new PlatformConfigAgentSource(monitor.Object, Path.GetDirectoryName(path) ?? throw new InvalidOperationException(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformConfigAgentSource>.Instance);
            var descriptor = Assert.Single(await source.LoadAsync());
            Assert.Equal("SaMpLe", descriptor.AgentId.Value);
            Assert.Equal("SaMpLe", descriptor.DisplayName);
        });
    }

    [Fact]
    public async Task Patch_OtherEditorSettingsSurviveLaterPolicyEdits_WithoutLosingOpaqueSiblings()
    {
        await WithConfig(async (writer, _) =>
        {
            await writer.MutateAsync(root =>
            {
                var agent = Assert.IsType<JsonObject>(root["agents"]?["sample"]);
                agent["heartbeat"] = JsonNode.Parse("""{"ackMaxChars":333,"future":{"secret":"keep"}}""");
                agent["extensions"] = JsonNode.Parse("""{"custom":{"token":"keep"}}""");
                agent["isolationOptions"] = JsonNode.Parse("""{"future":"keep"}""");
            }, "test");
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"description":"Updated","emoji":"X","thinking":"high","toolIds":["read"],"allowedModels":["test"],"memory":{"search":{"defaultTopK":7}},"heartbeat":{"intervalMinutes":15},"soul":{"timezone":"UTC"}}"""));
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"toolPolicy":{"denied":["shell"]},"sessionAccess":{"level":"all"}}"""));
            var agent = Assert.IsType<JsonObject>((await writer.ReadAsync())["agents"]?["sample"]);
            Assert.Equal("Updated", agent["description"]?.GetValue<string>());
            Assert.Equal("high", agent["thinking"]?.GetValue<string>());
            Assert.Equal("read", agent["toolIds"]?[0]?.GetValue<string>());
            Assert.Equal(7, agent["memory"]?["search"]?["defaultTopK"]?.GetValue<int>());
            Assert.Equal(15, agent["heartbeat"]?["intervalMinutes"]?.GetValue<int>());
            Assert.Equal(333, agent["heartbeat"]?["ackMaxChars"]?.GetValue<int>());
            Assert.Equal("keep", agent["heartbeat"]?["future"]?["secret"]?.GetValue<string>());
            Assert.Equal("keep", agent["extensions"]?["custom"]?["token"]?.GetValue<string>());
            Assert.Equal("keep", agent["isolationOptions"]?["future"]?.GetValue<string>());
            Assert.Equal("UTC", agent["soul"]?["timezone"]?.GetValue<string>());
        });
    }

    [Fact]
    public async Task Patch_NullAndRemovalUseRuntimeFallback_NotMergedDefaults_AndDisabledAgentsAreValidated()
    {
        await WithConfig(async (writer, path) =>
        {
            await writer.MutateAsync(root =>
            {
                var agents = Assert.IsType<JsonObject>(root["agents"]);
                agents["defaults"] = JsonNode.Parse("""{"toolTimeoutSeconds":999}""");
                var agent = Assert.IsType<JsonObject>(agents["sample"]);
                agent["enabled"] = false;
            }, "test");
            var before = await File.ReadAllTextAsync(path);
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"thinking":"not-a-level"}"""));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            foreach (var payload in new[] { """{"toolTimeoutSeconds":null}""", """{"inherit":["toolTimeoutSeconds"]}""" })
            {
                Assert.IsType<OkObjectResult>(await Patch(writer, payload));
                var config = await writer.ReadPlatformConfigAsync();
                var agent = (config.Agents ?? throw new InvalidOperationException())["sample"];
                agent.Enabled = true;
                var monitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
                monitor.Setup(m => m.CurrentValue).Returns(config);
                var source = new PlatformConfigAgentSource(monitor.Object, Path.GetDirectoryName(path) ?? throw new InvalidOperationException(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformConfigAgentSource>.Instance);
                var descriptor = Assert.Single(await source.LoadAsync());
                Assert.Equal(AgentDefaultsConfig.DefaultToolTimeoutSeconds, descriptor.Metadata["toolTimeoutSeconds"]);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Patch_EnabledLifecycle_PreservesOmissionsAndProjectsDisabledDescriptors(bool enabled)
    {
        await WithConfig(async (writer, path) =>
        {
            Assert.IsType<OkObjectResult>(await Patch(writer, enabled ? """{"enabled":true}""" : """{"enabled":false}"""));
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"description":"unrelated"}"""));
            var config = await writer.ReadPlatformConfigAsync();
            Assert.Equal(enabled, (config.Agents ?? throw new InvalidOperationException())["sample"].Enabled);
            var monitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<PlatformConfig>>();
            monitor.Setup(m => m.CurrentValue).Returns(config);
            var source = new PlatformConfigAgentSource(monitor.Object, Path.GetDirectoryName(path) ?? throw new InvalidOperationException(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformConfigAgentSource>.Instance);
            var descriptor = Assert.Single(await source.LoadAsync());
            Assert.Equal(enabled, descriptor.Enabled);
            Assert.Equal(30, descriptor.Metadata["toolTimeoutSeconds"]);
            var registry = new BotNexus.Gateway.Agents.DefaultAgentRegistry(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BotNexus.Gateway.Agents.DefaultAgentRegistry>.Instance);
            registry.Register(descriptor);
            Assert.Equal(enabled, registry.Get(descriptor.AgentId) is not null);
            Assert.NotNull(registry.Get(descriptor.AgentId, includeDisabled: true));
            var before = await File.ReadAllTextAsync(path);
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"enabled":true,"thinking":"not-a-level"}"""));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"enabled":null}"""));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"inherit":["enabled"]}"""));
            Assert.False(Assert.IsType<JsonObject>((await writer.ReadAsync())["agents"]?["sample"]).ContainsKey("enabled"));
            Assert.True(( (await writer.ReadPlatformConfigAsync()).Agents ?? throw new InvalidOperationException())["sample"].Enabled);
        });
    }

    [Fact]
    public async Task Patch_DeepRemovalUsesTypedPaths_AndRejectsConflictingParentWrites()
    {
        await WithConfig(async (writer, path) =>
        {
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"memory":{"search":{"temporalDecay":{"halfLifeDays":40,"enabled":true}}}}"""));
            Assert.IsType<OkObjectResult>(await Patch(writer, """{"inherit":["memory.search.temporalDecay.halfLifeDays"]}"""));
            var memory = (await writer.ReadAsync())["agents"]?["sample"]?["memory"];
            Assert.Null(memory?["search"]?["temporalDecay"]?["halfLifeDays"]);
            Assert.True(memory?["search"]?["temporalDecay"]?["enabled"]?.GetValue<bool>());
            var before = await File.ReadAllTextAsync(path);
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"memory":null,"inherit":["memory.search.temporalDecay.enabled"]}"""));
            Assert.IsType<BadRequestObjectResult>(await Patch(writer, """{"inherit":["memory.search.temporalDecay.unknown"]}"""));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task Patch_UsesSelectedModelCapabilities_BeforeSaving()
    {
        await WithConfig(async (writer, path) =>
        {
            var models = new BotNexus.Agent.Providers.Core.Registry.ModelRegistry();
            models.Register("test", new BotNexus.Agent.Providers.Core.Models.LlmModel(
                Id: "test", Name: "test", Api: "completions", Provider: "test", BaseUrl: "https://example.invalid",
                Reasoning: false, Input: ["text"], Cost: new(0m, 0m, 0m, 0m), ContextWindow: 128_000, MaxTokens: 16_000));
            var controller = new AgentsController(Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(),
                Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(),
                Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>(), modelRegistry: models);
            var before = await File.ReadAllTextAsync(path);
            foreach (var patch in new[] { new AgentConfigurationPatch { Thinking = "high" }, new AgentConfigurationPatch { ContextWindow = 1_000_000 } })
                Assert.IsType<BadRequestObjectResult>(await controller.PatchConfiguration("sample", patch, writer, CancellationToken.None));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.IsType<OkObjectResult>(await controller.PatchConfiguration("sample", new AgentConfigurationPatch { ContextWindow = 128_000 }, writer, CancellationToken.None));
        });
    }

    [Fact]
    public async Task GetConfiguration_ExcludesOpaqueSecrets_WithoutChangingStorage()
    {
        await WithConfig(async (writer, path) =>
        {
            await writer.MutateAsync(root =>
            {
                var agent = Assert.IsType<JsonObject>(root["agents"]?["sample"]);
                agent["extensions"] = JsonNode.Parse("""{"protected":{"apiKey":"extension-secret"}}""");
                agent["metadata"] = JsonNode.Parse("""{"token":"metadata-secret"}""");
                agent["enabled"] = false;
            }, "test");
            var before = await File.ReadAllTextAsync(path);
            var controller = new AgentsController(Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(),
                Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(), Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>());
            var result = Assert.IsType<ContentResult>(await controller.GetConfiguration("sample", writer, CancellationToken.None));
            var body = JsonNode.Parse(result.Content ?? "")?.AsObject() ?? throw new InvalidOperationException();
            Assert.False(body.ContainsKey("extensions"));
            Assert.False(body.ContainsKey("metadata"));
            Assert.False(body["enabled"]?.GetValue<bool>());
            Assert.DoesNotContain("secret", result.Content ?? "");
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public void OperatorEndpoints_HaveExplicitRoutesAndTypedPresenceContract()
    {
        var patch = typeof(AgentsController).GetMethod("PatchConfiguration") ?? throw new InvalidOperationException();
        Assert.Equal("{agentId}/configuration", Assert.Single(patch.GetCustomAttributes(typeof(HttpPatchAttribute), false).Cast<HttpPatchAttribute>()).Template);
        Assert.Equal(typeof(AgentConfigurationPatch), patch.GetParameters()[1].ParameterType);
        var get = typeof(AgentsController).GetMethod("GetConfiguration") ?? throw new InvalidOperationException();
        Assert.Equal("{agentId}/configuration", Assert.Single(get.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>()).Template);
    }

    private static async Task WithConfig(Func<PlatformConfigWriter, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4974-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, """{"agents":{"sample":{"provider":"test","model":"test","metadata":{"unknown":"keep"},"toolTimeoutSeconds":30,"sessionAccess":{"level":"own","future":"keep"},"toolPolicy":{"askFallback":"deny","denied":["old"]}}}}""");
            await action(new PlatformConfigWriter(path, new FileSystem()), path);
        }
        finally { Directory.Delete(directory, true); }
    }
}
