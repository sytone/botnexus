using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Api.Controllers;

namespace BotNexus.Gateway.Tests;

public sealed class ContextBudgetDiagnosticsTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null, null, 1_050_000, "model")]
    [InlineData(64_000, "agent", 64_000, "agent")]
    [InlineData(32_000, "conversation", 32_000, "conversation")]
    [InlineData(0, "conversation", 1_050_000, "model")]
    [InlineData(-1, "agent", 1_050_000, "model")]
    public void ResolveBudget_SeparatesWorkingBudgetFromModelDeclarations(
        int? selectedOverride, string? overrideSource, int expectedBudget, string expectedSource)
    {
        var budget = ContextWindowResolver.ResolveBudget(selectedOverride, overrideSource, Declarations(Model()));

        budget.EffectiveWorkingBudgetTokens.ShouldBe(expectedBudget);
        budget.EffectiveWorkingBudgetSource.ShouldBe(expectedSource);
        budget.ModelContextWindowTokens.ShouldBe(1_050_000);
        budget.ModelMaxOutputTokens.ShouldBe(128_000);
        budget.ModelContextWindowSource.ShouldBe("configured-model");
        budget.ModelMaxOutputSource.ShouldBe("configured-model");
        ContextWindowResolver.Resolve(selectedOverride, Model()).ShouldBe(expectedBudget);
    }

    [Fact]
    public void ResolveBudget_ExtendedOverrideIsNotClampedToStandardModelWindow()
    {
        var model = Model() with { ContextWindow = 200_000, SupportsExtendedContextWindow = true };
        var budget = ContextWindowResolver.ResolveBudget(1_000_000, "conversation", Declarations(model));

        budget.EffectiveWorkingBudgetTokens.ShouldBe(1_000_000);
        budget.EffectiveWorkingBudgetSource.ShouldBe("conversation");
        budget.ModelContextWindowTokens.ShouldBe(200_000);
        budget.ModelMaxOutputTokens.ShouldBe(128_000);
    }

    [Fact]
    public void ResolveBudget_UnknownModelKeepsKnownOverrideWithoutInventingCapacities()
    {
        var budget = ContextWindowResolver.ResolveBudget(32_000, "agent", null);
        budget.EffectiveWorkingBudgetTokens.ShouldBe(32_000);
        budget.EffectiveWorkingBudgetSource.ShouldBe("agent");
        budget.ModelContextWindowTokens.ShouldBeNull();
        budget.ModelMaxOutputTokens.ShouldBeNull();
        budget.ModelContextWindowSource.ShouldBeNull();
        budget.ModelMaxOutputSource.ShouldBeNull();
    }

    [Fact]
    public void ResolveBudget_UnresolvableAndNonPositiveDeclarationsRemainUnknown()
    {
        foreach (var model in new LlmModel?[] { null, Model() with { ContextWindow = 0, MaxTokens = -1 } })
        {
            var budget = ContextWindowResolver.ResolveBudget(null, null, Declarations(model));
            budget.EffectiveWorkingBudgetTokens.ShouldBeNull();
            budget.EffectiveWorkingBudgetSource.ShouldBeNull();
            budget.ModelContextWindowTokens.ShouldBeNull();
            budget.ModelMaxOutputTokens.ShouldBeNull();
            budget.ModelContextWindowSource.ShouldBeNull();
            budget.ModelMaxOutputSource.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("registered")]
    [InlineData("fallback")]
    [InlineData("configured-provider")]
    [InlineData("configured-model")]
    public void BuildContextResponse_SerializesOriginsWithoutClaimingProviderVerification(string declarationSource)
    {
        var model = Model() with { ContextWindowSource = declarationSource, MaxTokensSource = declarationSource };
        var budget = ContextWindowResolver.ResolveBudget(64_000, "agent", Declarations(model));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentsController.BuildContextResponse("a", "s", new ContextDiagnostics { TotalEstimatedTokens = 16_000 },
                contextWindowTokens: 64_000, contextBudget: budget), Wire));
        var root = json.RootElement;
        root.GetProperty("contextWindowTokens").GetInt32().ShouldBe(64_000);
        root.GetProperty("usagePercent").GetDouble().ShouldBe(25);
        var reported = root.GetProperty("contextBudget");
        reported.GetProperty("effectiveWorkingBudgetTokens").GetInt32().ShouldBe(64_000);
        reported.GetProperty("effectiveWorkingBudgetSource").GetString().ShouldBe("agent");
        reported.GetProperty("modelContextWindowTokens").GetInt32().ShouldBe(1_050_000);
        reported.GetProperty("modelMaxOutputTokens").GetInt32().ShouldBe(128_000);
        reported.GetProperty("modelContextWindowSource").GetString().ShouldBe(declarationSource);
        reported.GetProperty("modelMaxOutputSource").GetString().ShouldBe(declarationSource);
        root.GetRawText().ShouldNotContain("verified");
    }

    [Fact]
    public void BuildContextResponse_UsesBudgetAsDenominatorRatherThanContradictoryLegacyWindow()
    {
        var budget = ContextWindowResolver.ResolveBudget(32_000, "conversation", Declarations(Model()));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentsController.BuildContextResponse("a", "s", new ContextDiagnostics { TotalEstimatedTokens = 8_000 },
                contextWindowTokens: 1_050_000, contextBudget: budget), Wire));
        json.RootElement.GetProperty("contextWindowTokens").GetInt32().ShouldBe(32_000);
        json.RootElement.GetProperty("usagePercent").GetDouble().ShouldBe(25);
    }

    [Fact]
    public void BuildContextResponse_UnknownBudgetReportsNullWithoutInventedDenominator()
    {
        var budget = ContextWindowResolver.ResolveBudget(null, null, null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentsController.BuildContextResponse("a", "s", new ContextDiagnostics { TotalEstimatedTokens = 8_000 },
                contextBudget: budget), Wire));
        json.RootElement.GetProperty("contextWindowTokens").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("usagePercent").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("contextBudget").GetProperty("effectiveWorkingBudgetSource")
            .ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetRawText().ShouldNotContain("128000");
    }

    private static ContextBudgetDiagnostics Declarations(LlmModel? model) => new()
    {
        ModelContextWindowTokens = model?.ContextWindow,
        ModelContextWindowSource = model?.ContextWindowSource,
        ModelMaxOutputTokens = model?.MaxTokens,
        ModelMaxOutputSource = model?.MaxTokensSource
    };

    private static LlmModel Model() => new("large", "Large", "test-api", "dynamic", "", false, ["text"],
        new ModelCost(0, 0, 0, 0), 1_050_000, 128_000,
        ContextWindowSource: "configured-model", MaxTokensSource: "configured-model");
}
