using System.IO.Abstractions;
using System.Net;
using Azure.Core;
using Azure.Identity;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.MicrosoftFoundry;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class MicrosoftFoundryHealthCompositionTests
{
    [Theory]
    [InlineData(false, ProviderHealthStatus.Healthy)]
    [InlineData(true, ProviderHealthStatus.Unhealthy)]
    public async Task CheckAsync_RealFoundryThroughRegistryAndGateway_OwnsEntraPolicyEvenWithStaleGenericKey(
        bool failCredential, ProviderHealthStatus expected)
    {
        var configuration = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["foundry-instance"] = new()
                {
                    Type = "microsoft-foundry", ApiKey = "stale-generic-key",
                    Authentication = new ProviderAuthenticationConfig { Type = "entra-default" }
                }
            }
        };
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(configuration);
        var authentication = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, Substitute.For<IFileSystem>());
        var credential = new TestCredential(failCredential);
        var handler = new TestHandler();
        using var provider = new MicrosoftFoundryResponsesProvider(
            [new("foundry-instance", new Uri("https://example.services.ai.azure.com"), MicrosoftFoundryAuthentication.Entra(credential))],
            _ => handler, NullLogger<MicrosoftFoundryResponsesProvider>.Instance);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var models = new ModelRegistry();
        models.Register("foundry-instance", new LlmModel("deployment", "Deployment", provider.Api, "foundry-instance",
            "https://example.services.ai.azure.com/openai/v1", false, ["text"], new ModelCost(0, 0, 0, 0), 128000, 4096));
        var services = new ServiceCollection();
        services.AddSingleton(providers);
        services.AddSingleton(models);
        services.AddSingleton(authentication);
        services.AddSingleton<ILogger<DefaultProviderHealthCheck>>(NullLogger<DefaultProviderHealthCheck>.Instance);
        services.AddProviderHealthCheck();
        using var container = services.BuildServiceProvider();

        var result = await container.GetRequiredService<IProviderHealthCheck>().CheckAsync("foundry-instance");

        result.Status.ShouldBe(expected);
        result.ModelCount.ShouldBe(1);
        result.ProviderId.ShouldBe("foundry-instance");
        result.HasCredentials.ShouldBe(!failCredential);
        credential.Calls.ShouldBe(1);
        handler.Sends.ShouldBe(failCredential ? 0 : 1);
        if (failCredential)
        {
            result.Error.ShouldNotBeNull();
            result.Error.ShouldContain("Token acquisition failed");
            result.Error.ShouldNotContain("synthetic-secret");
        }
        else
        {
            handler.Authorization.ShouldBe("Bearer synthetic-secret");
            handler.HasApiKey.ShouldBeFalse();
        }
    }

    private sealed class TestCredential(bool fail) : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            requestContext.Scopes.ShouldBe(new[] { MicrosoftFoundryAuthenticationResolver.EntraAudience });
            if (fail)
                throw new AuthenticationFailedException("synthetic-secret");
            return ValueTask.FromResult(new AccessToken("synthetic-secret", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public string? Authorization { get; private set; }
        public bool HasApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            request.RequestUri.ShouldBe(new Uri("https://example.services.ai.azure.com/openai/v1/models"));
            Authorization = request.Headers.Authorization?.ToString();
            HasApiKey = request.Headers.Contains("api-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
