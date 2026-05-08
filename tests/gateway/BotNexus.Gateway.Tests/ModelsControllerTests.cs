using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ModelsControllerTests
{
    [Fact]
    public void GetModels_WhenNoModelsAvailable_ReturnsEmptyList()
    {
        var modelFilter = CreateModelFilter();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["openai"]);
        modelFilter.Setup(filter => filter.GetModels("openai")).Returns([]);
        var controller = CreateController(modelFilter.Object);

        var result = controller.GetModels();

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.ShouldNotBeNull();
        models!.ShouldBeEmpty();
    }

    [Fact]
    public void GetModels_WhenModelsAvailable_ReturnsAllModels()
    {
        var modelFilter = CreateModelFilter();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["openai", "anthropic"]);
        modelFilter.Setup(filter => filter.GetModels("openai")).Returns([
            new LlmModelInfo("gpt-4o", "GPT-4o", "openai")
        ]);
        modelFilter.Setup(filter => filter.GetModels("anthropic")).Returns([
            new LlmModelInfo("claude-sonnet-4", "Claude Sonnet 4", "anthropic")
        ]);

        var controller = CreateController(modelFilter.Object);

        var result = controller.GetModels();

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.ShouldNotBeNull();
        models!.Select(model => model.Name).OrderBy(n => n).ShouldBe(new[] { "Claude Sonnet 4", "GPT-4o" });
    }

    [Fact]
    public void GetModels_ReturnsModelsSortedAlphabeticallyByName()
    {
        var modelFilter = CreateModelFilter();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["anthropic"]);
        modelFilter.Setup(filter => filter.GetModels("anthropic")).Returns([
            new LlmModelInfo("claude-sonnet-4", "Claude Sonnet 4", "anthropic"),
            new LlmModelInfo("claude-haiku", "Anthropic Haiku", "anthropic")
        ]);

        var controller = CreateController(modelFilter.Object);

        var result = controller.GetModels();

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.ShouldNotBeNull();
        models!.Select(model => model.Name).ShouldBe(new[] { "Anthropic Haiku", "Claude Sonnet 4" });
    }

    [Fact]
    public void GetModels_WhenProviderSpecified_UsesOnlySpecifiedProvider()
    {
        var modelFilter = CreateModelFilter();
        modelFilter.Setup(filter => filter.GetModels("openai")).Returns([
            new LlmModelInfo("gpt-4o", "GPT-4o", "openai")
        ]);
        var controller = CreateController(modelFilter.Object);

        var result = controller.GetModels(provider: "openai");

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.ShouldNotBeNull();
        models!.ShouldAllBe(model => model.Provider == "openai");
        modelFilter.Verify(filter => filter.GetModels("openai"), Times.Once);
        modelFilter.Verify(filter => filter.GetProviders(), Times.Never);
    }

    [Fact]
    public void GetModels_WhenAgentSpecified_ReturnsAgentFilteredModels()
    {
        var modelFilter = CreateModelFilter();
        modelFilter.Setup(filter => filter.GetModelsForAgent("openai", It.IsAny<IReadOnlyList<string>>())).Returns([
            new LlmModelInfo("gpt-4o", "GPT-4o", "openai")
        ]);
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(registry => registry.Get("agent-a")).Returns(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ApiProvider = "openai",
            ModelId = "gpt-4o",
            AllowedModelIds = ["gpt-4o"]
        });
        var controller = new ModelsController(modelFilter.Object, agentRegistry.Object);

        var result = controller.GetModels(agentId: "agent-a");

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.ShouldNotBeNull();
        models!.ShouldHaveSingleItem().ModelId.ShouldBe("gpt-4o");
        modelFilter.Verify(filter => filter.GetModelsForAgent("openai", It.Is<IReadOnlyList<string>>(list => list.SequenceEqual(new[] { "gpt-4o" }))), Times.Once);
    }

    [Fact]
    public void GetModels_WhenAgentNotFound_ReturnsNotFound()
    {
        var modelFilter = CreateModelFilter();
        var controller = CreateController(modelFilter.Object);

        var result = controller.GetModels(agentId: "missing-agent");

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void GetAgentModels_DelegatesToAgentFilteredModelsEndpoint()
    {
        var modelFilter = CreateModelFilter();
        modelFilter.Setup(filter => filter.GetModelsForAgent("openai", It.IsAny<IReadOnlyList<string>>())).Returns([
            new LlmModelInfo("gpt-4o", "GPT-4o", "openai")
        ]);
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(registry => registry.Get("agent-a")).Returns(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ApiProvider = "openai",
            ModelId = "gpt-4o"
        });
        var controller = new ModelsController(modelFilter.Object, agentRegistry.Object);

        var result = controller.GetAgentModels("agent-a");

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.ShouldNotBeNull();
        models!.Where(model => model.ModelId == "gpt-4o").ShouldHaveSingleItem();
    }

    private static Mock<IModelFilter> CreateModelFilter()
    {
        return new Mock<IModelFilter>(MockBehavior.Strict);
    }

    private static ModelsController CreateController(IModelFilter modelFilter)
    {
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(registry => registry.Get(It.IsAny<BotNexus.Domain.Primitives.AgentId>())).Returns((AgentDescriptor?)null);
        return new ModelsController(modelFilter, agentRegistry.Object);
    }
}
