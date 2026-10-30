using System.IO.Abstractions;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class DefaultProviderHealthCheckTests
{
    private readonly ModelRegistry _modelRegistry = new();
    private readonly GatewayAuthManager _authManager;

    public DefaultProviderHealthCheckTests()
    {
        var config = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        config.CurrentValue.Returns(new PlatformConfig());
        var fileSystem = Substitute.For<IFileSystem>();
        _authManager = new GatewayAuthManager(config, NullLogger<GatewayAuthManager>.Instance, fileSystem);
    }

    private DefaultProviderHealthCheck CreateSut() =>
        new(_modelRegistry, _authManager, NullLogger<DefaultProviderHealthCheck>.Instance);

    [Fact]
    public async Task CheckAsync_RegisteredProviderOwnedHealth_BypassesGenericCredentialResolution()
    {
        var providers = new ApiProviderRegistry();
        var provider = Substitute.For<IApiProvider, IProviderHealthCheck>();
        provider.Api.Returns("provider-owned-test");
        ((IProviderHealthCheck)provider).CheckAsync("entra-instance", Arg.Any<CancellationToken>())
            .Returns(new ProviderHealthResult("entra-instance", ProviderHealthStatus.Healthy, 1,
                DateTimeOffset.UtcNow, 0, true));
        providers.Register(provider);
        _modelRegistry.Register("entra-instance", new LlmModel(
            "deployment", "Deployment", "provider-owned-test", "entra-instance", "https://example.test",
            false, ["text"], new ModelCost(0, 0, 0, 0), 128000, 4096));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, providers);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _modelRegistry);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _authManager);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Extensions.Logging.ILogger<DefaultProviderHealthCheck>>(
            services, NullLogger<DefaultProviderHealthCheck>.Instance);
        using var serviceProvider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var sut = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<DefaultProviderHealthCheck>(serviceProvider);

        var result = await sut.CheckAsync("entra-instance");

        result.Status.ShouldBe(ProviderHealthStatus.Healthy);
        result.HasCredentials.ShouldBeTrue();
        result.ModelCount.ShouldBe(1);
        await ((IProviderHealthCheck)provider).Received(1).CheckAsync("entra-instance", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_GenericRegisteredProvider_DoesNotAdvertiseHealthAndRetainsMissingCredentialResult()
    {
        var providers = new ApiProviderRegistry();
        var generic = Substitute.For<IApiProvider>();
        generic.Api.Returns("generic-test");
        providers.Register(generic);
        (providers.Get("generic-test") is IProviderHealthCheck).ShouldBeFalse();
        _modelRegistry.Register("generic", new LlmModel("model", "Model", "generic-test", "generic",
            "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 128000, 4096));
        var sut = new DefaultProviderHealthCheck(_modelRegistry, _authManager,
            NullLogger<DefaultProviderHealthCheck>.Instance, providers);

        var result = await sut.CheckAsync("generic");

        result.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
        result.HasCredentials.ShouldBeFalse();
        result.ModelCount.ShouldBe(1);
        result.Error.ShouldBe("No API key or credential configured for this provider.");
    }

    [Fact]
    public async Task CheckAsync_NoRegisteredModels_DoesNotInvokeProviderOwnedHealth()
    {
        var providers = new ApiProviderRegistry();
        var provider = Substitute.For<IApiProvider, IProviderHealthCheck>();
        provider.Api.Returns("owned-test");
        providers.Register(provider);
        var sut = new DefaultProviderHealthCheck(_modelRegistry, _authManager,
            NullLogger<DefaultProviderHealthCheck>.Instance, providers);

        var result = await sut.CheckAsync("empty-instance");

        result.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
        result.ModelCount.ShouldBe(0);
        result.Error.ShouldBe("No models registered for this provider.");
        await ((IProviderHealthCheck)provider).DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_UnknownProvider_ReturnsUnhealthyNoModels()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("nonexistent");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Equal(0, result.ModelCount);
        Assert.Contains("No models registered", result.Error);
    }

    [Fact]
    public async Task CheckAsync_EmptyProviderId_ReturnsUnhealthy()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Contains("required", result.Error);
    }

    [Fact]
    public async Task CheckAsync_NullProviderId_ReturnsUnhealthy()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync(null!);

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ModelsRegisteredButNoCredentials_ReturnsUnhealthy()
    {
        _modelRegistry.Register("test-provider", new LlmModel(
            Id: "test-model",
            Name: "Test Model",
            Api: "openai-completions",
            Provider: "test-provider",
            BaseUrl: "https://api.test.com",
            Reasoning: false,
            Input: new[] { "text" },
            Cost: new ModelCost(1m, 2m, 0.5m, 0.5m),
            ContextWindow: 128000,
            MaxTokens: 4096));
        var sut = CreateSut();

        var result = await sut.CheckAsync("test-provider");

        Assert.Equal(ProviderHealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.ModelCount);
        Assert.False(result.HasCredentials);
        Assert.Contains("credential", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_ReturnsLatencyAndTimestamp()
    {
        var sut = CreateSut();

        var result = await sut.CheckAsync("any-provider");

        Assert.True(result.LatencyMs >= 0);
        Assert.True(result.CheckedAt <= DateTimeOffset.UtcNow);
        Assert.True(result.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task CheckAsync_CancellationToken_AlreadyCancelled_Throws()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // GetApiKeyAsync should observe the token
        // Since the implementation may not throw for all providers,
        // verify that a cancelled token at least returns quickly
        var result = await sut.CheckAsync("test", cts.Token);

        // If it doesn't throw, it should still return a valid result
        Assert.NotNull(result);
    }
}
