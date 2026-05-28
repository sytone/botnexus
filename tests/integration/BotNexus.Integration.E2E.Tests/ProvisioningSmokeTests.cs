using System.Net.Http;
using System.Text.Json;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Smoke tests that validate the provisioning + gateway-startup half of the
/// new-user journey. These do not need a browser, so they run fast and act as
/// an early-warning canary if the more expensive Playwright flow degrades.
/// </summary>
[Collection(NewUserExperienceCollection.Name)]
public sealed class ProvisioningSmokeTests
{
    private readonly NewUserExperienceFixture _fx;

    public ProvisioningSmokeTests(NewUserExperienceFixture fx) => _fx = fx;

    [SkippableFact]
    public void FixtureCompletedProvisioningAndStartedGateway()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        File.Exists(Path.Combine(_fx.Home, "config.json")).ShouldBeTrue();
    }

    [SkippableFact]
    public void ConfigJsonContainsExpectedShape()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fx.Home, "config.json")));
        var root = doc.RootElement;

        root.TryGetProperty("providers", out var providers).ShouldBeTrue();
        providers.GetProperty("integration-mock").GetProperty("api").GetString()
            .ShouldBe("integration-mock");

        root.TryGetProperty("agents", out var agents).ShouldBeTrue();
        foreach (var id in _fx.AgentIds)
            agents.TryGetProperty(id, out _).ShouldBeTrue($"agent '{id}' missing from config.json");

        root.TryGetProperty("gateway", out var gateway).ShouldBeTrue();
        gateway.TryGetProperty("world", out var world).ShouldBeTrue();
        world.GetProperty("id").GetString().ShouldBe("e2e-world");

        gateway.TryGetProperty("locations", out var locations).ShouldBeTrue();
        foreach (var name in _fx.LocationNames)
            locations.TryGetProperty(name, out _).ShouldBeTrue($"location '{name}' missing from config.json");
    }

    [SkippableFact]
    public async Task GatewayHealthEndpointReturnsOk()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        using var http = new HttpClient();
        var resp = await http.GetAsync($"{_fx.GatewayBaseUrl}/health");
        resp.IsSuccessStatusCode.ShouldBeTrue($"GET /health returned {(int)resp.StatusCode}");
    }

    [SkippableFact]
    public async Task GatewayWorldEndpointReportsProvisionedWorld()
    {
        Skip.IfNot(_fx.Succeeded, $"Fixture initialization failed: {_fx.Error}");
        using var http = new HttpClient();
        var resp = await http.GetAsync($"{_fx.GatewayBaseUrl}/api/world");
        resp.IsSuccessStatusCode.ShouldBeTrue($"GET /api/world returned {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("e2e-world");
    }
}

internal static class ShouldlyShim
{
    // Tiny shim so the tests above can use Shouldly-style assertions without
    // pulling Shouldly into the centralized package list just for this project.
    // Keep this minimal — promote to real Shouldly once we're sure the project
    // is stable and we want richer diff output.
    public static void ShouldBeTrue(this bool actual, string? because = null)
    {
        if (!actual)
            Xunit.Assert.Fail(because ?? "Expected true but was false.");
    }
    public static void ShouldBe(this string? actual, string expected)
    {
        Xunit.Assert.Equal(expected, actual);
    }
    public static void ShouldContain(this string actual, string expected)
    {
        Xunit.Assert.Contains(expected, actual);
    }
}
