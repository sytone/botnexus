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
    /// #3336: all three Copilot transports declare the CRLF delta-framing quirk. The strip used to
    /// be gated on a <c>gpt-5.6</c> model-id prefix, which the <c>claude-opus-5</c> corruption
    /// evidence falsified - a transport artifact is a property of the wire, not of the model.
    /// </summary>
    [Fact]
    public void EveryCopilotTransport_DeclaresCrlfTextDeltaFraming()
    {
        CreateMessages().Capabilities.FramesStreamedTextDeltasWithCrlf.ShouldBeTrue();
        CreateCompletions().Capabilities.FramesStreamedTextDeltasWithCrlf.ShouldBeTrue();
        CreateResponses().Capabilities.FramesStreamedTextDeltasWithCrlf.ShouldBeTrue();
    }

    /// <summary>
    /// The complement, and the assertion that makes the flag mean something: a provider that has
    /// not declared the quirk does NOT get the lossy strip. Defaulting the workaround OFF is the
    /// #2432 contract - one provider's defect must not be paid for by every provider.
    /// </summary>
    [Fact]
    public void UndeclaredProvider_DoesNotGetTheCrlfStrip()
    {
        ProviderCapabilities.Default.FramesStreamedTextDeltasWithCrlf.ShouldBeFalse();
    }

    /// <summary>
    /// Every real provider outside the Copilot tree still declares the quirk OFF, so the migration
    /// from the model-id gate did not silently widen a lossy transform across the platform.
    /// </summary>
    [Fact]
    public void NonCopilotProviders_DoNotDeclareCrlfTextDeltaFraming()
    {
        new OpenAIResponsesProvider(new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance)
            .Capabilities.FramesStreamedTextDeltasWithCrlf.ShouldBeFalse();
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
