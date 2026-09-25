using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Search;

public sealed class SearchControllerTests
{
    [Fact]
    public async Task Get_ReturnsRegisteredContributorGroupsAndAppliesSourceFilter()
    {
        var first = new RecordingContributor("first");
        var extension = new RecordingContributor("test-extension");
        var controller = CreateController([first, extension]);

        var response = await controller.Get(
            "needle",
            "test-extension",
            7,
            CancellationToken.None);

        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        var groups = (IReadOnlyList<AggregatedSearchGroup>)(ok.Value
            ?? throw new InvalidOperationException("Search response had no value."));
        groups.Single().SourceId.ShouldBe("test-extension");
        extension.ObservedRequest.ShouldBe(new SearchRequest("needle", 7));
        first.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, SearchController.MaximumLimit)]
    public async Task Get_ClampsLimitToPublicBounds(int requested, int expected)
    {
        var contributor = new RecordingContributor("source");
        var controller = CreateController([contributor]);

        var response = await controller.Get(
            "needle",
            null,
            requested,
            CancellationToken.None);

        response.Result.ShouldBeOfType<OkObjectResult>();
        contributor.ObservedRequest.ShouldBe(new SearchRequest("needle", expected));
    }

    [Fact]
    public async Task Get_WhitespaceQueryReturnsEmptyResponseWithoutCallingContributor()
    {
        var contributor = new RecordingContributor("source");
        var controller = CreateController([contributor]);

        var response = await controller.Get("  ", null, 10, CancellationToken.None);

        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeAssignableTo<IReadOnlyList<AggregatedSearchGroup>>().ShouldBeEmpty();
        contributor.CallCount.ShouldBe(0);
    }

    private static SearchController CreateController(IEnumerable<ISearchContributor> contributors)
        => new(new SearchAggregator(contributors, Options.Create(new SearchAggregationOptions())));

    private sealed class RecordingContributor(string sourceId) : ISearchContributor
    {
        private int _callCount;

        public string SourceId => sourceId;
        public string Label => sourceId;
        public bool IsAvailable => true;
        public bool CanAssessProvenanceTrust => true;
        public int CallCount => Volatile.Read(ref _callCount);
        public SearchRequest? ObservedRequest { get; private set; }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            SearchRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            ObservedRequest = request;
            return Task.FromResult<IReadOnlyList<SearchResult>>(
            [
                new("result", "snippet", $"/{sourceId}", DateTimeOffset.Parse("2026-09-25T00:00:00Z"), 0.5,
                    SearchProvenanceTrust.Trusted)
            ]);
        }
    }
}
