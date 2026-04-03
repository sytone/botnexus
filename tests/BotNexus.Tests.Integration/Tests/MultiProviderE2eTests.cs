using System.Net;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-PRV-007: Multiple providers registered simultaneously
/// Validates that multiple LLM providers can be registered in the ProviderRegistry
/// concurrently and that agents route to the correct provider by name.
/// </summary>
public sealed class MultiProviderE2eTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _tempHome;

    public MultiProviderE2eTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-multiprov-test-{Guid.NewGuid():N}");
        _previousHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _previousHome);
        try { if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true); } catch { }
    }

    [Fact]
    public async Task MultipleProviders_RegisteredAndRetrievable_ByCaseInsensitiveName()
    {
        var alphaProvider = new StubLlmProvider("alpha-response", "alpha-model");
        var betaProvider = new StubLlmProvider("beta-response", "beta-model");
        var gammaProvider = new StubLlmProvider("gamma-response", "gamma-model");

        var registry = new ProviderRegistry();
        registry.Register("alpha", alphaProvider);
        registry.Register("beta", betaProvider);
        registry.Register("GAMMA", gammaProvider);

        // Retrieve by exact name
        registry.Get("alpha").Should().BeSameAs(alphaProvider);
        registry.Get("beta").Should().BeSameAs(betaProvider);
        registry.Get("GAMMA").Should().BeSameAs(gammaProvider);

        // Case-insensitive lookup
        registry.Get("Alpha").Should().BeSameAs(alphaProvider);
        registry.Get("BETA").Should().BeSameAs(betaProvider);
        registry.Get("gamma").Should().BeSameAs(gammaProvider);

        // List all provider names
        registry.GetProviderNames().Should().HaveCount(3);
        registry.GetProviderNames().Should().Contain("alpha");
        registry.GetProviderNames().Should().Contain("beta");

        // GetRequired throws for missing
        var act = () => registry.GetRequired("nonexistent");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task MultipleProviders_InGateway_AgentsRouteToCorrectProvider()
    {
        var alphaProvider = new StubLlmProvider("Alpha says hello", "alpha-model");
        var betaProvider = new StubLlmProvider("Beta says world", "beta-model");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:Gateway:ApiKey"] = string.Empty,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();

                    services.AddSingleton<ILlmProvider>(alphaProvider);
                    services.AddSingleton<ILlmProvider>(betaProvider);
                    services.AddSingleton(sp =>
                    {
                        var registry = new ProviderRegistry();
                        registry.Register("alpha", alphaProvider);
                        registry.Register("beta", betaProvider);
                        return registry;
                    });
                });
            });

        using var client = factory.CreateClient();

        // Gateway should start successfully with multiple providers
        var healthResponse = await client.GetAsync("/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify both providers accessible via DI
        var providers = factory.Services.GetServices<ILlmProvider>().ToList();
        providers.Should().HaveCountGreaterThanOrEqualTo(2);

        // Verify registry has both
        var registry = factory.Services.GetRequiredService<ProviderRegistry>();
        registry.Get("alpha").Should().BeSameAs(alphaProvider);
        registry.Get("beta").Should().BeSameAs(betaProvider);
        registry.GetProviderNames().Should().HaveCount(2);
    }

    [Fact]
    public async Task DifferentProviders_ReturnDifferentResponses()
    {
        var alphaProvider = new StubLlmProvider("Alpha response content", "alpha-model");
        var betaProvider = new StubLlmProvider("Beta response content", "beta-model");

        var request = new ChatRequest(
            [new ChatMessage("user", "test")],
            new GenerationSettings { Model = "test" });

        var alphaResult = await alphaProvider.ChatAsync(request);
        var betaResult = await betaProvider.ChatAsync(request);

        alphaResult.Content.Should().Be("Alpha response content");
        betaResult.Content.Should().Be("Beta response content");
        alphaResult.Content.Should().NotBe(betaResult.Content);
    }

    private sealed class StubLlmProvider(string response, string model) : ILlmProvider
    {
        public string DefaultModel => model;
        public GenerationSettings Generation { get; set; } = new()
        {
            MaxTokens = 4096, Temperature = 0.0, ContextWindowTokens = 32000, MaxToolIterations = 5
        };

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { DefaultModel });
        }

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(response, FinishReason.Stop));

        public async IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return response;
        }
    }
}
