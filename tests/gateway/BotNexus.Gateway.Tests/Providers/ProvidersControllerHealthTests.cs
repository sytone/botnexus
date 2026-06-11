using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class ProvidersControllerHealthTests
{
    private readonly IModelFilter _modelFilter = Substitute.For<IModelFilter>();
    private readonly IProviderHealthCheck _healthCheck = Substitute.For<IProviderHealthCheck>();

    private ProvidersController CreateSut() => new(_modelFilter, _healthCheck);

    [Fact]
    public async Task CheckHealth_ProviderNotFound_Returns404()
    {
        _modelFilter.GetProviders().Returns(new List<string> { "copilot" });
        var sut = CreateSut();

        var result = await sut.CheckHealth("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CheckHealth_HealthyProvider_Returns200()
    {
        _modelFilter.GetProviders().Returns(new List<string> { "copilot" });
        _healthCheck.CheckAsync("copilot", Arg.Any<CancellationToken>())
            .Returns(new ProviderHealthResult("copilot", ProviderHealthStatus.Healthy, 42, DateTimeOffset.UtcNow, 5, true));
        var sut = CreateSut();

        var result = await sut.CheckHealth("copilot", CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ProviderHealthResponse>(okResult.Value);
        Assert.Equal("healthy", response.Status);
        Assert.Equal(42, response.LatencyMs);
        Assert.Equal(5, response.Models);
        Assert.True(response.HasCredentials);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task CheckHealth_UnhealthyProvider_Returns503()
    {
        _modelFilter.GetProviders().Returns(new List<string> { "anthropic" });
        _healthCheck.CheckAsync("anthropic", Arg.Any<CancellationToken>())
            .Returns(new ProviderHealthResult("anthropic", ProviderHealthStatus.Unhealthy, 10, DateTimeOffset.UtcNow, 3, false, "No credentials"));
        var sut = CreateSut();

        var result = await sut.CheckHealth("anthropic", CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, statusResult.StatusCode);
        var response = Assert.IsType<ProviderHealthResponse>(statusResult.Value);
        Assert.Equal("unhealthy", response.Status);
        Assert.Equal("No credentials", response.Error);
    }

    [Fact]
    public async Task CheckHealth_NoHealthCheckService_Returns404()
    {
        var sut = new ProvidersController(_modelFilter, healthCheck: null);

        var result = await sut.CheckHealth("copilot", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetProviders_StillWorks()
    {
        _modelFilter.GetProviders().Returns(new List<string> { "copilot", "anthropic" });
        var sut = CreateSut();

        var result = sut.GetProviders();

        var okResult = Assert.IsType<ActionResult<IEnumerable<ProviderInfo>>>(result);
        var providers = Assert.IsAssignableFrom<IEnumerable<ProviderInfo>>(((OkObjectResult)okResult.Result!).Value);
        Assert.Equal(2, providers.Count());
    }
}
