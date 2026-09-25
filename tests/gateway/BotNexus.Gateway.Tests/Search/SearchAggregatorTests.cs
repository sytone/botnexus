using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Search;

public sealed class SearchAggregatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void AggregatedSearchGroup_DoesNotExposeCrossSourceGlobalRank()
    {
        var forbiddenNames = new[] { "GlobalRank", "Rank", "GlobalRelevance" };

        typeof(AggregatedSearchGroup).GetProperties().Select(property => property.Name)
            .Intersect(forbiddenNames)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_FansOutConcurrentlyAndPreservesSourceLocalResults()
    {
        var allEntered = NewGate();
        var release = NewGate();
        var entered = 0;
        var first = new DelegateContributor("first", "First", async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                allEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return [Result("first-a", "/first/a", SearchProvenanceTrust.Trusted), Result("first-b", "/first/b")];
        });
        var second = new DelegateContributor("second", "Second", async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                allEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return [Result("second-a", "/second/a")];
        });
        var aggregator = CreateAggregator([first, second]);

        var search = aggregator.SearchAsync("needle", 10, cancellationToken: CancellationToken.None);
        await allEntered.Task.WaitAsync(TestTimeout, CancellationToken.None);
        release.TrySetResult();
        var groups = await search.WaitAsync(TestTimeout, CancellationToken.None);

        groups.Select(group => group.SourceId).ShouldBe(["first", "second"]);
        groups[0].Label.ShouldBe("First");
        groups[0].CanAssessProvenanceTrust.ShouldBeTrue();
        groups[0].IsAvailable.ShouldBeTrue();
        groups[0].Count.ShouldBe(2);
        groups[0].Error.ShouldBeNull();
        groups[0].Results.Select(result => result.Title).ShouldBe(["first-a", "first-b"]);
        groups[0].Results[0].Target.ShouldBe("/first/a");
        groups[0].Results[0].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Trusted);
    }

    [Fact]
    public async Task SearchAsync_LimitsEachSourceAndSkipsUnavailableContributors()
    {
        var available = new DelegateContributor("available", "Available", (request, _) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(
            [Result($"limit-{request.MaxResults}", "/one"), Result("two", "/two"), Result("three", "/three")]));
        var unavailable = new DelegateContributor("offline", "Offline", (_, _) =>
            throw new InvalidOperationException("Unavailable contributors must not be called."), isAvailable: false);
        var aggregator = CreateAggregator([available, unavailable]);

        var groups = await aggregator.SearchAsync("needle", 2, cancellationToken: CancellationToken.None);

        groups[0].Results.Select(result => result.Title).ShouldBe(["limit-2", "two"]);
        groups[0].Count.ShouldBe(2);
        groups[1].IsAvailable.ShouldBeFalse();
        groups[1].Count.ShouldBe(0);
        groups[1].Results.ShouldBeEmpty();
        unavailable.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryPerformsNoContributorCalls()
    {
        var contributor = new DelegateContributor("source", "Source", (_, _) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([Result("unexpected", "/unexpected")]));
        var aggregator = CreateAggregator([contributor]);

        var groups = await aggregator.SearchAsync("   ", 10, cancellationToken: CancellationToken.None);

        groups.ShouldBeEmpty();
        contributor.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_IsolatesThrowingAndTimedOutSources()
    {
        var timeoutCancellationObserved = NewGate();
        var successful = new DelegateContributor("ok", "OK", (_, _) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([Result("kept", "/kept")]));
        var throwing = new DelegateContributor("throws", "Throws", (_, _) =>
            throw new InvalidOperationException("source exploded"));
        var hanging = new DelegateContributor("slow", "Slow", async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                timeoutCancellationObserved.TrySetResult();
                throw;
            }

            return [];
        });
        var options = new SearchAggregationOptions { DefaultSourceTimeout = TimeSpan.FromSeconds(5) };
        options.SourceTimeouts["slow"] = TimeSpan.FromMilliseconds(50);
        var aggregator = CreateAggregator([successful, throwing, hanging], options);

        var groups = await aggregator.SearchAsync("needle", 10, cancellationToken: CancellationToken.None)
            .WaitAsync(TestTimeout, CancellationToken.None);
        await timeoutCancellationObserved.Task.WaitAsync(TestTimeout, CancellationToken.None);

        groups.Single(group => group.SourceId == "ok").Results.Single().Title.ShouldBe("kept");
        groups.Single(group => group.SourceId == "throws").Error.ShouldBe("Source search failed.");
        groups.Single(group => group.SourceId == "slow").Error.ShouldBe("Source search timed out.");
    }

    [Fact]
    public async Task SearchAsync_CallerCancellationPropagatesInsteadOfBecomingSourceError()
    {
        var entered = NewGate();
        var contributor = new DelegateContributor("source", "Source", async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var aggregator = CreateAggregator([contributor]);
        using var cancellation = new CancellationTokenSource();

        var search = aggregator.SearchAsync("needle", 10, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TestTimeout, CancellationToken.None);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task SearchAsync_SourceFilterSelectsRegisteredContributorsWithoutHardcoding()
    {
        var first = new DelegateContributor("first", "First", (_, _) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([Result("first", "/first")]));
        var extension = new DelegateContributor("test-extension", "Test extension", (_, _) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([Result("extension", "/extension")]));
        var aggregator = CreateAggregator([first, extension]);

        var groups = await aggregator.SearchAsync(
            "needle",
            10,
            new HashSet<string>(["test-extension"], StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        groups.Single().SourceId.ShouldBe("test-extension");
        groups.Single().Results.Single().Target.ShouldBe("/extension");
        first.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task AddBotNexusGateway_ResolvesCollectionRegisteredTestContributor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISearchContributor>(new DelegateContributor(
            "test-extension",
            "Test extension",
            (_, _) => Task.FromResult<IReadOnlyList<SearchResult>>([Result("extension", "/extension")])));
        services.AddBotNexusGateway();
        await using var provider = services.BuildServiceProvider();

        var aggregator = provider.GetRequiredService<SearchAggregator>();
        var groups = await aggregator.SearchAsync("needle", 10, cancellationToken: CancellationToken.None);

        groups.Single().SourceId.ShouldBe("test-extension");
    }

    private static SearchAggregator CreateAggregator(
        IEnumerable<ISearchContributor> contributors,
        SearchAggregationOptions? options = null)
        => new(contributors, Options.Create(options ?? new SearchAggregationOptions()));

    private static TaskCompletionSource NewGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SearchResult Result(
        string title,
        string target,
        SearchProvenanceTrust trust = SearchProvenanceTrust.Untrusted)
        => new(title, $"Snippet for {title}", target, DateTimeOffset.Parse("2026-09-25T00:00:00Z"), 0.75, trust);

    private sealed class DelegateContributor(
        string sourceId,
        string label,
        Func<SearchRequest, CancellationToken, Task<IReadOnlyList<SearchResult>>> search,
        bool isAvailable = true) : ISearchContributor
    {
        private int _callCount;

        public string SourceId => sourceId;
        public string Label => label;
        public bool IsAvailable => isAvailable;
        public bool CanAssessProvenanceTrust => true;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            SearchRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return search(request, cancellationToken);
        }
    }
}
