using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Api.Controllers;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3091. <c>GET /api/agents/{id}/sessions/{sid}/context</c> used to report a compile-time
/// <c>contextWindowTokens = 128000</c> for every agent on every model, and derived
/// <c>usagePercent</c> from it. These tests pin the two halves of the fix: the resolution
/// (<see cref="ContextWindowResolver"/>) and the reporting
/// (<see cref="AgentsController.BuildContextResponse"/>), including the unresolvable case, which
/// must be reported as absent rather than as a plausible-looking constant.
/// </summary>
public sealed class ContextWindowReportingTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static LlmModel Model(int contextWindow) => new(
        Id: "m",
        Name: "M",
        Api: "anthropic-messages",
        Provider: "anthropic",
        BaseUrl: "https://example.invalid",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: 1000);

    private static JsonDocument Response(int totalTokens, int? window) => JsonDocument.Parse(
        JsonSerializer.Serialize(
            AgentsController.BuildContextResponse(
                "a", "s", new ContextDiagnostics { TotalEstimatedTokens = totalTokens }, window),
            Wire));

    // ── Resolution ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UsesTheRegisteredModelsWindow_WhenNoOverrideIsSelected()
    {
        ContextWindowResolver.Resolve(effectiveOverride: null, Model(200_000)).ShouldBe(200_000);
        ContextWindowResolver.Resolve(effectiveOverride: null, Model(32_000)).ShouldBe(32_000);
    }

    [Fact]
    public void Resolve_PrefersTheOverride_BecauseItIsTheMoreSpecificLayer()
    {
        // The conversation > agent override stack already decided; the model default must not win.
        ContextWindowResolver.Resolve(effectiveOverride: 1_000_000, Model(200_000)).ShouldBe(1_000_000);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTheModelIsNotRegisteredAndNoOverrideExists()
    {
        // The unresolvable case: say so, never substitute a plausible constant (#3091).
        ContextWindowResolver.Resolve(effectiveOverride: null, model: null).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_TreatsANonPositiveDeclaredWindowAsUnresolvable(int declared)
    {
        // A zero window would otherwise produce a divide-by-zero usage percentage.
        ContextWindowResolver.Resolve(effectiveOverride: null, Model(declared)).ShouldBeNull();
        ContextWindowResolver.Resolve(effectiveOverride: declared, Model(declared)).ShouldBeNull();
    }

    // ── Reporting (#3091 AC2/AC3/AC4) ────────────────────────────────────────

    [Fact]
    public void Two_sessions_on_models_with_different_windows_report_different_values()
    {
        var wide = Response(10_000, ContextWindowResolver.Resolve(null, Model(200_000))).RootElement;
        var narrow = Response(10_000, ContextWindowResolver.Resolve(null, Model(32_000))).RootElement;

        wide.GetProperty("contextWindowTokens").GetInt32().ShouldBe(200_000);
        narrow.GetProperty("contextWindowTokens").GetInt32().ShouldBe(32_000);
    }

    [Fact]
    public void UsagePercent_is_computed_against_the_per_session_window_not_128000()
    {
        // 8,000 / 32,000 = 25.0%. Against the old 128,000 literal it would have been 6.3%.
        // Deliberately not a .x5 midpoint: this test pins the DENOMINATOR, not Math.Round's
        // banker's-rounding tie behaviour, which would be an unrelated contract to encode here.
        var narrow = Response(8_000, 32_000).RootElement;
        narrow.GetProperty("usagePercent").GetDouble().ShouldBe(25.0);

        // 8,000 / 200,000 = 4.0%, versus 6.3% under the old literal.
        var wide = Response(8_000, 200_000).RootElement;
        wide.GetProperty("usagePercent").GetDouble().ShouldBe(4.0);

        // The old constant is neither answer, so neither number can be produced by the defect.
        narrow.GetProperty("usagePercent").GetDouble().ShouldNotBe(6.3);
        wide.GetProperty("usagePercent").GetDouble().ShouldNotBe(6.3);
    }

    [Fact]
    public void An_unresolvable_window_is_reported_as_null_and_never_as_a_plausible_constant()
    {
        var root = Response(10_000, null).RootElement;

        root.GetProperty("contextWindowTokens").ValueKind.ShouldBe(JsonValueKind.Null);
        // usagePercent has no meaning without a denominator: absent, not a fabricated number.
        root.GetProperty("usagePercent").ValueKind.ShouldBe(JsonValueKind.Null);

        // The specific regression: no 128000 anywhere in the payload.
        root.GetRawText().ShouldNotContain("128000");
    }

    [Fact]
    public void The_response_builder_contains_no_context_window_literal()
    {
        // AC1: the value is supplied by the caller. If someone reintroduces a default inside the
        // builder, a caller passing null would silently start emitting that literal again - which is
        // exactly the #3091 defect. Passing null must be the ONLY thing that yields null.
        foreach (var window in new int?[] { null, 8_000, 200_000 })
        {
            var root = Response(1_000, window).RootElement;
            var reported = root.GetProperty("contextWindowTokens");
            if (window is null)
                reported.ValueKind.ShouldBe(JsonValueKind.Null);
            else
                reported.GetInt32().ShouldBe(window.Value);
        }
    }
}
