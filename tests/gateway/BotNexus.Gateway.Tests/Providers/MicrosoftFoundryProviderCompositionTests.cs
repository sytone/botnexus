using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class MicrosoftFoundryProviderCompositionTests
{
    [Fact]
    public void Register_ConfiguredEntraInstance_RegistersSelectableProviderAndDeployment()
    {
        var config = Config(
            authenticationType: "entra-default",
            apiKey: null,
            clientId: null);
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();

        MicrosoftFoundryProviderComposition.Register(
            config,
            providers,
            models,
            NullLoggerFactory.Instance);

        providers.Get("microsoft-foundry-responses").ShouldNotBeNull();
        var model = models.GetModel("azure-foundry-test", "example-deployment");
        model.ShouldNotBeNull();
        model.Api.ShouldBe("microsoft-foundry-responses");
        model.Provider.ShouldBe("azure-foundry-test");
        model.BaseUrl.ShouldBe("https://example.services.ai.azure.com/openai/v1");
    }

    [Theory]
    [InlineData("entra-default", null)]
    [InlineData("managed-identity", null)]
    [InlineData("user-assigned-managed-identity", "00000000-0000-0000-0000-000000000001")]
    public void Register_SupportedEntraModes_ShareProviderOwnedContract(string type, string? clientId)
    {
        var config = Config(type, apiKey: null, clientId);

        var count = MicrosoftFoundryProviderComposition.Register(
            config,
            new ApiProviderRegistry(),
            new ModelRegistry(),
            NullLoggerFactory.Instance);

        count.ShouldBe(1);
    }

    [Fact]
    public void Register_ApiKeyMode_RequiresApiKeyAndDoesNotUseEntraConfiguration()
    {
        var config = Config("api-key", apiKey: "example-secret", clientId: null);

        var count = MicrosoftFoundryProviderComposition.Register(
            config,
            new ApiProviderRegistry(),
            new ModelRegistry(),
            NullLoggerFactory.Instance);

        count.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://example.services.ai.azure.com/openai/v1")]
    [InlineData("https://example.services.ai.azure.com/not-openai/v1")]
    [InlineData("https://user@example.services.ai.azure.com/openai/v1")]
    public void Register_UnsafeInferenceEndpoint_FailsClosed(string baseUrl)
    {
        var config = Config("entra-default", apiKey: null, clientId: null);
        config.Providers!["azure-foundry-test"].BaseUrl = baseUrl;

        Action action = () => MicrosoftFoundryProviderComposition.Register(
            config,
            new ApiProviderRegistry(),
            new ModelRegistry(),
            NullLoggerFactory.Instance);

        action.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void Register_UserAssignedIdentityWithoutClientId_FailsClosed()
    {
        var config = Config("user-assigned-managed-identity", apiKey: null, clientId: null);

        Action action = () => MicrosoftFoundryProviderComposition.Register(
            config,
            new ApiProviderRegistry(),
            new ModelRegistry(),
            NullLoggerFactory.Instance);

        action.ShouldThrow<InvalidOperationException>();
    }

    private static PlatformConfig Config(string authenticationType, string? apiKey, string? clientId) => new()
    {
        Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["azure-foundry-test"] = new()
            {
                Type = "microsoft-foundry",
                Enabled = true,
                BaseUrl = "https://example.services.ai.azure.com/openai/v1",
                ApiKey = apiKey,
                Authentication = new ProviderAuthenticationConfig
                {
                    Type = authenticationType,
                    ClientId = clientId
                },
                Chat = new ProviderChatConfig
                {
                    Api = "microsoft-foundry-responses",
                    Models = ["example-deployment"],
                    DefaultModel = "example-deployment"
                }
            }
        }
    };
}
