using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.A2A;
using BotNexus.Gateway.Abstractions.A2A;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.A2A.Tests;

public sealed class A2AToolContributorTests
{
    [Fact]
    public async Task ContributeAsync_RequiresGrantedRegisteredReadyProfile()
    {
        var profiles = new IA2AServiceProfile[]
        {
            new StubProfile("ready", true),
            new StubProfile("not-ready", false),
        };
        var contributor = new A2AToolContributor(profiles, static () => new A2AClient());

        (await contributor.ContributeAsync(Context())).Tools.ShouldBeEmpty();
        (await contributor.ContributeAsync(Context("missing"))).Tools.ShouldBeEmpty();
        (await contributor.ContributeAsync(Context("not-ready"))).Tools.ShouldBeEmpty();

        var contribution = await contributor.ContributeAsync(Context("ready", "missing"));
        var tool = contribution.Tools.ShouldHaveSingleItem();
        tool.Name.ShouldBe("delegate_a2a_task");
        tool.ContentSource.ShouldBe(ToolContentSource.Untrusted);
        contribution.ResourcesToDispose.ShouldHaveSingleItem().ShouldBeSameAs(tool);
    }

    [Fact]
    public void ServiceContributor_ComposesProfilesThroughOrdinaryDi()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IA2AServiceProfile>(new StubProfile("ready", true));
        new A2AServiceContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var contributor = provider.GetServices<IAgentToolContributor>().ShouldHaveSingleItem();
        contributor.ShouldBeOfType<A2AToolContributor>();
    }

    private static AgentToolContributionContext Context(params string[] profiles)
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("a2a-test"),
            DisplayName = "a2a-test",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = profiles.Length == 0
                ? new Dictionary<string, JsonElement>()
                : new Dictionary<string, JsonElement>
                {
                    [A2AExtensionConfig.ExtensionId] = JsonSerializer.SerializeToElement(new { profiles })
                }
        };

        return new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            string.Empty,
            new AllowAllPathValidator(),
            null,
            (_, _) => Task.FromResult<string?>(null));
    }

    private sealed class StubProfile(string id, bool ready) : IA2AServiceProfile
    {
        public string Id => id;
        public string DisplayName => id;
        public bool IsReady => ready;
        public ValueTask<A2AServiceConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new A2AServiceConnection(new Uri("https://agents.example.test/")));
    }

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }
}
