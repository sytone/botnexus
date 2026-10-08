using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Search;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Search;

public sealed class ScopedSearchStoreBoundaryTests
{
    private static readonly string HistoryEntry = JsonSerializer.Serialize(
        new SessionEntry { Role = MessageRole.User, Content = "synthetic" },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    [Theory]
    [InlineData("mixed-agent")]
    [InlineData("MIXED-AGENT")]
    public async Task Contributors_ResolveCanonicalPartitionWithoutReadingDeniedPartitions(string allowed)
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAll().Returns([
            new AgentDescriptor { AgentId = AgentId.From("Mixed-Agent"), DisplayName = "allowed", ModelId = "test", ApiProvider = "test" },
            new AgentDescriptor { AgentId = AgentId.From("Denied-Agent"), DisplayName = "denied", ModelId = "test", ApiProvider = "test" }
        ]);
        var conversation = new Conversation { ConversationId = ConversationId.From("needle-conversation"), AgentId = AgentId.From("Mixed-Agent"), Title = "needle" };
        var conversations = Substitute.For<IConversationStore>();
        conversations.ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<AgentId?>().ShouldBe(AgentId.From("Mixed-Agent"));
            return Task.FromResult<IReadOnlyList<Conversation>>([conversation]);
        });
        var sessions = Substitute.For<ISessionStore>();
        sessions.ListSummaryPageAsync(Arg.Any<SessionSummaryQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.Arg<SessionSummaryQuery>();
            query.AgentId.ShouldBe("Mixed-Agent");
            query.ConversationIds.ShouldNotBeNull().SetEquals([conversation.ConversationId]).ShouldBeTrue();
            return Task.FromResult(new SessionSummaryPage([
                new SessionSummary("needle-session", "Mixed-Agent", null, SessionStatus.Active, SessionType.UserAgent, true, 1,
                    DateTimeOffset.MinValue, DateTimeOffset.MinValue, conversation.ConversationId.Value)
            ], 1, false));
        });
        var request = new SearchRequest("needle", 1, SearchScope.ForAgents([AgentId.From(allowed)]));
        (await new ConversationSearchContributor(conversations, registry).SearchAsync(request)).ShouldHaveSingleItem()
            .Target.ShouldBe("/chat/Mixed-Agent/needle-conversation");
        (await new SessionSearchContributor(sessions, conversations, registry).SearchAsync(request)).ShouldHaveSingleItem()
            .Target.ShouldBe("/chat/Mixed-Agent/needle-conversation");
        await conversations.Received(2).ListAsync(AgentId.From("Mixed-Agent"), Arg.Any<CancellationToken>());
        await conversations.DidNotReceive().ListAsync(null, Arg.Any<CancellationToken>());
        await conversations.DidNotReceive().ListAsync(AgentId.From("Denied-Agent"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilePage_EligibilityPrecedesHistoryResolutionMigrationAndFiveHundredRowWindow()
    {
        var fixture = new FileFixture();
        var eligible = ConversationId.From("allowed-conversation");
        fixture.Add("needle/allowed", eligible.Value, DateTimeOffset.MinValue, $"{HistoryEntry}\nnot-json\nnull\n{HistoryEntry}\n");
        for (var index = 0; index < 500; index++)
        {
            fixture.Add($"denied-{index}", "denied-conversation", DateTimeOffset.MaxValue, "denied malformed transcript");
            fixture.Add($"hidden-{index}", "hidden-conversation", DateTimeOffset.MaxValue, "hidden malformed transcript");
        }
        fixture.Add("legacy-orphan", null, DateTimeOffset.MaxValue, "orphan transcript");
        fixture.Conversations.GetAsync(eligible, Arg.Any<CancellationToken>()).Returns(
            new Conversation { ConversationId = eligible, AgentId = AgentId.From("Mixed-Agent"), Title = "allowed" });
        var query = new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "Mixed-Agent", IncludeInactive: true,
            Limit: 500, ConversationIds: new HashSet<ConversationId> { eligible });

        var page = await fixture.Store.ListSummaryPageAsync(query);

        var summary = page.Items.ShouldHaveSingleItem();
        summary.SessionId.ShouldBe("needle/allowed");
        summary.MessageCount.ShouldBe(2);
        summary.AgentId.ShouldBe("Mixed-Agent");
        page.TotalCount.ShouldBe(1);
        page.HasMore.ShouldBeFalse();
        fixture.HistoryReads.ShouldBe([fixture.HistoryPath("needle/allowed")]);
        fixture.Store.MigrationInvocationCount.ShouldBe(0);
        await fixture.Conversations.Received(1).GetAsync(eligible, Arg.Any<CancellationToken>());
        await fixture.Conversations.DidNotReceive().GetAsync(ConversationId.From("denied-conversation"), Arg.Any<CancellationToken>());
        await fixture.Conversations.DidNotReceive().GetAsync(ConversationId.From("hidden-conversation"), Arg.Any<CancellationToken>());
        await fixture.Conversations.DidNotReceive().ListAsync(Arg.Any<AgentId?>(), Arg.Any<CancellationToken>());
        await fixture.Files.DidNotReceive().WriteAllTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilePage_EmptyEligibilityDoesNotReadMetadataOrHistory()
    {
        var fixture = new FileFixture();
        fixture.Add("denied", "denied-conversation", DateTimeOffset.MaxValue, "malformed");
        var page = await fixture.Store.ListSummaryPageAsync(new SessionSummaryQuery(DateTimeOffset.MinValue,
            ConversationIds: new HashSet<ConversationId>()));
        page.ShouldBe(SessionSummaryPage.Empty);
        await fixture.Files.DidNotReceive().ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        fixture.HistoryReads.ShouldBeEmpty();
        fixture.Store.MigrationInvocationCount.ShouldBe(0);
    }

    [Fact]
    public async Task FilePage_FiltersBeforeCountAndOffsetAndCountsOnlyReturnedHistory()
    {
        var fixture = new FileFixture();
        var eligible = ConversationId.From("allowed-conversation");
        fixture.Conversations.GetAsync(eligible, Arg.Any<CancellationToken>()).Returns(
            new Conversation { ConversationId = eligible, AgentId = AgentId.From("Mixed-Agent"), Title = "allowed" });
        fixture.Add("a", eligible.Value, DateTimeOffset.MinValue, $"{HistoryEntry}\n");
        fixture.Add("b", eligible.Value, DateTimeOffset.MinValue, $"{HistoryEntry}\n{HistoryEntry}\n");
        fixture.Add("sealed", eligible.Value, DateTimeOffset.MaxValue, "must not open", SessionStatus.Sealed);
        var page = await fixture.Store.ListSummaryPageAsync(new SessionSummaryQuery(DateTimeOffset.MinValue,
            Limit: 1, Offset: 1, ConversationIds: new HashSet<ConversationId> { eligible }));
        page.Items.ShouldHaveSingleItem().SessionId.ShouldBe("b");
        page.Items[0].MessageCount.ShouldBe(2);
        page.TotalCount.ShouldBe(2);
        page.HasMore.ShouldBeFalse();
        fixture.HistoryReads.ShouldBe([fixture.HistoryPath("b")]);
    }

    private sealed class FileFixture
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "4741-file-summary-boundary");
        private readonly MockFileSystem _mock = new();
        public IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();
        public IFile Files { get; } = Substitute.For<IFile>();
        public List<string> HistoryReads { get; } = [];
        public FileSessionStore Store { get; }

        public FileFixture()
        {
            Files.Exists(Arg.Any<string>()).Returns(call => _mock.File.Exists(call.Arg<string>()));
            Files.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
                _mock.File.ReadAllTextAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
            Files.ReadAllLinesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                HistoryReads.Add(call.Arg<string>());
                return Task.FromException<string[]>(new InvalidOperationException(
                    "Full transcript materialization is forbidden in summary search."));
            });
            var streams = Substitute.For<IFileStreamFactory>();
            streams.New(Arg.Any<string>(), Arg.Any<FileMode>(), Arg.Any<FileAccess>(), Arg.Any<FileShare>()).Returns(call =>
            {
                var path = call.Arg<string>();
                HistoryReads.Add(path);
                return _mock.FileStream.New(path, call.Arg<FileMode>(), call.Arg<FileAccess>(), call.Arg<FileShare>());
            });
            var fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(Files);
            fileSystem.Directory.Returns(_mock.Directory);
            fileSystem.FileStream.Returns(streams);
            Store = new FileSessionStore(_root, NullLogger<FileSessionStore>.Instance, fileSystem, Conversations);
        }

        public string HistoryPath(string id) => Path.Combine(_root, SessionFileNames.HistoryFileName(id));
        public void Add(string id, string? conversation, DateTimeOffset updated, string history, SessionStatus status = SessionStatus.Active)
        {
            _mock.AddFile(Path.Combine(_root, SessionFileNames.MetadataFileName(id)), new MockFileData(JsonSerializer.Serialize(new
            {
                conversationId = conversation, agentId = "legacy-agent", createdAt = DateTimeOffset.MinValue,
                updatedAt = updated, status,
                // A summary projection must not deserialize replay payloads.
                streamEvents = "not-a-replay-array"
            })));
            _mock.AddFile(HistoryPath(id), new MockFileData(history));
        }
    }
}
