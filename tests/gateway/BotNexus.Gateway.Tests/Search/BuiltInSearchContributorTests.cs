using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Search;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests.Search;

public sealed class BuiltInSearchContributorTests
{
    [Fact]
    public async Task MemoryContributor_UsesNativeSearchAndPreservesScoreTrustTargetAndBounds()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([
            Descriptor("alpha", memoryEnabled: true),
            Descriptor("disabled", memoryEnabled: false)
        ]);

        var memory = Substitute.For<IAgentMemory>();
        memory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AgentMemorySearchRequest>();
                request.AgentId.ShouldBe("alpha");
                request.Query.ShouldBe("needle");
                request.TopK.ShouldBe(2);
                return Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([
                    new("entry/1", "first matching memory", "manual", null, DateTimeOffset.Parse("2026-09-24T12:00:00Z"), 0.9)
                    {
                        TrustTier = "trusted"
                    },
                    new("entry-2", "second matching memory", "conversation", "session-1", DateTimeOffset.Parse("2026-09-23T12:00:00Z"), 0.7)
                    {
                        TrustTier = "unknown"
                    },
                    new("entry-3", "provider over-return", "manual", null, DateTimeOffset.Parse("2026-09-22T12:00:00Z"), 0.6)
                    {
                        TrustTier = "trusted"
                    }
                ]);
            });

        var factory = Substitute.For<IAgentMemoryFactory>();
        factory.Create("alpha").Returns(memory);
        var contributor = new MemorySearchContributor(registry, factory);
        using var cancellation = new CancellationTokenSource();

        var results = await contributor.SearchAsync(new SearchRequest("needle", 2), cancellation.Token);

        contributor.SourceId.ShouldBe("memory");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeTrue();
        results.Count.ShouldBe(2);
        results[0].Relevance.ShouldBe(0.9);
        results[0].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Trusted);
        results[0].Target.ShouldBe("/memory/alpha/entries/entry%2F1");
        results[1].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
        await memory.Received(1).SearchAsync(Arg.Any<AgentMemorySearchRequest>(), cancellation.Token);
        factory.DidNotReceive().Create("disabled");
    }

    [Fact]
    public async Task MemoryContributor_PropagatesCancellation()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([Descriptor("alpha", memoryEnabled: true)]);
        var memory = Substitute.For<IAgentMemory>();
        memory.SearchAsync(Arg.Any<AgentMemorySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromCanceled<IReadOnlyList<AgentMemorySearchResult>>(call.Arg<CancellationToken>()));
        var factory = Substitute.For<IAgentMemoryFactory>();
        factory.Create("alpha").Returns(memory);
        var contributor = new MemorySearchContributor(registry, factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            contributor.SearchAsync(new SearchRequest("needle", 5), cancellation.Token));
    }

    [Fact]
    public async Task FileContributor_MatchesContentAndFileNameWithinBoundAndSkipsDeniedFiles()
    {
        const string workspace = "/agents/alpha/workspace";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{workspace}/notes/allowed.txt"] = new("first line\nneedle in allowed content\nlast line"),
            [$"{workspace}/needle-name.md"] = new("filename-only match"),
            [$"{workspace}/private/denied.txt"] = new("needle must not escape policy"),
            [$"{workspace}/other.txt"] = new("nothing relevant")
        }, workspace);
        fileSystem.File.SetLastWriteTimeUtc($"{workspace}/notes/allowed.txt", DateTime.Parse("2026-09-24T10:00:00Z").ToUniversalTime());

        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([Descriptor("alpha", deniedPaths: [$"{workspace}/private/**"])]);
        var workspaces = Substitute.For<IAgentWorkspaceManager>();
        workspaces.GetWorkspacePath("alpha").Returns(workspace);
        var contributor = new FileSearchContributor(registry, workspaces, fileSystem);

        var results = await contributor.SearchAsync(new SearchRequest("needle", 2));

        contributor.SourceId.ShouldBe("files");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeFalse();
        results.Count.ShouldBe(2);
        results.ShouldAllBe(result => result.ProvenanceTrust == SearchProvenanceTrust.Untrusted);
        results.ShouldContain(result => result.Snippet.Contains("needle in allowed content", StringComparison.Ordinal));
        results.ShouldContain(result => result.Title == "needle-name.md");
        results.ShouldNotContain(result => result.Target.Contains("denied.txt", StringComparison.Ordinal));
        results.ShouldAllBe(result => result.Snippet.Length <= FileSearchContributor.MaxSnippetLength);
    }

    [Fact]
    public async Task FileContributor_ReturnsNoResultsWhenWorkspaceIsUnavailable()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([Descriptor("alpha")]);
        var workspaces = Substitute.For<IAgentWorkspaceManager>();
        workspaces.GetWorkspacePath("alpha").Returns("/agents/alpha/workspace");
        var contributor = new FileSearchContributor(registry, workspaces, new MockFileSystem());

        contributor.IsAvailable.ShouldBeFalse();
        (await contributor.SearchAsync(new SearchRequest("needle", 5))).ShouldBeEmpty();
    }

    [Fact]
    public void GatewayRegistration_ExposesBuiltInsThroughSharedContributorContract()
    {
        var services = new ServiceCollection();
        services.AddBotNexusGateway();

        var contributorTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(ISearchContributor))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        contributorTypes.ShouldContain(typeof(MemorySearchContributor));
        contributorTypes.ShouldContain(typeof(FileSearchContributor));
        contributorTypes.ShouldContain(typeof(AgentSearchContributor));
        contributorTypes.ShouldContain(typeof(ConversationSearchContributor));
        contributorTypes.ShouldContain(typeof(SessionSearchContributor));
    }

    [Fact]
    public async Task AgentContributor_MatchesCaseInsensitivelyOrdersDeterministicallyAndBoundsResults()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([
            Descriptor("zeta", description: "Needle worker"),
            Descriptor("alpha", summary: "finds NEEDLE records"),
            Descriptor("other", description: "unrelated")
        ]);
        var contributor = new AgentSearchContributor(registry);

        var results = await contributor.SearchAsync(new SearchRequest("needle", 1));

        contributor.SourceId.ShouldBe("agents");
        contributor.Label.ShouldBe("Agents");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeFalse();
        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("alpha");
        results[0].Target.ShouldBe("/agents/alpha");
        results[0].Snippet.Length.ShouldBeLessThanOrEqualTo(AgentSearchContributor.MaxSnippetLength);
        results[0].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
        (await contributor.SearchAsync(new SearchRequest("absent", 10))).ShouldBeEmpty();
        (await contributor.SearchAsync(new SearchRequest("   ", 10))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ConversationContributor_UsesNativeSummariesAndProducesChatTargets()
    {
        var store = Substitute.For<IConversationStore>();
        store.ListAsync(null, Arg.Any<CancellationToken>()).Returns([
            Conversation("c/late", "agent one", "Needle later", DateTimeOffset.Parse("2026-09-24T00:00:00Z"),
                purpose: new string('x', 300) + " needle"),
            Conversation("c-early", "agent-two", "NEEDLE earlier", DateTimeOffset.Parse("2026-09-23T00:00:00Z")),
            Conversation("c-other", "agent-two", "unrelated", DateTimeOffset.Parse("2026-09-22T00:00:00Z"))
        ]);
        var contributor = new ConversationSearchContributor(store);

        var results = await contributor.SearchAsync(new SearchRequest("needle", 2));

        contributor.SourceId.ShouldBe("conversations");
        contributor.Label.ShouldBe("Conversations");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeFalse();
        results.Select(result => result.Title).ShouldBe(["Needle later", "NEEDLE earlier"]);
        results[0].Target.ShouldBe("/chat/agent%20one/c%2Flate");
        results.ShouldAllBe(result => result.Snippet.Length <= ConversationSearchContributor.MaxSnippetLength);
        results.ShouldAllBe(result => result.ProvenanceTrust == SearchProvenanceTrust.Untrusted);
        (await contributor.SearchAsync(new SearchRequest("absent", 5))).ShouldBeEmpty();
        (await contributor.SearchAsync(new SearchRequest(string.Empty, 5))).ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionContributor_UsesBoundedSummaryPageWithoutMaterializingTranscripts()
    {
        var store = Substitute.For<ISessionStore>();
        store.ListSummaryPageAsync(Arg.Any<SessionSummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<SessionSummaryQuery>();
                query.Limit.ShouldBe(SessionSearchContributor.MaxScannedSummaries);
                query.IncludeInactive.ShouldBeTrue();
                return Task.FromResult(new SessionSummaryPage([
                    Summary("session/2", "agent one", "conversation/2", DateTimeOffset.Parse("2026-09-24T00:00:00Z")),
                    Summary("session-1", "other", "conversation-1", DateTimeOffset.Parse("2026-09-23T00:00:00Z")),
                    Summary("session-0", "agent one", "conversation-0", DateTimeOffset.Parse("2026-09-22T00:00:00Z"))
                ], 3, false));
            });
        var contributor = new SessionSearchContributor(store);

        var results = await contributor.SearchAsync(new SearchRequest("agent ONE", 1));

        contributor.SourceId.ShouldBe("sessions");
        contributor.Label.ShouldBe("Sessions");
        contributor.IsAvailable.ShouldBeTrue();
        contributor.CanAssessProvenanceTrust.ShouldBeFalse();
        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("session/2");
        results[0].Target.ShouldBe("/chat/agent%20one/conversation%2F2");
        results[0].Snippet.Length.ShouldBeLessThanOrEqualTo(SessionSearchContributor.MaxSnippetLength);
        results[0].ProvenanceTrust.ShouldBe(SearchProvenanceTrust.Untrusted);
        await store.DidNotReceive().ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewBuiltInContributors_HonorEmptyBoundsCancellationAndAgentUnavailability()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([]);
        var conversationStore = Substitute.For<IConversationStore>();
        var sessionStore = Substitute.For<ISessionStore>();
        ISearchContributor[] contributors = [
            new AgentSearchContributor(registry),
            new ConversationSearchContributor(conversationStore),
            new SessionSearchContributor(sessionStore)
        ];

        contributors[0].IsAvailable.ShouldBeFalse();
        contributors[1].IsAvailable.ShouldBeTrue();
        contributors[2].IsAvailable.ShouldBeTrue();
        foreach (var contributor in contributors)
        {
            (await contributor.SearchAsync(new SearchRequest("needle", 0))).ShouldBeEmpty();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() =>
                contributor.SearchAsync(new SearchRequest("needle", 5), cancellation.Token));
        }

        await conversationStore.DidNotReceive().ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>());
        await sessionStore.DidNotReceive().ListSummaryPageAsync(Arg.Any<SessionSummaryQuery>(), Arg.Any<CancellationToken>());
    }

    private static AgentDescriptor Descriptor(
        string id,
        bool memoryEnabled = false,
        IReadOnlyList<string>? deniedPaths = null,
        string? description = null,
        string? summary = null)
        => new()
        {
            AgentId = AgentId.From(id),
            DisplayName = id,
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Description = description,
            Summary = summary,
            Memory = memoryEnabled ? new MemoryAgentConfig { Enabled = true } : null,
            FileAccess = deniedPaths is null ? null : new FileAccessPolicy { DeniedPaths = deniedPaths }
        };

    private static Conversation Conversation(
        string conversationId,
        string agentId,
        string title,
        DateTimeOffset updatedAt,
        string? purpose = null)
        => new()
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = AgentId.From(agentId),
            Title = title,
            Purpose = purpose,
            UpdatedAt = updatedAt
        };

    private static SessionSummary Summary(
        string sessionId,
        string agentId,
        string conversationId,
        DateTimeOffset updatedAt)
        => new(
            sessionId,
            agentId,
            ChannelKey.From("web"),
            SessionStatus.Active,
            SessionType.UserAgent,
            true,
            3,
            updatedAt.AddHours(-1),
            updatedAt,
            conversationId);
}
