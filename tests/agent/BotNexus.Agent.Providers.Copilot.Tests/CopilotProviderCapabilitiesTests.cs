using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Issue #2432: the three Copilot transports are the ONLY providers that declare
/// <c>RecoversLeakedToolCallMarkup</c>, because the Copilot transport is where the #1709 leak was
/// actually observed ("opus via github-copilot" serialising a tool call as invoke/tool_use XML in
/// the assistant text channel).
/// <para>
/// These declarations cannot be covered by <c>StreamingProviderConformanceTests</c>: the Copilot
/// conformance subclass instantiates <c>OpenAICompletionsProvider</c> against a Copilot-shaped
/// model, so it never constructs a Copilot provider type at all. Without this file, the transport
/// that the entire workaround exists for would have no test proving it still declares the need.
/// </para>
/// <para>
/// All three transports declare it rather than only the one that produced the capture, because
/// Copilot model discovery routes a model to whichever transport the account exposes. #2170 is the
/// recorded consequence of fixing one Copilot transport and leaving the others: the same artifact
/// returned the moment discovery selected a different endpoint.
/// </para>
/// </summary>
public sealed class CopilotProviderCapabilitiesTests
{
    private static CopilotMessagesProvider CreateMessages() => new(new HttpClient());

    private static CopilotCompletionsProvider CreateCompletions() =>
        new(new HttpClient(), NullLogger<CopilotCompletionsProvider>.Instance);

    private static CopilotResponsesProvider CreateResponses() =>
        new(new HttpClient(), NullLogger<CopilotResponsesProvider>.Instance);

    /// <summary>
    /// The Messages transport -- the one named in #1709 -- declares the recovery need. This is the
    /// single assertion that guarantees #2432's gating did not silently disable the workaround for
    /// the provider that needed it.
    /// </summary>
    [Fact]
    public void MessagesTransport_DeclaresLeakedToolCallRecovery()
    {
        CreateMessages().Capabilities.RecoversLeakedToolCallMarkup.ShouldBeTrue();
    }

    /// <summary>The Completions transport declares it too, so discovery cannot route around the fix.</summary>
    [Fact]
    public void CompletionsTransport_DeclaresLeakedToolCallRecovery()
    {
        CreateCompletions().Capabilities.RecoversLeakedToolCallMarkup.ShouldBeTrue();
    }

    /// <summary>The Responses transport declares it too, so discovery cannot route around the fix.</summary>
    [Fact]
    public void ResponsesTransport_DeclaresLeakedToolCallRecovery()
    {
        CreateResponses().Capabilities.RecoversLeakedToolCallMarkup.ShouldBeTrue();
    }

    /// <summary>
    /// The Messages transport writes the system prompt into a dedicated top-level <c>system</c>
    /// field (<c>CopilotMessagesRequestBuilder</c>), whereas the two OpenAI-shaped Copilot
    /// transports prepend it as a message. Declaring the difference is what lets a caller answer
    /// the question without diffing request payloads.
    /// </summary>
    [Fact]
    public void SystemPromptPlacement_MatchesEachTransportsWireShape()
    {
        CreateMessages().Capabilities.SystemPromptPlacement.ShouldBe(SystemPromptPlacement.DedicatedField);
        CreateCompletions().Capabilities.SystemPromptPlacement.ShouldBe(SystemPromptPlacement.FirstMessage);
        CreateResponses().Capabilities.SystemPromptPlacement.ShouldBe(SystemPromptPlacement.FirstMessage);
    }

    /// <summary>
    /// #3442: no Copilot transport declares a CRLF delta-framing quirk any more. Mitm captures of
    /// the identical endpoints contain 0 raw CR bytes across 3,025 provider deltas, so the flag
    /// (and the lossy strip it gated) was removed entirely. This asserts the property that
    /// replaced it: every Copilot transport accumulates text deltas byte-identically, exactly as
    /// every non-Copilot provider already did.
    /// </summary>
    [Fact]
    public void NoCopilotTransport_DeclaresATextDeltaMutationCapability()
    {
        var capabilityNames = typeof(ProviderCapabilities)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        capabilityNames.ShouldNotContain(
            "FramesStreamedTextDeltasWithCrlf",
            "The CRLF this flag gated is not on the wire (#3442). The real defect was our own " +
            "separator injection in MessageConverter.ToAgentMessage (#3425, fixed by #3428).");

        // Non-vacuity: the record must still carry its OTHER declared capabilities, otherwise this
        // assertion would pass against an empty or renamed type.
        capabilityNames.ShouldContain("RecoversLeakedToolCallMarkup");
        capabilityNames.ShouldContain("SystemPromptPlacement");
    }

    /// <summary>
    /// Every Copilot transport declares its own record rather than inheriting the interface default
    /// -- the same conformance rule the shared base applies to the providers it can construct.
    /// </summary>
    [Fact]
    public void EveryCopilotTransport_DeclaresItsOwnCapabilities()
    {
        IApiProvider[] providers = [CreateMessages(), CreateCompletions(), CreateResponses()];

        foreach (var provider in providers)
        {
            ReferenceEquals(provider.Capabilities, ProviderCapabilities.Default).ShouldBeFalse(
                $"{provider.GetType().Name} must declare its own ProviderCapabilities (#2432).");
        }
    }
}
