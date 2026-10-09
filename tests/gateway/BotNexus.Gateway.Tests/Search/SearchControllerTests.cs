using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Search;

public sealed class SearchControllerTests
{
    [Fact]
    public async Task Get_ReturnsRegisteredContributorGroupsAndAppliesSourceFilter()
    {
        var fixture = new Fixture();
        var response = await fixture.Controller.Get("needle", "memory", 7, CancellationToken.None);

        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        var groups = (IReadOnlyList<AggregatedSearchGroup>)(ok.Value
            ?? throw new InvalidOperationException("Search response had no value."));
        groups.Single().SourceId.ShouldBe("memory");
        fixture.ObservedRequest.ShouldBe(new AgentMemorySearchRequest("alpha", "needle", 7));
        fixture.FirstRegistry.DidNotReceive().GetAll();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, SearchController.MaximumLimit)]
    public async Task Get_ClampsLimitToPublicBounds(int requested, int expected)
    {
        var fixture = new Fixture();
        var response = await fixture.Controller.Get("needle", "memory", requested, CancellationToken.None);
        response.Result.ShouldBeOfType<OkObjectResult>();
        fixture.ObservedRequest.ShouldBe(new AgentMemorySearchRequest("alpha", "needle", expected));
    }

    [Fact]
    public async Task Get_WhitespaceQueryReturnsEmptyResponseWithoutCallingContributor()
    {
        var fixture = new Fixture();
        var response = await fixture.Controller.Get("  ", null, 10, CancellationToken.None);
        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeAssignableTo<IReadOnlyList<AggregatedSearchGroup>>().ShouldBeEmpty();
        fixture.CallCount.ShouldBe(0);
        fixture.FirstRegistry.DidNotReceive().GetAll();
    }

    // HTTP fixtures use reviewed concrete built-ins, not a production test-only approval escape hatch.
    private sealed class Fixture
    {
        public IAgentRegistry FirstRegistry { get; } = Substitute.For<IAgentRegistry>();
        public SearchController Controller { get; }
        public AgentMemorySearchRequest? ObservedRequest { get; private set; }
        public int CallCount { get; private set; }

        public Fixture()
        {
            var registry = Substitute.For<IAgentRegistry>();
            registry.GetAll().Returns([new AgentDescriptor
            {
                AgentId = AgentId.From("alpha"), DisplayName = "alpha", ModelId = "test", ApiProvider = "test",
                Memory = new MemoryAgentConfig { Enabled = true }
            }]);
            var memory = Substitute.For<IAgentMemory>();
            memory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                CallCount++;
                ObservedRequest = call.Arg<AgentMemorySearchRequest>();
                return Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([]);
            });
            var factory = Substitute.For<IAgentMemoryFactory>();
            factory.Create("alpha").Returns(memory);
            ISearchContributor[] contributors = [new AgentSearchContributor(FirstRegistry), new MemorySearchContributor(registry, factory)];
            var context = new DefaultHttpContext();
            context.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity { CallerId = "test", IsAdmin = true };
            Controller = new SearchController(new SearchAggregator(contributors, Options.Create(new SearchAggregationOptions())))
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
        }
    }
}
