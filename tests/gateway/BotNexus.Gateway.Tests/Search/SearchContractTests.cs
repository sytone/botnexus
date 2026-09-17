using System.Text.Json;
using BotNexus.Gateway.Abstractions.Extensions;

namespace BotNexus.Gateway.Tests.Search;

public sealed class SearchContractTests
{
    [Fact]
    public void SearchResult_TrustSurvivesJsonRoundTripIndependentlyOfRelevance()
    {
        var original = new SearchResult(
            Title: "Architecture",
            Snippet: "A source-owned match.",
            Target: "/docs/architecture",
            Timestamp: new DateTimeOffset(2026, 9, 17, 3, 0, 0, TimeSpan.Zero),
            Relevance: 0.01,
            ProvenanceTrust: SearchProvenanceTrust.Trusted);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<SearchResult>(json);

        roundTripped.ShouldNotBeNull();
        roundTripped.ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Trusted);
        roundTripped.Relevance.ShouldBe(0.01);

        var highlyRelevantUntrusted = original with
        {
            Relevance = 1.0,
            ProvenanceTrust = SearchProvenanceTrust.Untrusted
        };
        highlyRelevantUntrusted.ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
        highlyRelevantUntrusted.Relevance.ShouldBe(1.0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"unknown\"")]
    [InlineData("\"future-attested\"")]
    public void SearchResult_MissingOrUnknownTrustDeserializesAsUntrusted(string? trustJson)
    {
        var trustProperty = trustJson is null ? string.Empty : $", \"provenanceTrust\": {trustJson}";
        var payload = $$"""
            {
              "title": "Result",
              "snippet": "Snippet",
              "target": "/target",
              "timestamp": "2026-09-17T03:00:00Z"
              {{trustProperty}}
            }
            """;

        var result = JsonSerializer.Deserialize<SearchResult>(payload, JsonOptions);

        result.ShouldNotBeNull();
        result.ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
    }

    [Fact]
    public void SearchResult_UndefinedProgrammaticTrustNormalizesToUntrusted()
    {
        var result = new SearchResult(
            "Result",
            "Snippet",
            "/target",
            DateTimeOffset.UtcNow,
            0.5,
            (SearchProvenanceTrust)1234);

        result.ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
    }

    [Fact]
    public void SearchContracts_DoNotExposeCrossSourceGlobalRank()
    {
        var forbiddenNames = new[] { "GlobalRank", "Rank", "GlobalRelevance" };

        typeof(SearchResult).GetProperties().Select(property => property.Name)
            .Intersect(forbiddenNames)
            .ShouldBeEmpty();
        typeof(SearchSourceGroup).GetProperties().Select(property => property.Name)
            .Intersect(forbiddenNames)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task ContributorContract_DescribesSourceAndReceivesBoundAndCancellation()
    {
        ISearchContributor contributor = new RecordingContributor();
        using var cancellation = new CancellationTokenSource();
        var request = new SearchRequest("contract", MaxResults: 7);

        var results = await contributor.SearchAsync(request, cancellation.Token);

        contributor.SourceId.ShouldBe("test-source");
        contributor.Label.ShouldBe("Test source");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeTrue();
        results.ShouldBeEmpty();
        ((RecordingContributor)contributor).ObservedRequest.ShouldBe(request);
        ((RecordingContributor)contributor).ObservedCancellation.ShouldBe(cancellation.Token);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class RecordingContributor : ISearchContributor
    {
        public string SourceId => "test-source";
        public string Label => "Test source";
        public bool IsAvailable => true;
        public bool CanAssessProvenanceTrust => true;
        public SearchRequest? ObservedRequest { get; private set; }
        public CancellationToken ObservedCancellation { get; private set; }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            SearchRequest request,
            CancellationToken cancellationToken = default)
        {
            ObservedRequest = request;
            ObservedCancellation = cancellationToken;
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        }
    }
}
