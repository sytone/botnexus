using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Tests;

public sealed class QuickModelConfigurationPatchTests
{
    [Theory]
    [InlineData("unknown", null)]
    [InlineData("disallowed", null)]
    [InlineData("next", "other")]
    public async Task ModelPatch_InvalidIdentityOrPolicy_LeavesCanonicalDocumentUnchanged(string model, string? provider)
    {
        await WithStore(async (writer, _) =>
        {
            var before = (await writer.ReadAsync()).ToJsonString();
            var patch = new AgentConfigurationPatch { Model = model };
            if (provider is not null)
                patch.Provider = provider;

            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"), patch, CancellationToken.None, Models());

            errors.ShouldNotBeEmpty();
            (await writer.ReadAsync()).ToJsonString().ShouldBe(before);
        });
    }

    [Fact]
    public async Task ModelPatch_ValidSelection_PreservesEveryOtherAuthoredAndUnknownValue()
    {
        await WithStore(async (writer, freshWriter) =>
        {
            var before = (await writer.ReadAsync()).DeepClone();
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch { Model = "next" }, CancellationToken.None, Models());

            errors.ShouldBeEmpty();
            var after = await freshWriter().ReadAsync();
            after["agents"]?["sample"]?["model"]?.GetValue<string>().ShouldBe("next");
            after["agents"]?["sample"]?.AsObject().Remove("model");
            before["agents"]?["sample"]?.AsObject().Remove("model");
            JsonNode.DeepEquals(before, after).ShouldBeTrue();
            var typed = await freshWriter().ReadPlatformConfigAsync();
            typed.Agents.ShouldNotBeNull()["sample"].Model.ShouldBe("next");
        });
    }

    [Fact]
    public async Task ModelPatch_ProviderPolicyDisallowsSelection_IsAtomic()
    {
        await WithStore(async (writer, _) =>
        {
            await writer.MutateAsync(root => root["providers"] = JsonNode.Parse("""{"test":{"chat":{"models":["current"]}}}"""), "test");
            var before = (await writer.ReadAsync()).ToJsonString();
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch { Model = "next" }, CancellationToken.None, Models());
            errors.ShouldNotBeEmpty();
            (await writer.ReadAsync()).ToJsonString().ShouldBe(before);
        });
    }

    [Theory]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("allowedModels")]
    [InlineData("inheritAllowedModels")]
    public async Task ModelPatch_MissingRegistry_FailsClosedWithoutChangingDocument(string field)
    {
        await WithStore(async (writer, _) =>
        {
            var before = (await writer.ReadAsync()).ToJsonString();
            var patch = field switch
            {
                "model" => new AgentConfigurationPatch { Model = "next" },
                "provider" => new AgentConfigurationPatch { Provider = "other" },
                "allowedModels" => new AgentConfigurationPatch { AllowedModels = ["current"] },
                _ => new AgentConfigurationPatch { Inherit = ["allowedModels"] },
            };
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"), patch, CancellationToken.None);
            string.Join("; ", errors).ShouldContain("registry");
            (await writer.ReadAsync()).ToJsonString().ShouldBe(before);
        });
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("provider")]
    public async Task ModelPatch_MissingRegistry_DoesNotBypassDocumentPolicy(string policy)
    {
        await WithStore(async (writer, _) =>
        {
            if (policy == "provider")
                await writer.MutateAsync(root => root["providers"] = JsonNode.Parse("""{"test":{"chat":{"models":["current"]}}}"""), "test");
            var before = (await writer.ReadAsync()).ToJsonString();
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch { Model = policy == "agent" ? "disallowed" : "next" }, CancellationToken.None);
            string.Join("; ", errors).ShouldContain("not allowed");
            (await writer.ReadAsync()).ToJsonString().ShouldBe(before);
        });
    }

    [Fact]
    public async Task ModelPatch_InheritAllowlist_ValidatesResultingSelection()
    {
        await WithStore(async (writer, _) =>
        {
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch { Inherit = ["allowedModels"] }, CancellationToken.None, Models());
            errors.ShouldBeEmpty();
            (await writer.ReadAsync())["agents"]?["sample"]?.AsObject().ContainsKey("allowedModels").ShouldBeFalse();
        });
    }

    [Fact]
    public async Task ModelPatch_UnrelatedEdit_DoesNotRequireRegistry()
    {
        await WithStore(async (writer, _) =>
        {
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch { Description = "updated" }, CancellationToken.None);
            errors.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task ModelPatch_ControllerWithoutRegistry_ReturnsActionableBadRequestAtomically()
    {
        await WithStore(async (writer, _) =>
        {
            var before = (await writer.ReadAsync()).ToJsonString();
            var controller = new BotNexus.Gateway.Api.Controllers.AgentsController(
                Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentRegistry>(),
                Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentSupervisor>(),
                Moq.Mock.Of<BotNexus.Gateway.Abstractions.Agents.IAgentConfigurationWriter>());
            var result = await controller.PatchConfiguration("sample", new AgentConfigurationPatch { Model = "next" }, writer, CancellationToken.None);
            var rejected = result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
            System.Text.Json.JsonSerializer.Serialize(rejected.Value).ShouldContain("registry");
            (await writer.ReadAsync()).ToJsonString().ShouldBe(before);
        });
    }

    [Fact]
    public async Task ModelPatch_AuthoredAlias_PreservesProviderAndValidatesRegisteredIdentity()
    {
        await WithStore(async (writer, freshWriter) =>
        {
            await writer.MutateAsync(root =>
            {
                var agent = root["agents"]?["sample"]?.AsObject() ?? throw new InvalidOperationException();
                agent["provider"] = "copilot";
            }, "test");
            var models = new ModelRegistry();
            models.Register("github-copilot", new LlmModel("next", "Next", "completions", "github-copilot", "https://example.invalid",
                false, ["text"], new(0m, 0m, 0m, 0m), 128_000, 16_000));
            var errors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch { Model = "next" }, CancellationToken.None, models);
            errors.ShouldBeEmpty();
            (await freshWriter().ReadAsync())["agents"]?["sample"]?["provider"]?.GetValue<string>().ShouldBe("copilot");
        });
    }

    private static ModelRegistry Models()
    {
        var registry = new ModelRegistry();
        foreach (var id in new[] { "current", "next", "disallowed" })
            registry.Register("test", new LlmModel(id, id, "completions", "test", "https://example.invalid",
                false, ["text"], new(0m, 0m, 0m, 0m), 128_000, 16_000));
        return registry;
    }

    private static async Task WithStore(Func<PlatformConfigWriter, Func<PlatformConfigWriter>, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-4952-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = Path.Combine(directory, "config.db");
            var projection = Path.Combine(directory, "config.json");
            var store = new SqliteConfigStore($"Data Source={db};Pooling=False");
            await store.WriteDocumentAsync(JsonNode.Parse("""
                {"agents":{"sample":{"model":"current","provider":"test","allowedModels":["current","next"],
                "thinking":null,"description":"keep","memory":{"enabled":true,"future":7},
                "toolPolicy":{"denied":["delete"],"future":true},"unknown":{"nested":[1,null,"keep"]}}}}
                """)?.AsObject() ?? throw new InvalidOperationException());
            PlatformConfigWriter Writer() => new(projection, new FileSystem(), null,
                new SqliteConfigurationWriter(store, db), store);
            await action(Writer(), Writer);
            File.Exists(projection).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
