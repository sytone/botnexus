using System.Reflection;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron.Tests;

public sealed class CronDefaultModelTests
{
    [Fact]
    public void DefaultModel_IsOptIn()
    {
        var property = typeof(CronOptions).GetProperty("DefaultModel");
        property.ShouldNotBeNull();
        property.GetValue(new CronOptions()).ShouldBeNull();
    }

    [Theory]
    [InlineData("p/job", "p/global", "p/standing", "job", "job")]
    [InlineData(null, "p/global", "p/standing", "global", "global")]
    [InlineData(null, null, "p/standing", "standing", "conversation")]
    [InlineData(" ", " ", null, "fallback", "agent")]
    public void Resolve_Precedence_IsOccurrenceLocal(string? job, string? global, string? standing, string expected, string source)
    {
        var result = Resolve("agent-prompt", job, global, standing);
        Read(result, "ModelId").ShouldBe(expected);
        Read(result, "Provider").ShouldBe("p");
        Read(result, "Source").ShouldBe(source);
    }

    [Theory]
    [InlineData("agent-prompt")]
    [InlineData("heartbeat")]
    [InlineData("skill-review")]
    [InlineData("memory-dreaming")]
    [InlineData("agent-converse")]
    public void Resolve_AllModelActions_UseGlobal(string action)
        => Read(Resolve(action, null, "p/global", null), "ModelId").ShouldBe("global");

    [Theory]
    [InlineData("command")]
    [InlineData("webhook")]
    public void Resolve_DeterministicActions_DoNotResolveInvalidDefault(string action)
        => Resolve(action, null, "p/not-registered", null).ShouldBeNull();

    [Fact]
    public void Resolve_InvalidDefault_FailsClosed()
        => Should.Throw<InvalidOperationException>(() => Resolve("agent-prompt", null, "p/invalid", null));

    [Fact]
    public void Resolve_MissingRegistry_FailsClosed()
        => Should.Throw<InvalidOperationException>(() => Resolve("agent-prompt", null, "p/global", null, missingRegistry: true));

    [Fact]
    public void Resolve_ForbiddenDefault_FailsClosed()
        => Should.Throw<InvalidOperationException>(() => Resolve("agent-prompt", null, "p/global", null, allowed: ["fallback"]));

    private static object? Resolve(string action, string? job, string? global, string? standing, bool missingRegistry = false, string[]? allowed = null)
    {
        var type = typeof(CronOptions).Assembly.GetType("BotNexus.Cron.CronModelRouting");
        type.ShouldNotBeNull("dispatch-time routing must have a single authority");
        var method = type.GetMethod("Resolve");
        method.ShouldNotBeNull();
        var registry = new ModelRegistry();
        foreach (var model in new[] { "job", "global", "standing", "fallback" })
            registry.Register("p", new LlmModel(Id: model, Name: model, Provider: "p", Api: "openai-completions", BaseUrl: "https://example.invalid", Reasoning: false, Input: ["text"], Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 1000, MaxTokens: 100));
        var descriptor = new AgentDescriptor { AgentId = AgentId.From("test"), DisplayName = "Test", ApiProvider = "p", ModelId = "fallback", AllowedModelIds = allowed ?? [] };
        try { return method.Invoke(null, [action, job, global, standing, descriptor, missingRegistry ? null : registry]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static string? Read(object? result, string property)
    {
        result.ShouldNotBeNull();
        return result.GetType().GetProperty(property)?.GetValue(result)?.ToString();
    }
}
