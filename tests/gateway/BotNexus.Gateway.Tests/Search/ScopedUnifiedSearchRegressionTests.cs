using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Search;

/// <summary>
/// Issue #4741: exercises the real controller/aggregator/contributors without starting a gateway.
/// All backing data is synthetic. These tests intentionally specify behavior absent at the pinned base.
/// </summary>
public sealed class ScopedUnifiedSearchRegressionTests
{
    private const string Allowed = "zzz-allowed";
    private const string Denied = "aaa-denied";
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-25T00:00:00Z");
    private static readonly string[] BuiltInSources = ["agents", "conversations", "sessions", "memory", "files"];

    [Theory]
    [InlineData("agents")]
    [InlineData("conversations")]
    [InlineData("sessions")]
    [InlineData("memory")]
    [InlineData("files")]
    public async Task Get_RestrictedCallerWithoutSelector_ReturnsOnlyAllowedAgentAcrossBuiltIns(string source)
    {
        var fixture = new Fixture();
        var groups = await fixture.SearchAsync(RestrictedCaller(), source: source, limit: 10);

        AssertOnlyAllowed(groups.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task Get_RestrictedCallerNormalUnifiedRequest_HasNoDeniedCountOrPayloadContribution()
    {
        var fixture = new Fixture();
        var groups = await fixture.SearchAsync(RestrictedCaller(), limit: 10);

        groups.Select(group => group.SourceId).ShouldBe(BuiltInSources);
        groups.Sum(group => group.Count).ShouldBe(5);
        foreach (var group in groups)
            AssertOnlyAllowed(group);
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("conversations")]
    [InlineData("sessions")]
    [InlineData("memory")]
    [InlineData("files")]
    public async Task Get_DeniedCandidateOrderedFirst_CannotConsumeAllowedResultBudget(string source)
    {
        // Denied sorts first by agent ID and is newer in both stores. Post-result filtering loses
        // the allowed candidate at limit=1, so sanitizing the final response cannot pass this test.
        var fixture = new Fixture();
        var groups = await fixture.SearchAsync(RestrictedCaller(), source: source, limit: 1);

        AssertOnlyAllowed(groups.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task Get_RestrictedMemorySearch_DoesNotCreateOrSearchDeniedAgentMemory()
    {
        var fixture = new Fixture();
        _ = await fixture.SearchAsync(RestrictedCaller(), source: "memory", limit: 10);

        fixture.MemoryFactory.DidNotReceive().Create(Denied);
        await fixture.DeniedMemory.DidNotReceive().SearchAsync(
            Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>());
        await fixture.AllowedMemory.Received(1).SearchAsync(
            Arg.Is<AgentMemorySearchRequest>(request => request.AgentId == Allowed), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_RestrictedFileSearch_DoesNotResolveOrOpenDeniedWorkspace()
    {
        var fixture = new Fixture();
        _ = await fixture.SearchAsync(RestrictedCaller(), source: "files", limit: 10);

        fixture.OpenedFiles.ShouldContain(fixture.AllowedFile);
        fixture.OpenedFiles.ShouldNotContain(fixture.DeniedFile);
        fixture.Workspaces.DidNotReceive().GetWorkspacePath(Denied);
    }

    [Fact]
    public async Task Get_RestrictedConversationSearch_ReadsOnlyAuthorizedAgentStorePartition()
    {
        var fixture = new Fixture();
        _ = await fixture.SearchAsync(RestrictedCaller(), source: "conversations", limit: 10);

        fixture.ConversationQueries.ShouldNotBeEmpty();
        fixture.ConversationQueries.ShouldAllBe(agent => agent == Allowed);
    }

    [Fact]
    public async Task Get_RestrictedSessionSearch_AppliesAgentPredicateBeforeMetadataWindow()
    {
        var fixture = new Fixture(deniedSessionCount: SessionSearchContributor.MaxScannedSummaries);
        var groups = await fixture.SearchAsync(RestrictedCaller(), source: "sessions", limit: 1);

        fixture.SessionQueries.ShouldNotBeEmpty();
        fixture.SessionQueries.ShouldAllBe(query => query.AgentId == Allowed);
        fixture.SessionQueries.ShouldAllBe(query => query.ConversationIds != null
            && query.ConversationIds.SetEquals(new[] { ConversationId.From("needle-allowed") }));
        fixture.SessionQueries.ShouldAllBe(query => query.Limit > 0 && query.Limit <= SessionSearchContributor.MaxScannedSummaries);
        AssertOnlyAllowed(groups.ShouldHaveSingleItem());
        await fixture.Sessions.DidNotReceive().ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("agentId")]
    public async Task Get_DeniedSelector_CannotWidenRestrictedIdentityOrCauseBackingReads(string selectorName)
    {
        var fixture = new Fixture();
        var controller = fixture.Controller(RestrictedCaller(), $"?{selectorName}={Denied}");

        var response = await controller.Get("needle", null, 10, CancellationToken.None);

        // Either a forbidden response or an empty successful search is fail-closed. The assertion
        // also applies when this action is invoked after middleware, without relying on its filter.
        if (response.Result is ObjectResult { StatusCode: StatusCodes.Status403Forbidden })
        {
            fixture.AssertNoBackingReads();
            return;
        }
        Groups(response).SelectMany(group => group.Results).ShouldBeEmpty();
        Groups(response).Sum(group => group.Count).ShouldBe(0);
        fixture.AssertNoBackingReads();
    }

    [Theory]
    [InlineData("agent", "unrestricted")]
    [InlineData("agentId", "unrestricted")]
    [InlineData("agent", "admin")]
    [InlineData("agentId", "admin")]
    [InlineData("agent", "restricted")]
    [InlineData("agentId", "restricted")]
    public async Task Get_AllowedSelector_NarrowsButNeverReplacesAuthenticatedScope(string selectorName, string callerMode)
    {
        var fixture = new Fixture();
        var groups = await fixture.SearchAsync(Caller(callerMode), selector: $"?{selectorName}={Allowed}", limit: 10);

        groups.Count.ShouldBe(5);
        foreach (var group in groups)
            AssertOnlyAllowed(group);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("unrestricted")]
    public async Task Get_UnrestrictedCallerWithoutSelector_RetainsBothAgents(string callerMode)
    {
        var fixture = new Fixture();
        var groups = await fixture.SearchAsync(Caller(callerMode), limit: 10);

        groups.Count.ShouldBe(5);
        foreach (var group in groups)
        {
            group.Error.ShouldBeNull();
            group.Count.ShouldBe(2);
            group.Results.ShouldContain(result => result.Target.Contains(Allowed, StringComparison.Ordinal));
            group.Results.ShouldContain(result => result.Target.Contains(Denied, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("restricted", "conversations")]
    [InlineData("admin", "conversations")]
    [InlineData("unrestricted", "conversations")]
    [InlineData("restricted", "sessions")]
    [InlineData("admin", "sessions")]
    [InlineData("unrestricted", "sessions")]
    public async Task Get_InternalHiddenConversationAndLinkedSession_AreExcludedBeforeLimitEvenForAdmin(string callerMode, string source)
    {
        var fixture = new Fixture(includeVisibilityRecords: true, includeDenied: false);
        var groups = await fixture.SearchAsync(Caller(callerMode), source: source, limit: 1);

        var group = groups.ShouldHaveSingleItem();
        group.Error.ShouldBeNull();
        group.Count.ShouldBe(1);
        var result = group.Results.ShouldHaveSingleItem();
        // The inspectable record is newer than the user-facing record; it must survive. Its ID
        // deliberately looks internal, while the hidden record has an ordinary opaque ID.
        result.Target.ShouldBe($"/chat/{Allowed}/internal%3Aneedle-inspectable");
        result.Title.ShouldContain("inspectable");
        result.Snippet.ShouldNotContain("hidden-secret");
        result.Target.ShouldNotContain("opaque-hidden");
        result.ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
    }

    [Fact]
    public async Task Get_AllowedMemorySourceFailure_IsIsolatedWithoutFallingBackToDeniedAgent()
    {
        var fixture = new Fixture();
        fixture.AllowedMemory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AgentMemorySearchResult>>(
                new InvalidOperationException("synthetic source failure")));

        var groups = await fixture.SearchAsync(RestrictedCaller(), limit: 10);

        var memory = groups.Single(group => group.SourceId == "memory");
        memory.Error.ShouldBe("Source search failed.");
        memory.Count.ShouldBe(0);
        memory.Results.ShouldBeEmpty();
        fixture.MemoryFactory.DidNotReceive().Create(Denied);
        foreach (var group in groups.Where(group => group.SourceId != "memory"))
            AssertOnlyAllowed(group);
    }

    [Fact]
    public async Task ConversationContributor_InternalHiddenRecords_DoNotProduceSnippetsOrConsumeLimit()
    {
        var fixture = new Fixture(includeVisibilityRecords: true, includeDenied: false);
        var contributor = new ConversationSearchContributor(fixture.Conversations, Substitute.For<IAgentRegistry>());

        var results = await contributor.SearchAsync(new SearchRequest("needle", 1));

        results.ShouldHaveSingleItem().Title.ShouldBe("needle inspectable");
        results.ShouldAllBe(result => !result.Snippet.Contains("hidden-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("test-extension")]
    [InlineData("agents")]
    [InlineData("conversations")]
    [InlineData("sessions")]
    [InlineData("memory")]
    [InlineData("files")]
    public async Task Get_UnapprovedExtensionIncludingSpoofedBuiltInId_IsNotInvokedForRestrictedCaller(string sourceId)
    {
        var extension = new UnapprovedContributor(sourceId);
        var controller = CreateController([extension], RestrictedCaller());

        var response = await controller.Get("needle", sourceId, 10, CancellationToken.None);

        extension.SearchCalls.ShouldBe(0);
        extension.AvailabilityReads.ShouldBe(0);
        Groups(response).SelectMany(group => group.Results).ShouldBeEmpty();
        Groups(response).Sum(group => group.Count).ShouldBe(0);
    }

    [Theory]
    [InlineData("restricted")]
    [InlineData("admin")]
    [InlineData("unrestricted")]
    public async Task Get_ExtensionWithoutServerApprovedScopePolicy_IsFailClosedRegardlessOfCallerMode(string callerMode)
    {
        var extension = new UnapprovedContributor("test-extension");
        var controller = CreateController([extension], Caller(callerMode));

        var response = await controller.Get("needle", null, 10, CancellationToken.None);

        extension.SearchCalls.ShouldBe(0);
        Groups(response).SelectMany(group => group.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_RestrictedWhitespaceQuery_PerformsNoBackingReads()
    {
        var fixture = new Fixture();
        var response = await fixture.Controller(RestrictedCaller()).Get("   ", null, 10, CancellationToken.None);

        Groups(response).ShouldBeEmpty();
        fixture.AssertNoBackingReads();
    }

    [Fact]
    public async Task Get_RestrictedCancelledRequest_PerformsNoBackingReadsAndPropagatesCancellation()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            fixture.Controller(RestrictedCaller()).Get("needle", null, 10, cancellation.Token));

        fixture.AssertNoBackingReads();
    }

    [Theory]
    [InlineData("?agent=zzz-allowed&agent=zzz-allowed")]
    [InlineData("?agent=zzz-allowed&agentId=aaa-denied")]
    [InlineData("?agent=")]
    public async Task Get_AmbiguousSelectors_FailClosedWithoutBackingReads(string selector)
    {
        var fixture = new Fixture();
        var response = await fixture.Controller(RestrictedCaller(), selector).Get("needle", null, 10);
        response.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        fixture.AssertNoBackingReads();
    }

    [Fact]
    public async Task Get_MissingIdentity_FailsClosed()
    {
        var context = new DefaultHttpContext();
        var extension = new UnapprovedContributor("test-extension");
        var controller = new SearchController(new SearchAggregator([extension], Options.Create(new SearchAggregationOptions())))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var response = await controller.Get("needle", null, 10);
        response.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        extension.AvailabilityReads.ShouldBe(0);
        extension.SearchCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Get_CaseInsensitiveScopeAndMatchingAliases_RetainsAllowedPartition()
    {
        var fixture = new Fixture();
        var caller = new GatewayCallerIdentity { CallerId = "case", AllowedAgents = [Allowed.ToUpperInvariant()] };
        var groups = await fixture.SearchAsync(caller, selector: $"?agent={Allowed}&agentId={Allowed.ToUpperInvariant()}");
        foreach (var group in groups)
            AssertOnlyAllowed(group);
    }

    [Fact]
    public async Task Get_MultipleAllowedAgents_ReadsOnlyExplicitPartitions()
    {
        var fixture = new Fixture();
        var caller = new GatewayCallerIdentity { CallerId = "multiple", AllowedAgents = [Allowed, Denied] };
        var groups = await fixture.SearchAsync(caller);
        groups.ShouldAllBe(group => group.Count == 2);
        fixture.ConversationQueries.ShouldNotBeEmpty();
        fixture.ConversationQueries.ShouldAllBe(agent => agent == Allowed || agent == Denied);
    }

    [Fact]
    public async Task SessionContributor_HiddenSummariesCannotConsumeFiveHundredRowWindow()
    {
        var fixture = new Fixture(includeVisibilityRecords: true, includeDenied: false, hiddenSessionCount: 500);
        var groups = await fixture.SearchAsync(RestrictedCaller(), source: "sessions", limit: 1);
        groups.ShouldHaveSingleItem().Results.ShouldHaveSingleItem().Target
            .ShouldBe($"/chat/{Allowed}/internal%3Aneedle-inspectable");
        fixture.SessionQueries.ShouldAllBe(query => query.ConversationIds != null
            && !query.ConversationIds.Contains(ConversationId.From("opaque-hidden")));
    }

    [Fact]
    public async Task ExplicitEmptyScope_IsNoneAndDoesNotReadStores()
    {
        var fixture = new Fixture();
        var contributor = new SessionSearchContributor(fixture.Sessions, fixture.Conversations, Substitute.For<IAgentRegistry>());
        (await contributor.SearchAsync(new SearchRequest("needle", 10, SearchScope.ForAgents([])))).ShouldBeEmpty();
        fixture.AssertNoBackingReads();
        SearchScope.All.Allows(AgentId.From(Allowed)).ShouldBeTrue();
        var agents = new List<AgentId> { AgentId.From(Allowed.ToUpperInvariant()) };
        var scope = SearchScope.ForAgents(agents);
        agents.Clear();
        scope.Allows(AgentId.From(Allowed)).ShouldBeTrue();
        scope.Allows(AgentId.From(Denied)).ShouldBeFalse();
    }

    private static void AssertOnlyAllowed(AggregatedSearchGroup group)
    {
        group.Error.ShouldBeNull();
        group.Count.ShouldBe(1);
        var result = group.Results.ShouldHaveSingleItem();
        result.Target.ShouldContain(Allowed);
        result.Title.ShouldNotContain(Denied);
        result.Snippet.ShouldNotContain(Denied);
        result.Target.ShouldNotContain(Denied);
        result.Snippet.ShouldNotContain("denied-secret");
    }

    private static GatewayCallerIdentity RestrictedCaller()
        => new() { CallerId = "synthetic-restricted", AllowedAgents = [Allowed] };

    private static GatewayCallerIdentity Caller(string mode)
        => mode switch
        {
            "restricted" => RestrictedCaller(),
            // Admin bypass is tested even with a nonempty agent list.
            "admin" => new() { CallerId = "synthetic-admin", IsAdmin = true, AllowedAgents = [Allowed] },
            "unrestricted" => new() { CallerId = "synthetic-unrestricted", AllowedAgents = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static SearchController CreateController(IEnumerable<ISearchContributor> contributors, GatewayCallerIdentity caller, string selector = "")
    {
        var context = new DefaultHttpContext();
        context.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = caller;
        context.Request.QueryString = new QueryString(selector);
        return new SearchController(new SearchAggregator(contributors, Options.Create(new SearchAggregationOptions())))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static IReadOnlyList<AggregatedSearchGroup> Groups(ActionResult<IReadOnlyList<AggregatedSearchGroup>> response)
    {
        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        var value = ok.Value ?? throw new InvalidOperationException("Search response had no value.");
        return value.ShouldBeAssignableTo<IReadOnlyList<AggregatedSearchGroup>>();
    }

    private sealed class Fixture
    {
        public IAgentMemoryFactory MemoryFactory { get; } = Substitute.For<IAgentMemoryFactory>();
        public IAgentMemory AllowedMemory { get; } = Substitute.For<IAgentMemory>();
        public IAgentMemory DeniedMemory { get; } = Substitute.For<IAgentMemory>();
        public IAgentWorkspaceManager Workspaces { get; } = Substitute.For<IAgentWorkspaceManager>();
        public IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();
        public ISessionStore Sessions { get; } = Substitute.For<ISessionStore>();
        public ConcurrentQueue<string?> ConversationQueries { get; } = new();
        public ConcurrentQueue<SessionSummaryQuery> SessionQueries { get; } = new();
        public ConcurrentQueue<string> OpenedFiles { get; } = new();
        public string AllowedFile { get; }
        public string DeniedFile { get; }
        private readonly ISearchContributor[] _contributors;

        public Fixture(int deniedSessionCount = 1, bool includeVisibilityRecords = false, bool includeDenied = true, int hiddenSessionCount = 1)
        {
            var registry = Substitute.For<IAgentRegistry>();
            registry.GetAll().Returns(includeDenied ? [Descriptor(Denied), Descriptor(Allowed)] : [Descriptor(Allowed)]);
            SetupMemory(AllowedMemory, Allowed, "needle allowed content");
            SetupMemory(DeniedMemory, Denied, "needle denied-secret");
            MemoryFactory.Create(Allowed).Returns(AllowedMemory);
            MemoryFactory.Create(Denied).Returns(DeniedMemory);

            var root = Path.Combine(Path.GetTempPath(), "botnexus-4741-synthetic");
            var allowedWorkspace = Path.Combine(root, Allowed, "workspace");
            var deniedWorkspace = Path.Combine(root, Denied, "workspace");
            AllowedFile = Path.Combine(allowedWorkspace, "needle-allowed.txt");
            DeniedFile = Path.Combine(deniedWorkspace, "needle-denied.txt");
            var mock = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                [AllowedFile] = new("needle allowed content"),
                [DeniedFile] = new("needle denied-secret")
            }, root);
            Workspaces.GetWorkspacePath(Allowed).Returns(allowedWorkspace);
            Workspaces.GetWorkspacePath(Denied).Returns(deniedWorkspace);
            var files = Substitute.For<IFile>();
            files.Open(Arg.Any<string>(), Arg.Any<FileMode>(), Arg.Any<FileAccess>(), Arg.Any<FileShare>())
                .Returns(call =>
                {
                    var path = call.Arg<string>();
                    OpenedFiles.Enqueue(path);
                    return mock.File.Open(path, call.Arg<FileMode>(), call.Arg<FileAccess>(), call.Arg<FileShare>());
                });
            var fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(files);
            fileSystem.Directory.Returns(mock.Directory);
            fileSystem.Path.Returns(mock.Path);
            fileSystem.FileInfo.Returns(mock.FileInfo);

            List<Conversation> conversations = [Conversation(Allowed, "needle-allowed", ConversationVisibility.UserFacing, Epoch)];
            if (includeDenied)
                conversations.Add(Conversation(Denied, "needle-denied", ConversationVisibility.UserFacing, Epoch.AddHours(1)));
            if (includeVisibilityRecords)
            {
                conversations.Add(Conversation(Allowed, "opaque-hidden", ConversationVisibility.InternalHidden, Epoch.AddHours(3)));
                conversations.Add(Conversation(Allowed, "internal:needle-inspectable", ConversationVisibility.InspectableReadOnly, Epoch.AddHours(2)));
            }
            Conversations.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var agent = call.Arg<AgentId?>();
                ConversationQueries.Enqueue(agent?.Value);
                return Task.FromResult<IReadOnlyList<Conversation>>(conversations.Where(item => agent is null || item.AgentId == agent).ToArray());
            });
            Conversations.GetAsync(ConversationId.From("synthetic-probe"), CancellationToken.None).ReturnsForAnyArgs(call =>
                Task.FromResult(conversations.SingleOrDefault(item => item.ConversationId == call.Arg<ConversationId>())));
            List<SessionSummary> summaries = conversations.Where(item => item.AgentId.Value == Allowed).Select(Summary).ToList();
            if (includeVisibilityRecords && hiddenSessionCount > 1)
                summaries.AddRange(Enumerable.Range(1, hiddenSessionCount - 1).Select(index => Summary(
                    conversations.Single(item => item.Visibility == ConversationVisibility.InternalHidden)) with { SessionId = $"needle-hidden-{index:D4}" }));
            if (includeDenied)
                summaries.AddRange(Enumerable.Range(0, deniedSessionCount).Select(index => Summary(
                    conversations.Single(item => item.AgentId.Value == Denied)) with { SessionId = $"needle-{Denied}-{index:D4}" }));
            Sessions.ListSummaryPageAsync(Arg.Any<SessionSummaryQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var query = call.Arg<SessionSummaryQuery>();
                SessionQueries.Enqueue(query);
                var matching = summaries.Where(query.Matches).OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.SessionId, StringComparer.Ordinal).ToArray();
                var page = matching.Skip(query.Offset).Take(query.Limit ?? matching.Length).ToArray();
                return Task.FromResult(new SessionSummaryPage(page, matching.Length, query.Offset + page.Length < matching.Length));
            });
            _contributors = [
                new AgentSearchContributor(registry),
                new ConversationSearchContributor(Conversations, registry),
                new SessionSearchContributor(Sessions, Conversations, registry),
                new MemorySearchContributor(registry, MemoryFactory),
                new FileSearchContributor(registry, Workspaces, fileSystem)
            ];
        }

        public SearchController Controller(GatewayCallerIdentity caller, string selector = "")
            => CreateController(_contributors, caller, selector);

        public async Task<IReadOnlyList<AggregatedSearchGroup>> SearchAsync(GatewayCallerIdentity caller, string? source = null, int limit = 10, string selector = "")
            => Groups(await Controller(caller, selector).Get("needle", source, limit, CancellationToken.None));

        public void AssertNoBackingReads()
        {
            ConversationQueries.ShouldBeEmpty();
            SessionQueries.ShouldBeEmpty();
            OpenedFiles.ShouldBeEmpty();
            MemoryFactory.DidNotReceive().Create(Arg.Any<string>());
            Workspaces.DidNotReceive().GetWorkspacePath(Arg.Any<string>());
        }

        private static AgentDescriptor Descriptor(string id)
            => new()
            {
                AgentId = AgentId.From(id), DisplayName = $"needle {id}",
                ModelId = "test-model", ApiProvider = "test-provider",
                Description = $"needle {id} description", Memory = new MemoryAgentConfig { Enabled = true }
            };

        private static void SetupMemory(IAgentMemory memory, string agent, string content)
            => memory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([
                    new($"needle-{agent}", content, "manual", null, Epoch, 0.9) { TrustTier = "trusted" }
                ]));

        private static Conversation Conversation(string agent, string id, ConversationVisibility visibility, DateTimeOffset updated)
            => new()
            {
                AgentId = AgentId.From(agent), ConversationId = ConversationId.From(id),
                Title = visibility == ConversationVisibility.InspectableReadOnly ? "needle inspectable" : $"needle {agent} {id}",
                Purpose = visibility == ConversationVisibility.InternalHidden ? "needle hidden-secret" : $"needle {agent} purpose",
                Visibility = visibility, UpdatedAt = updated
            };

        private static SessionSummary Summary(Conversation conversation)
            => new($"needle-{conversation.ConversationId.Value}", conversation.AgentId.Value, ChannelKey.From("web"),
                SessionStatus.Active, SessionType.UserAgent, true, 3, Epoch.AddDays(-1), conversation.UpdatedAt, conversation.ConversationId.Value);
    }

    private sealed class UnapprovedContributor(string sourceId) : ISearchContributor
    {
        private int _searchCalls;
        private int _availabilityReads;
        public string SourceId => sourceId;
        public string Label => "Unapproved synthetic extension";
        public bool IsAvailable { get { Interlocked.Increment(ref _availabilityReads); return true; } }
        public bool CanAssessProvenanceTrust => true;
        public int SearchCalls => Volatile.Read(ref _searchCalls);
        public int AvailabilityReads => Volatile.Read(ref _availabilityReads);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCalls);
            // An allowed-looking URL and a trust claim must not authorize this denied content.
            return Task.FromResult<IReadOnlyList<SearchResult>>([
                new($"needle {Denied}", "needle denied-secret", $"/chat/{Allowed}/needle-allowed", Epoch,
                    ProvenanceTrust: SearchProvenanceTrust.Trusted)
            ]);
        }
    }
}
