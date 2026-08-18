using System.Text.Json;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Gateway.Tests.Models;

/// <summary>
/// Issue #3364: <c>CopilotModelDiscoveryProvider.MapToLlmModel</c> never set
/// <c>SupportsExtendedContextWindow</c>, so it took the record default of false for EVERY
/// Copilot-discovered model. <c>ModelRegistry.GetSupportedContextSizes</c> then returned a
/// single-element list, which is what made the portal's context-window picker unselectable.
/// <para>
/// Every assertion here goes through <see cref="ModelRegistry.GetSupportedContextSizes"/> - the
/// OBSERVABLE the portal actually binds to - rather than only the boolean flag. A test asserting
/// the flag alone would pass against a flag wired to nothing (acceptance criterion 7).
/// </para>
/// </summary>
public sealed class CopilotExtendedContextWindowTests
{
    private const int StandardContextWindow = 200_000;
    private const int ExtendedContextWindow = 1_000_000;

    private static CopilotModelInfo Info(
        string id,
        bool? longContext = null,
        int? maxPromptTokens = null)
    {
        var supports = new CopilotModelSupports { LongContext = longContext };

        Dictionary<string, JsonElement>? limits = null;
        if (maxPromptTokens is { } prompt)
        {
            limits = new Dictionary<string, JsonElement>
            {
                ["max_prompt_tokens"] = JsonSerializer.SerializeToElement(prompt)
            };
        }

        return new CopilotModelInfo
        {
            Id = id,
            Vendor = "Anthropic",
            Capabilities = new CopilotModelCapabilities
            {
                Family = "claude",
                Supports = supports,
                Limits = limits
            }
        };
    }

    // --- Happy path: a discovered model advertising long context offers two selectable tiers. ---

    [Fact]
    public void DeclaredLongContext_YieldsTwoSelectableTiers()
    {
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(Info("claude-opus-4.8", longContext: true));

        model.ShouldNotBeNull();
        model.SupportsExtendedContextWindow.ShouldBeTrue();
        ModelRegistry.SupportsExtendedContext(model).ShouldBeTrue();
        ModelRegistry.GetSupportedContextSizes(model)
            .ShouldBe([StandardContextWindow, ExtendedContextWindow]);
    }

    /// <summary>
    /// The exact models named in the bug report. Copilot's payload is silent about long context for
    /// these, so the family heuristic must carry them - the reporter switched between Opus 4.8 and
    /// Opus 5 and found the picker unselectable in BOTH directions.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-opus-4.5")]
    [InlineData("claude-sonnet-4.6")]
    public void SilentPayload_ExtendedContextFamily_YieldsTwoSelectableTiers(string modelId)
    {
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(Info(modelId));

        model.ShouldNotBeNull();
        ModelRegistry.GetSupportedContextSizes(model)
            .ShouldBe([StandardContextWindow, ExtendedContextWindow]);
    }

    /// <summary>
    /// A prompt budget beyond the standard 200K ceiling is itself proof of an extended window, even
    /// for a family the heuristic does not recognise.
    /// </summary>
    [Fact]
    public void PromptBudgetAboveStandardCeiling_YieldsTwoSelectableTiers()
    {
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(
            Info("some-future-vendor-model", maxPromptTokens: 400_000));

        model.ShouldNotBeNull();
        ModelRegistry.GetSupportedContextSizes(model)
            .ShouldBe([StandardContextWindow, ExtendedContextWindow]);
    }

    // --- Sad path: a model with no long-context capability must still expose exactly one tier. ---

    /// <summary>
    /// Acceptance criterion 6: no declared capability and an unrecognised family means one tier -
    /// the model's own discovered window, not a guessed 1M.
    /// </summary>
    [Fact]
    public void NoLongContextCapability_YieldsExactlyOneTier()
    {
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(Info("gpt-4o", maxPromptTokens: 128_000));

        model.ShouldNotBeNull();
        model.SupportsExtendedContextWindow.ShouldBeFalse();
        ModelRegistry.GetSupportedContextSizes(model).ShouldBe([128_000]);
    }

    /// <summary>
    /// Acceptance criterion 4, the load-bearing case: an EXPLICIT <c>long_context: false</c> pins the
    /// model to one tier even for a Claude generation the family heuristic would otherwise widen. A
    /// fix that only ever widened would offer a 1M tier the provider has said it will reject.
    /// </summary>
    [Fact]
    public void ExplicitLongContextFalse_OverridesFamilyHeuristic_AndYieldsOneTier()
    {
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(
            Info("claude-opus-5", longContext: false, maxPromptTokens: 200_000));

        model.ShouldNotBeNull();
        model.SupportsExtendedContextWindow.ShouldBeFalse();
        ModelRegistry.GetSupportedContextSizes(model).ShouldBe([200_000]);
    }

    /// <summary>
    /// Regression guard for models that pre-date the extended window: Claude 3.x and Haiku must not
    /// be widened, and a discovered model with NO limits block at all keeps the discovery default.
    /// </summary>
    [Theory]
    [InlineData("claude-3-5-sonnet")]
    [InlineData("claude-haiku-4.5")]
    [InlineData("claude-opus-4")]
    public void PreExtendedGenerations_YieldExactlyOneTier(string modelId)
    {
        var model = CopilotModelDiscoveryProvider.MapToLlmModel(Info(modelId));

        model.ShouldNotBeNull();
        ModelRegistry.GetSupportedContextSizes(model).Count.ShouldBe(1);
        ModelRegistry.GetSupportedContextSizes(model).ShouldBe([model.ContextWindow]);
    }

    /// <summary>
    /// The dotted (Copilot) and dated (Anthropic-direct) spellings of the same generation must agree.
    /// The pre-fix literal-prefix heuristic recognised only the dated spelling, which is precisely why
    /// the same Claude model behaved differently depending on which transport served it.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4.6")]
    [InlineData("claude-opus-4-5-20250929", "claude-opus-4.5")]
    public void DatedAndDottedSpellings_Agree(string datedId, string dottedId)
    {
        DynamicModelCapabilities.InferExtendedContext(datedId)
            .ShouldBe(DynamicModelCapabilities.InferExtendedContext(dottedId));
        DynamicModelCapabilities.InferExtendedContext(datedId).ShouldBeTrue();
    }
}
