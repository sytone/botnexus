using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class ScopedCompactionCeilingTests
{
    [Fact]
    public async Task Resolver_PrecedenceResetSiblingAndWorldIsolation()
    {
        var agentId = AgentId.From("ceiling-agent");
        var registry = new Mock<IAgentRegistry>();
        var descriptor = new AgentDescriptor { AgentId = agentId, DisplayName = "A", ModelId = "m", ApiProvider = "p", CompactionTokenCeiling = 2000 };
        registry.Setup(r => r.Get(agentId)).Returns(() => descriptor);
        var store = new InMemoryConversationStore();
        var c = new Conversation { AgentId = agentId, ConversationId = ConversationId.From("ceiling-c"), WorldId = "local-world", CompactionTokenCeiling = 1000 };
        var sibling = new Conversation { AgentId = agentId, ConversationId = ConversationId.From("ceiling-sibling"), WorldId = "local-world" };
        await store.CreateAsync(c);
        await store.CreateAsync(sibling);
        var config = new PlatformConfig { Gateway = new GatewaySettingsConfig { World = new BotNexus.Domain.WorldIdentity { Id = "local-world", Name = "Local" } }, World = new WorldSettingsConfig { CompactionTokenCeiling = 3000 } };
        var monitor = new TestOptionsMonitor<PlatformConfig>(config);
        var resolver = new SessionCompactionCeilingResolver(NullLogger<SessionCompactionCeilingResolver>.Instance, registry.Object, store, monitor);
        var resolved = await resolver.ResolveAsync(agentId, c.ConversationId);
        resolved.Tokens.ShouldBe(1000);
        resolved.Source.ShouldBe("conversation");
        (await resolver.ResolveAsync(agentId, sibling.ConversationId)).Tokens.ShouldBe(2000);
        await store.PatchOverrideAsync(c.ConversationId, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(null) });
        (await resolver.ResolveAsync(agentId, c.ConversationId)).Source.ShouldBe("agent");
        descriptor = descriptor with { CompactionTokenCeiling = null };
        (await resolver.ResolveAsync(agentId, c.ConversationId)).Tokens.ShouldBe(3000);
        config.World.CompactionTokenCeiling = 4000;
        (await resolver.ResolveAsync(agentId, c.ConversationId)).Tokens.ShouldBe(4000);
        c.WorldId = "other-world";
        c.CompactionTokenCeiling = null;
        await store.SaveAsync(c);
        (await resolver.ResolveAsync(agentId, c.ConversationId)).Tokens.ShouldBeNull();
        config.World.CompactionTokenCeiling = null;
        (await resolver.ResolveAsync(agentId, sibling.ConversationId)).Tokens.ShouldBeNull();
    }

    [Fact]
    public void Configuration_RoundtripAndInvalidRawWrites()
    {
        var config = JsonSerializer.Deserialize<PlatformConfig>("{\"world\":{\"compactionTokenCeiling\":5000},\"agents\":{\"a\":{\"provider\":\"p\",\"model\":\"m\",\"compactionTokenCeiling\":1000}}}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        config.ShouldNotBeNull();
        config.World.ShouldNotBeNull();
        config.World.CompactionTokenCeiling.ShouldBe(5000);
        foreach (var value in new[] { "0", "-1", "1.5", "2147483648", "\"1000\"" })
        {
            PlatformConfigLoader.ValidateRawJson("{\"world\":{\"compactionTokenCeiling\":" + value + "}}").ShouldNotBeEmpty();
            PlatformConfigLoader.ValidateRawJson("{\"agents\":{\"a\":{\"provider\":\"p\",\"model\":\"m\",\"compactionTokenCeiling\":" + value + "}}}").ShouldNotBeEmpty();
        }
        PlatformConfigLoader.ValidateRawJson("{\"world\":{\"compactionTokenCeiling\":null}}").ShouldBeEmpty();
    }

    [Fact]
    public async Task ConversationPersistence_RoundtripPatchResetAndInvalidWrites()
    {
        var store = new InMemoryConversationStore();
        var c = new Conversation { AgentId = AgentId.From("a"), ConversationId = ConversationId.From("ceiling-persist"), CompactionTokenCeiling = 1234 };
        await store.CreateAsync(c);
        var first = await store.GetAsync(c.ConversationId);
        first.ShouldNotBeNull();
        first.CompactionTokenCeiling.ShouldBe(1234);
        await store.PatchOverrideAsync(c.ConversationId, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(5678) });
        var second = await store.GetAsync(c.ConversationId);
        second.ShouldNotBeNull();
        second.CompactionTokenCeiling.ShouldBe(5678);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.PatchOverrideAsync(c.ConversationId, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(0) }));
        await store.PatchOverrideAsync(c.ConversationId, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(null) });
        var reset = await store.GetAsync(c.ConversationId);
        reset.ShouldNotBeNull();
        reset.CompactionTokenCeiling.ShouldBeNull();
    }
    [Fact]
    public async Task FileAndSqlite_ReloadPreservesCeilingAndReset()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "ceiling-files");
        await VerifyStore(new FileConversationStore(root, NullLogger<FileConversationStore>.Instance, fs),
            () => new FileConversationStore(root, NullLogger<FileConversationStore>.Instance, fs));
        var db = Path.Combine(Path.GetTempPath(), "ceiling-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var cs = "Data Source=" + db + ";Pooling=False";
            await VerifyStore(new SqliteConversationStore(cs, NullLogger<SqliteConversationStore>.Instance),
                () => new SqliteConversationStore(cs, NullLogger<SqliteConversationStore>.Instance));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(db + suffix)) File.Delete(db + suffix);
        }
    }

    private static async Task VerifyStore(IConversationStore store, Func<IConversationStore> reopen)
    {
        var id = ConversationId.From("ceiling-durable");
        await store.CreateAsync(new Conversation { AgentId = AgentId.From("a"), ConversationId = id, CompactionTokenCeiling = 1234 });
        var loaded = await reopen().GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded.CompactionTokenCeiling.ShouldBe(1234);
        await store.PatchOverrideAsync(id, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(5678) });
        loaded = await reopen().GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded.CompactionTokenCeiling.ShouldBe(5678);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.PatchOverrideAsync(id, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(-1) }));
        await store.PatchOverrideAsync(id, new ConversationOverridePatch { CompactionTokenCeiling = FieldUpdate<int?>.Set(null) });
        loaded = await reopen().GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded.CompactionTokenCeiling.ShouldBeNull();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.CreateAsync(new Conversation { AgentId = AgentId.From("a"), ConversationId = ConversationId.From("bad"), CompactionTokenCeiling = 0 }));
    }

    [Fact]
    public async Task Api_WriteReadResetOmissionAndInvalidValues()
    {
        var store = new InMemoryConversationStore();
        var id = ConversationId.From("ceiling-api");
        await store.CreateAsync(new Conversation { AgentId = AgentId.From("a"), ConversationId = id });
        var controller = new BotNexus.Gateway.Api.Controllers.ConversationsController(store, new InMemorySessionStore());
        var result = await controller.SetOverride(id.Value, new BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest(CompactionTokenCeiling: 2345, ApplyCompactionTokenCeiling: true), CancellationToken.None);
        var response = result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Value.ShouldBeOfType<BotNexus.Gateway.Api.Controllers.ConversationResponse>();
        response.CompactionTokenCeiling.ShouldBe(2345);
        await controller.SetOverride(id.Value, new BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest(), CancellationToken.None);
        var loaded = await store.GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded.CompactionTokenCeiling.ShouldBe(2345);
        foreach (var invalid in new[] { 0, -1 })
            (await controller.SetOverride(id.Value, new BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest(CompactionTokenCeiling: invalid, ApplyCompactionTokenCeiling: true), CancellationToken.None)).ShouldBeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        foreach (var invalid in new[] { "1.5", "2147483648", "\"1000\"" })
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest>("{\"CompactionTokenCeiling\":" + invalid + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await controller.SetOverride(id.Value, new BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest(ApplyCompactionTokenCeiling: true), CancellationToken.None);
        loaded = await store.GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded.CompactionTokenCeiling.ShouldBeNull();
    }

    [Fact]
    public async Task CanonicalConfigWriter_RejectsInvalidCeilingsEvenOnRawMutation()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(Path.GetTempPath(), "ceiling-config", "config.json");
        var writer = new PlatformConfigWriter(path, fs);
        await writer.MutateAsync(root => root["world"] = new System.Text.Json.Nodes.JsonObject { ["compactionTokenCeiling"] = 3456 }, "test");
        (await writer.ReadPlatformConfigAsync()).World.ShouldNotBeNull();
        var config = await writer.ReadPlatformConfigAsync();
        config.World.ShouldNotBeNull();
        config.World.CompactionTokenCeiling.ShouldBe(3456);
        foreach (var invalid in new[] { "0", "-1", "1.5", "2147483648", "\"1000\"" })
            await Should.ThrowAsync<InvalidOperationException>(() => writer.MutateAsync(root => root["world"] = System.Text.Json.Nodes.JsonNode.Parse("{\"compactionTokenCeiling\":" + invalid + "}"), "test"));
        config = await writer.ReadPlatformConfigAsync();
        config.World.ShouldNotBeNull();
        config.World.CompactionTokenCeiling.ShouldBe(3456);
        await writer.MutateAsync(root => root["world"] = new System.Text.Json.Nodes.JsonObject { ["compactionTokenCeiling"] = null }, "test");
        config = await writer.ReadPlatformConfigAsync();
        config.World.ShouldNotBeNull();
        config.World.CompactionTokenCeiling.ShouldBeNull();
    }
    [Fact]
    public Task CanonicalConfigWriter_RejectsDuplicateWorldScopes()
        => VerifyAmbiguousCeilings([
            "{\"world\":{\"compactionTokenCeiling\":100},\"World\":{\"compactionTokenCeiling\":-1}}",
            "{\"World\":{\"compactionTokenCeiling\":null},\"world\":{\"compactionTokenCeiling\":100}}",
            "{\"world\":null,\"World\":{\"compactionTokenCeiling\":100}}"]);

    [Fact]
    public Task CanonicalConfigWriter_RejectsDuplicateAgentScopes()
        => VerifyAmbiguousCeilings([
            "{\"agents\":{\"a\":{\"compactionTokenCeiling\":100}},\"Agents\":{\"a\":{\"compactionTokenCeiling\":-1}}}",
            "{\"Agents\":{\"a\":{\"compactionTokenCeiling\":null}},\"agents\":{\"a\":{\"compactionTokenCeiling\":100}}}",
            "{\"agents\":null,\"Agents\":{\"a\":{\"compactionTokenCeiling\":100}}}"]);

    [Fact]
    public Task CanonicalConfigWriter_RejectsDuplicateCeilingKeysIncludingNull()
        => VerifyAmbiguousCeilings([
            "{\"world\":{\"compactionTokenCeiling\":null,\"CompactionTokenCeiling\":-1}}",
            "{\"world\":{\"CompactionTokenCeiling\":100,\"compactionTokenCeiling\":null}}",
            "{\"agents\":{\"a\":{\"compactionTokenCeiling\":100,\"COMPACTIONTOKENCEILING\":-1}}}",
            "{\"agents\":{\"a\":{\"CompactionTokenCeiling\":null,\"compactionTokenCeiling\":100}}}"]);

    private static async Task VerifyAmbiguousCeilings(string[] documents)
    {
        foreach (var document in documents)
        {
            var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
            var path = Path.Combine(Path.GetTempPath(), "ceiling-ambiguity", "config.json");
            var writer = new PlatformConfigWriter(path, fs);
            await writer.MutateAsync(root => root["world"] = new System.Text.Json.Nodes.JsonObject { ["compactionTokenCeiling"] = 3456 }, "seed");
            var before = fs.File.ReadAllText(path);
            await Should.ThrowAsync<InvalidOperationException>(() => writer.MutateAsync(root =>
            {
                root.Clear();
                var candidate = System.Text.Json.Nodes.JsonNode.Parse(document).ShouldBeOfType<System.Text.Json.Nodes.JsonObject>();
                foreach (var entry in candidate) root[entry.Key] = entry.Value?.DeepClone();
            }, "ambiguous", namedSections: ["world", "World", "agents", "Agents"]));
            fs.File.ReadAllText(path).ShouldBe(before);
        }
    }

    [Fact]
    public async Task CanonicalConfigWriter_CaseInsensitiveSingleKeysAndUnrelatedLegacyRemainAccepted()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var writer = new PlatformConfigWriter(Path.Combine(Path.GetTempPath(), "ceiling-case", "config.json"), fs);
        await writer.MutateAsync(root =>
        {
            root["World"] = System.Text.Json.Nodes.JsonNode.Parse("{\"COMPACTIONTOKENCEILING\":100}");
            root["Agents"] = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":{\"CompactionTokenCeiling\":200}}");
            root["legacy"] = System.Text.Json.Nodes.JsonNode.Parse("{\"key\":1,\"Key\":2}");
        }, "valid");
        await Should.ThrowAsync<InvalidOperationException>(() => writer.MutateAsync(root => root["World"] =
            System.Text.Json.Nodes.JsonNode.Parse("{\"COMPACTIONTOKENCEILING\":-1}"), "invalid"));
        await writer.MutateAsync(root =>
        {
            root.Clear();
            root["world"] = new System.Text.Json.Nodes.JsonObject();
            root["World"] = new System.Text.Json.Nodes.JsonObject();
            root["agents"] = new System.Text.Json.Nodes.JsonObject();
            root["Agents"] = new System.Text.Json.Nodes.JsonObject();
        }, "legacy-no-ceiling", namedSections: ["world", "World", "agents", "Agents", "legacy"]);
    }

    [Fact]
    public async Task Api_CeilingWritesRequireResendingModelThinkingAndContextToPreserveThem()
    {
        var store = new InMemoryConversationStore();
        var id = ConversationId.From("ceiling-api-semantics");
        await store.CreateAsync(new Conversation
        {
            AgentId = AgentId.From("a"), ConversationId = id, ModelOverride = "custom-model",
            ThinkingOverride = "high", ContextWindowOverride = 8192, ToolOverrideJson = "existing-overlay"
        });
        var controller = new BotNexus.Gateway.Api.Controllers.ConversationsController(store, new InMemorySessionStore());
        var result = await controller.SetOverride(id.Value, new BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest(
            Model: "custom-model", Thinking: "high", ContextWindow: 8192, CompactionTokenCeiling: 2345, ApplyCompactionTokenCeiling: true), CancellationToken.None);
        var response = result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Value.ShouldBeOfType<BotNexus.Gateway.Api.Controllers.ConversationResponse>();
        response.ModelOverride.ShouldBe("custom-model");
        response.ThinkingOverride.ShouldBe("high");
        response.ContextWindowOverride.ShouldBe(8192);
        response.CompactionTokenCeiling.ShouldBe(2345);
        response.ToolOverrideJson.ShouldBe("existing-overlay");
        result = await controller.SetOverride(id.Value, new BotNexus.Gateway.Api.Controllers.SetConversationOverrideRequest(
            CompactionTokenCeiling: 3456, ApplyCompactionTokenCeiling: true), CancellationToken.None);
        response = result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Value.ShouldBeOfType<BotNexus.Gateway.Api.Controllers.ConversationResponse>();
        response.ModelOverride.ShouldBeNull();
        response.ThinkingOverride.ShouldBeNull();
        response.ContextWindowOverride.ShouldBeNull();
        response.CompactionTokenCeiling.ShouldBe(3456);
        response.ToolOverrideJson.ShouldBe("existing-overlay");
    }

    [Fact]
    public async Task AgentWriterSource_RoundtripResetAndInvalidValues()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "ceiling-agent-config");
        var writer = new PlatformConfigWriter(Path.Combine(root, "config.json"), fs);
        var agentWriter = new PlatformConfigAgentWriter(writer, new BotNexusHome(fs, root));
        var descriptor = new AgentDescriptor { AgentId = AgentId.From("ceiling-agent"), DisplayName = "A", ModelId = "m", ApiProvider = "p", CompactionTokenCeiling = 3456 };
        await agentWriter.SaveAsync(descriptor);
        var config = await writer.ReadPlatformConfigAsync();
        var source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), root, NullLogger<PlatformConfigAgentSource>.Instance);
        (await source.LoadAsync()).Single().CompactionTokenCeiling.ShouldBe(3456);
        foreach (var invalid in new[] { 0, -1 })
        {
            var bad = descriptor with { CompactionTokenCeiling = invalid };
            BotNexus.Gateway.Agents.AgentDescriptorValidator.Validate(bad).ShouldContain(e => e.Contains("CompactionTokenCeiling"));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => agentWriter.SaveAsync(bad));
        }
        await agentWriter.SaveAsync(descriptor with { CompactionTokenCeiling = null });
        config = await writer.ReadPlatformConfigAsync();
        source = new PlatformConfigAgentSource(new TestOptionsMonitor<PlatformConfig>(config), root, NullLogger<PlatformConfigAgentSource>.Instance);
        (await source.LoadAsync()).Single().CompactionTokenCeiling.ShouldBeNull();
        foreach (var invalid in new[] { "0", "-1", "1.5", "2147483648", "\"1000\"" })
            await Should.ThrowAsync<InvalidOperationException>(() => writer.MutateAsync(r => r["agents"] = System.Text.Json.Nodes.JsonNode.Parse("{\"ceiling-agent\":{\"provider\":\"p\",\"model\":\"m\",\"compactionTokenCeiling\":" + invalid + "}}"), "test"));
    }

    [Fact]
    public async Task Resolver_CancellationAndStoreFailureDoNotRelaxPolicy()
    {
        var registry = new Mock<IAgentRegistry>();
        var store = new Mock<IConversationStore>();
        var id = ConversationId.From("ceiling-failure");
        store.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("store unavailable"));
        var resolver = new SessionCompactionCeilingResolver(NullLogger<SessionCompactionCeilingResolver>.Instance, registry.Object, store.Object, new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()));
        await Should.ThrowAsync<IOException>(() => resolver.ResolveAsync(AgentId.From("a"), id));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => resolver.ResolveAsync(AgentId.From("a"), id, cts.Token));
    }
    [Fact]
    public async Task AgentApi_InvalidCeilingRejectsBeforeRegistryMutation()
    {
        var registry = new Mock<IAgentRegistry>();
        var id = AgentId.From("ceiling-api-agent");
        var original = new AgentDescriptor { AgentId = id, DisplayName = "A", ModelId = "m", ApiProvider = "p", CompactionTokenCeiling = 1000 };
        registry.Setup(r => r.Get(id)).Returns(original);
        var supervisor = new Mock<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>();
        var controller = new BotNexus.Gateway.Api.Controllers.AgentsController(registry.Object, supervisor.Object, Mock.Of<IAgentConfigurationWriter>());
        foreach (var invalid in new[] { 0, -1 })
        {
            var result = await controller.Update(id.Value, original with { CompactionTokenCeiling = invalid }, CancellationToken.None);
            result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        }
        registry.Verify(r => r.Update(It.IsAny<AgentId>(), It.IsAny<AgentDescriptor>()), Times.Never);
        foreach (var invalid in new[] { "1.5", "2147483648", "\"1000\"" })
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<AgentDescriptor>("{\"agentId\":\"a\",\"displayName\":\"A\",\"modelId\":\"m\",\"apiProvider\":\"p\",\"compactionTokenCeiling\":" + invalid + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
