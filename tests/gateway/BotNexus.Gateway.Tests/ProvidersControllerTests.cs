using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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

    [Fact]
    public void GetProviders_NamedBuiltInInstance_ProjectsInstanceAndTypeWithoutCredential()
    {
        var modelFilter = new Mock<IModelFilter>();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["copilot-work"]);
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["copilot-work"] = new()
                {
                    Type = "github-copilot",
                    ApiKey = "auth:copilot-work"
                }
            }
        };
        var options = new Mock<IOptionsMonitor<PlatformConfig>>();
        options.SetupGet(value => value.CurrentValue).Returns(config);
        var controller = new ProvidersController(modelFilter.Object, platformConfig: options.Object);

        var result = controller.GetProviders();

        var provider = ((IEnumerable<ProviderInfo>)((OkObjectResult)result.Result!).Value!)
            .ShouldHaveSingleItem();
        provider.Name.ShouldBe("copilot-work");
        provider.ProviderId.ShouldBe("copilot-work");
        provider.Id.ShouldBe("copilot-work");
        provider.Type.ShouldBe("github-copilot");
        typeof(ProviderInfo).GetProperties().Select(property => property.Name)
            .ShouldNotContain("ApiKey");
    }

    [Fact]
    public void GetProviders_CanonicalProviderWithoutConfiguration_UsesProviderAsType()
    {
        var modelFilter = new Mock<IModelFilter>();
        modelFilter.Setup(filter => filter.GetProviders()).Returns(["github-copilot"]);
        var controller = new ProvidersController(modelFilter.Object);

        var result = controller.GetProviders();

        var provider = ((IEnumerable<ProviderInfo>)((OkObjectResult)result.Result!).Value!)
            .ShouldHaveSingleItem();
        provider.Type.ShouldBe("github-copilot");
    }

}
