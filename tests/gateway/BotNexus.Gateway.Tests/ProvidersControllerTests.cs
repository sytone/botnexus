using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ProvidersControllerTests
{
    [Fact]
    public void GetProviders_WhenNoProvidersRegistered_ReturnsEmptyList()
    {
        var modelFilter = new Mock<IModelFilter>();
        modelFilter.Setup(filter => filter.GetProviders()).Returns([]);
        var controller = new ProvidersController(modelFilter.Object);

        var result = controller.GetProviders();

        var providers = (result.Result as OkObjectResult)?.Value as IEnumerable<ProviderInfo>;
        providers.ShouldNotBeNull();
        providers!.ShouldBeEmpty();
    }

    [Fact]
    public void GetProviders_WhenProvidersRegistered_ReturnsAllProviders()
    {
        var modelFilter = new Mock<IModelFilter>();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["openai", "anthropic"]);
        var controller = new ProvidersController(modelFilter.Object);

        var result = controller.GetProviders();

        var providers = (result.Result as OkObjectResult)?.Value as IEnumerable<ProviderInfo>;
        providers.ShouldNotBeNull();
        providers!.Select(p => p.Name).ShouldBe(new[] { "openai", "anthropic" });
    }

    [Fact]
    public void GetProviders_ReturnsProvidersSortedAlphabetically()
    {
        var modelFilter = new Mock<IModelFilter>();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["anthropic", "github-copilot", "openai"]);
        var controller = new ProvidersController(modelFilter.Object);

        var result = controller.GetProviders();

        var providers = (result.Result as OkObjectResult)?.Value as IEnumerable<ProviderInfo>;
        providers.ShouldNotBeNull();
        providers!.Select(p => p.Name).ShouldBe(new[] { "anthropic", "github-copilot", "openai" });
    }

}
