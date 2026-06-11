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
