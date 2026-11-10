using System.Reflection;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Tests;

public sealed class ProviderModelBoundaryTests
{
    private static Type ThinkingType => Nullable.GetUnderlyingType(
        typeof(EffectiveExecutionSettings).GetProperty("Thinking")?.PropertyType
        ?? throw new InvalidOperationException("Thinking property missing"))
        ?? throw new InvalidOperationException("Thinking must remain nullable");

    private static Type MappingType => typeof(ProviderModelBoundaryTests).Assembly.GetType(
        "BotNexus.Gateway.Isolation.ProviderModelMapping")
        ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(
            "BotNexus.Gateway.Isolation.ProviderModelMapping")).FirstOrDefault(t => t is not null)
        ?? Assembly.Load("BotNexus.Gateway").GetType("BotNexus.Gateway.Isolation.ProviderModelMapping")
        ?? throw new InvalidOperationException("Isolation provider-model mapping missing");

    private static object? Map(string name, object? value) => (MappingType.GetMethod(name,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Mapping {name} missing")).Invoke(null, [value]);

    [Fact]
    public void Contract_ThinkingIsGatewayOwnedAndResolverAcceptsDeclarations()
    {
        ThinkingType.Namespace.ShouldBe("BotNexus.Gateway.Abstractions.Agents");
        var resolve = typeof(ContextWindowResolver).GetMethod("Resolve");
        resolve.ShouldNotBeNull();
        resolve.GetParameters()[1].ParameterType.ShouldBe(typeof(ContextBudgetDiagnostics));
    }

    [Theory]
    [InlineData("Minimal", "minimal")]
    [InlineData("Low", "low")]
    [InlineData("Medium", "medium")]
    [InlineData("High", "high")]
    [InlineData("ExtraHigh", "xhigh")]
    [InlineData("Max", "max")]
    public void ThinkingMapping_AllDeclaredLevelsPreserveValueAndWireToken(string name, string token)
    {
        var core = Enum.Parse<ThinkingLevel>(name);
        var projected = Map("ToGatewayThinking", core);
        projected.ShouldNotBeNull();
        projected.GetType().ShouldBe(ThinkingType);
        projected.ToString().ShouldBe(name);
        Map("ToProviderThinking", projected).ShouldBe(core);
        Enum.GetNames(ThinkingType).ShouldBe(Enum.GetNames<ThinkingLevel>());
        var settings = Activator.CreateInstance(typeof(EffectiveExecutionSettings),
            [null, null, null, projected, null]);
        settings.ShouldNotBeNull();
        ((EffectiveExecutionSettings)settings).ThinkingWireToken.ShouldBe(token);
        JsonSerializer.Serialize(projected, ThinkingType).ShouldBe($"\"{token}\"");
    }

    [Fact]
    public void ThinkingMapping_NullAndInvalidUnderlyingValuesAreNotReinterpreted()
    {
        Map("ToGatewayThinking", null).ShouldBeNull();
        Map("ToProviderThinking", null).ShouldBeNull();
        new EffectiveExecutionSettings(null, null, null, null, null).ThinkingWireToken.ShouldBeNull();
        foreach (var raw in new[] { -1, 6, int.MinValue, int.MaxValue })
        {
            var projected = Map("ToGatewayThinking", (ThinkingLevel)raw);
            projected.ShouldNotBeNull();
            Convert.ToInt32(projected).ShouldBe(raw);
            Map("ToProviderThinking", projected).ShouldBe((ThinkingLevel)raw);
            var settings = Activator.CreateInstance(typeof(EffectiveExecutionSettings),
                [null, null, null, projected, null]);
            settings.ShouldNotBeNull();
            ((EffectiveExecutionSettings)settings).ThinkingWireToken.ShouldBeNull();
            JsonSerializer.Serialize(projected, ThinkingType).ShouldBe(
                JsonSerializer.Serialize((ThinkingLevel)raw));
        }
    }

    [Theory]
    [InlineData("registered", "fallback")]
    [InlineData("configured-provider", "configured-model")]
    public void ModelMapping_PreservesIndependentDeclarationsAndSources(string contextSource, string outputSource)
    {
        var model = new LlmModel("m", "M", "api", "provider", "", false, ["text"],
            new ModelCost(0, 0, 0, 0), 200_000, 32_000,
            ContextWindowSource: contextSource, MaxTokensSource: outputSource);
        var declarations = Map("ToContextDeclarations", model).ShouldBeOfType<ContextBudgetDiagnostics>();
        declarations.ModelContextWindowTokens.ShouldBe(200_000);
        declarations.ModelMaxOutputTokens.ShouldBe(32_000);
        declarations.ModelContextWindowSource.ShouldBe(contextSource);
        declarations.ModelMaxOutputSource.ShouldBe(outputSource);
        declarations.EffectiveWorkingBudgetTokens.ShouldBeNull();
        declarations.EffectiveWorkingBudgetSource.ShouldBeNull();
        var budget = ContextWindowResolver.ResolveBudget(1_000_000, "conversation", declarations);
        budget.EffectiveWorkingBudgetTokens.ShouldBe(1_000_000);
        budget.EffectiveWorkingBudgetSource.ShouldBe("conversation");
        budget.ModelContextWindowTokens.ShouldBe(200_000);
        budget.ModelMaxOutputTokens.ShouldBe(32_000);
        budget.ModelContextWindowSource.ShouldBe(contextSource);
        budget.ModelMaxOutputSource.ShouldBe(outputSource);
    }

    [Fact]
    public void ModelMapping_UnknownAndNonPositiveDeclarationsKeepExistingResolutionRules()
    {
        Map("ToContextDeclarations", null).ShouldBeNull();
        var resolve = typeof(ContextWindowResolver).GetMethod("Resolve")
            ?? throw new InvalidOperationException("Resolve missing");
        foreach (var value in new[] { -1, 0, 32_000 })
        {
            var model = new LlmModel("m", "M", "api", "provider", "", false, ["text"],
                new ModelCost(0, 0, 0, 0), value, value);
            var declarations = Map("ToContextDeclarations", model).ShouldBeOfType<ContextBudgetDiagnostics>();
            declarations.ModelContextWindowTokens.ShouldBe(value);
            declarations.ModelMaxOutputTokens.ShouldBe(value);
            foreach (var selected in new int?[] { null, -1, 0, 1_000_000 })
            {
                var expected = selected is > 0 ? selected : value > 0 ? value : (int?)null;
                resolve.Invoke(null, [selected, declarations]).ShouldBe(expected);
                var budget = ContextWindowResolver.ResolveBudget(selected, "agent", declarations);
                budget.EffectiveWorkingBudgetTokens.ShouldBe(expected);
                budget.ModelContextWindowTokens.ShouldBe(value > 0 ? value : null);
                budget.ModelMaxOutputTokens.ShouldBe(value > 0 ? value : null);
                budget.ModelContextWindowSource.ShouldBe(value > 0 ? "registered" : null);
                budget.ModelMaxOutputSource.ShouldBe(value > 0 ? "registered" : null);
            }
        }
    }
}
