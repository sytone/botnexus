using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;
using Shouldly;

namespace BotNexus.Agent.Providers.Copilot.Tests.Discovery;

/// <summary>
/// #1762: verifies <see cref="CopilotModelDiscoveryProvider.ResolveApiFormat"/> honors the
/// endpoints Copilot advertises for a model (<c>supported_endpoints</c>) over the legacy
/// model-name heuristic, and falls back to the name heuristic only when the advertised list
/// is absent. Mirrors OpenCode's <c>github-copilot-models.test.ts</c>.
/// </summary>
public class CopilotModelEndpointMappingTests
{
    // --- Advertised endpoint wins over the name heuristic ---

    [Fact]
    public void AdvertisedMessages_wins_over_name_heuristic()
    {
        // Name heuristic would route "gpt-5-foo" to responses; advertised /v1/messages must win.
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id: "gpt-5-foo", family: "gpt-5", vendor: "OpenAI",
            supportedEndpoints: new[] { "/v1/messages" });

        api.ShouldBe("github-copilot-messages");
    }

    [Fact]
    public void AdvertisedResponses_wins_over_name_heuristic()
    {
        // Name heuristic would route a non-gpt5/o3/o4 model to completions; advertised /responses must win.
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id: "some-reasoning-model", family: "reasoner", vendor: "Acme",
            supportedEndpoints: new[] { "/responses" });

        api.ShouldBe("github-copilot-responses");
    }

    [Fact]
    public void AdvertisedChatCompletions_wins_over_name_heuristic()
    {
        // Name heuristic would route "gpt-5.x" to responses; advertised /chat/completions must win.
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id: "gpt-5.9-turbo", family: "gpt-5.9", vendor: "OpenAI",
            supportedEndpoints: new[] { "/chat/completions" });

        api.ShouldBe("github-copilot-completions");
    }

    [Fact]
    public void AdvertisedFullPath_chat_completions_matches_by_suffix()
    {
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id: "gpt-5.9-turbo", family: "gpt-5.9", vendor: "OpenAI",
            supportedEndpoints: new[] { "/v1/chat/completions" });

        api.ShouldBe("github-copilot-completions");
    }

    // --- Fallback to name heuristic when the advertised list is absent ---

    [Theory]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4.5", "github-copilot-messages")]
    [InlineData("gpt-5-mini", "gpt-5", "github-copilot-responses")]
    [InlineData("o3-mini", "o3", "github-copilot-responses")]
    [InlineData("o4", "o4", "github-copilot-responses")]
    [InlineData("gpt-4o", "gpt-4o", "github-copilot-completions")]
    [InlineData("gemini-2.5-pro", "gemini", "github-copilot-completions")]
    public void NullAdvertisedList_falls_back_to_name_heuristic(string id, string family, string expected)
    {
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id, family, vendor: string.Empty, supportedEndpoints: null);

        api.ShouldBe(expected);
    }

    [Fact]
    public void EmptyAdvertisedList_falls_back_to_name_heuristic()
    {
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id: "claude-opus-4.5", family: "claude", vendor: "Anthropic",
            supportedEndpoints: Array.Empty<string>());

        api.ShouldBe("github-copilot-messages");
    }

    [Fact]
    public void UnrecognisedAdvertisedList_falls_back_to_name_heuristic()
    {
        // An advertised list that contains no known endpoint falls through to the name heuristic.
        var api = CopilotModelDiscoveryProvider.ResolveApiFormat(
            id: "gpt-5-mini", family: "gpt-5", vendor: "OpenAI",
            supportedEndpoints: new[] { "/some/unknown/path" });

        api.ShouldBe("github-copilot-responses");
    }

    // --- End-to-end through MapToLlmModel ---

    [Fact]
    public void MapToLlmModel_honors_advertised_endpoint()
    {
        // A gpt-5 model that the name heuristic would send to responses, but Copilot advertises
        // only /chat/completions for, must map to the completions API.
        var info = new CopilotModelInfo
        {
            Id = "gpt-5-legacy",
            Name = "GPT-5 Legacy",
            Vendor = "OpenAI",
            Capabilities = new CopilotModelCapabilities { Family = "gpt-5" },
            SupportedEndpoints = new List<string> { "/chat/completions" }
        };

        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        model.ShouldNotBeNull();
        model!.Api.ShouldBe("github-copilot-completions");
    }

    [Fact]
    public void MapToLlmModel_falls_back_to_name_heuristic_when_absent()
    {
        var info = new CopilotModelInfo
        {
            Id = "gpt-5-mini",
            Name = "GPT-5 Mini",
            Vendor = "OpenAI",
            Capabilities = new CopilotModelCapabilities { Family = "gpt-5" },
            SupportedEndpoints = null
        };

        var model = CopilotModelDiscoveryProvider.MapToLlmModel(info);

        model.ShouldNotBeNull();
        model!.Api.ShouldBe("github-copilot-responses");
    }
}
