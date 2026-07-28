using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2373: the cron model preflight must classify a model override accurately at create/update
/// time instead of letting a typo'd or decommissioned id fail silently on every fire.
/// </summary>
public sealed class CronModelPreflightTests
{
    [Fact]
    public void Resolve_NullOrWhitespace_IsNotSpecified()
    {
        var registry = BuildRegistry();

        CronModelPreflight.Resolve(registry, null).Kind.ShouldBe(CronModelPreflightKind.NotSpecified);
        CronModelPreflight.Resolve(registry, "   ").Kind.ShouldBe(CronModelPreflightKind.NotSpecified);
    }

    [Fact]
    public void Resolve_NullRegistry_IsRegistryUnavailable()
    {
        var result = CronModelPreflight.Resolve(null, "openai/gpt-4.1");

        result.Kind.ShouldBe(CronModelPreflightKind.RegistryUnavailable);
        result.IsRejection.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_EmptyRegistry_IsRegistryUnavailable()
    {
        var result = CronModelPreflight.Resolve(new ModelRegistry(), "openai/gpt-4.1");

        result.Kind.ShouldBe(CronModelPreflightKind.RegistryUnavailable);
        result.IsRejection.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_QualifiedId_ResolvesModel()
    {
        var result = CronModelPreflight.Resolve(BuildRegistry(), "openai/gpt-4.1");

        result.Kind.ShouldBe(CronModelPreflightKind.Resolved);
        result.IsRejection.ShouldBeFalse();
        result.Provider.ShouldBe("openai");
        result.ModelId.ShouldBe("gpt-4.1");
    }

    [Fact]
    public void Resolve_BareId_ResolvesAcrossProviders()
    {
        var result = CronModelPreflight.Resolve(BuildRegistry(), "claude-opus-5");

        result.Kind.ShouldBe(CronModelPreflightKind.Resolved);
        result.Provider.ShouldBe("github-copilot");
        result.ModelId.ShouldBe("claude-opus-5");
    }

    [Fact]
    public void Resolve_QualifiedId_UnknownModelForKnownProvider_IsUnknownModel()
    {
        var result = CronModelPreflight.Resolve(BuildRegistry(), "openai/gpt-4.1-typo");

        result.Kind.ShouldBe(CronModelPreflightKind.UnknownModel);
        result.IsRejection.ShouldBeTrue();
        result.Reason.ShouldNotBeNull();
        result.Reason!.ShouldContain("openai/gpt-4.1-typo");
        // The rejection must actually enumerate what IS available - that is the whole point.
        result.Reason.ShouldContain("gpt-4.1-mini");
    }

    [Fact]
    public void Resolve_UnknownProvider_IsUnknownProvider()
    {
        var result = CronModelPreflight.Resolve(BuildRegistry(), "acme/whatever");

        result.Kind.ShouldBe(CronModelPreflightKind.UnknownProvider);
        result.IsRejection.ShouldBeTrue();
        result.Reason.ShouldNotBeNull();
        result.Reason!.ShouldContain("acme");
        result.Reason.ShouldContain("openai");
    }

    [Fact]
    public void Resolve_BareUnknownId_IsUnknownModel()
    {
        var result = CronModelPreflight.Resolve(BuildRegistry(), "gpt-9-imaginary");

        result.Kind.ShouldBe(CronModelPreflightKind.UnknownModel);
        result.IsRejection.ShouldBeTrue();
        result.Reason!.ShouldContain("gpt-9-imaginary");
        result.Reason!.ShouldContain("openai/gpt-4.1");
    }

    [Fact]
    public void Resolve_ProviderAlias_IsAccepted()
    {
        // ModelRegistry aliases "copilot" -> "github-copilot"; the preflight must honour it
        // rather than rejecting a perfectly valid override.
        var result = CronModelPreflight.Resolve(BuildRegistry(), "copilot/claude-opus-5");

        result.Kind.ShouldBe(CronModelPreflightKind.Resolved);
        result.ModelId.ShouldBe("claude-opus-5");
    }

    [Fact]
    public void Resolve_Reason_IsBounded_WhenManyModelsAreRegistered()
    {
        var registry = new ModelRegistry();
        for (var i = 0; i < 400; i++)
            registry.Register("openai", Model("openai", $"model-with-a-fairly-long-identifier-{i:D3}"));

        var result = CronModelPreflight.Resolve(registry, "openai/nope");

        result.Kind.ShouldBe(CronModelPreflightKind.UnknownModel);
        result.Reason!.Length.ShouldBeLessThanOrEqualTo(CronModelPreflight.MaxReasonLength);
        result.Reason.ShouldContain("more)");
    }

    [Fact]
    public void ClassifyRejection_ReturnsNullForResolvableOverride()
    {
        CronModelPreflight.ClassifyRejection(BuildRegistry(), "openai/gpt-4.1").ShouldBeNull();
        CronModelPreflight.ClassifyRejection(BuildRegistry(), null).ShouldBeNull();
        CronModelPreflight.ClassifyRejection(null, "openai/gpt-4.1").ShouldBeNull();
    }

    [Fact]
    public void ClassifyRejection_ReturnsReasonForUnknownModel()
    {
        var reason = CronModelPreflight.ClassifyRejection(BuildRegistry(), "openai/nope");

        reason.ShouldNotBeNull();
        reason!.ShouldContain("openai/nope");
    }

    [Fact]
    public void Summarize_BoundsAndRedactsDiagnosticText()
    {
        var raw = "connect failed api_key=sk-super-secret-value-1234567890 " + new string('x', 4000);

        var summary = CronModelPreflight.Summarize(raw);

        summary.ShouldNotBeNull();
        summary!.Length.ShouldBeLessThanOrEqualTo(CronModelPreflight.MaxReasonLength);
        summary.ShouldNotContain("sk-super-secret-value-1234567890");
        summary.ShouldContain("connect failed");
    }

    [Fact]
    public void Summarize_NullIsNull()
    {
        CronModelPreflight.Summarize(null).ShouldBeNull();
    }

    internal static ModelRegistry BuildRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", Model("openai", "gpt-4.1"));
        registry.Register("openai", Model("openai", "gpt-4.1-mini"));
        registry.Register("github-copilot", Model("github-copilot", "claude-opus-5"));
        return registry;
    }

    private static LlmModel Model(string provider, string id) => new(
        Id: id,
        Name: id,
        Api: "openai-completions",
        Provider: provider,
        BaseUrl: "https://example.invalid",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200_000,
        MaxTokens: 8_000);
}
