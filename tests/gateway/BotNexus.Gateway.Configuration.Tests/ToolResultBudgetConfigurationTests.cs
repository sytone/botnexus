using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ToolResultBudgetConfigurationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000001")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    public void Schema_RejectsInvalidExecutedResultBudget(string value)
        => PlatformConfigSchema.ValidateJson("{\"gateway\":{\"maxToolResults\":" + value + "}}").ShouldNotBeEmpty();

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1000000)]
    public void Schema_AcceptsFiniteBudgetAndRoundTripsSetting(int value)
    {
        var json = "{\"gateway\":{\"maxToolResults\":" + value + "}}";
        PlatformConfigSchema.ValidateJson(json).ShouldBeEmpty();
        var config = JsonSerializer.Deserialize<PlatformConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).ShouldNotBeNull();
        config.Gateway.ShouldNotBeNull().MaxToolResults.ShouldBe(value);
    }
}
