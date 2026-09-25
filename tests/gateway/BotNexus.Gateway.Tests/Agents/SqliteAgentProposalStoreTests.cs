using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents.Proposals;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Gateway.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SqliteAgentProposalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "botnexus-agent-proposal-tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "agent-proposals.sqlite");

    private static AgentDescriptor Descriptor(string id = "metrics-analyst") => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = "Metrics Analyst",
        Description = "Analyses platform telemetry",
        ModelId = "model-a",
        ApiProvider = "provider-a",
        ToolIds = ["memory_save"],
        ExtensionConfig = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["sample"] = System.Text.Json.JsonDocument.Parse("{\"enabled\":true}").RootElement.Clone(),
        },
    };

    [Fact]
    public async Task CreateAsync_Reopen_PreservesCompletePendingProposal()
    {
        Directory.CreateDirectory(_directory);
        var configMarker = Path.Combine(_directory, "config.json");
        await File.WriteAllTextAsync(configMarker, "{\"unchanged\":true}");
        var proposedAt = new DateTimeOffset(2026, 9, 25, 1, 2, 3, TimeSpan.Zero);
        var proposal = new AgentProposal(
            Guid.NewGuid(),
            AgentProposalKind.Create,
            AgentId.From("metrics-analyst"),
            Descriptor(),
            "A specialist is needed",
            CitizenId.Of(AgentId.From("farnsworth")),
            proposedAt,
            AgentProposalStatus.Pending,
            null,
            null,
            null,
            []);

        await using (var store = new SqliteAgentProposalStore(DatabasePath))
            await store.CreateAsync(proposal);

        await using var reopened = new SqliteAgentProposalStore(DatabasePath);
        var persisted = await reopened.GetAsync(proposal.ProposalId);

        persisted.ShouldNotBeNull();
        persisted.ProposalId.ShouldBe(proposal.ProposalId);
        persisted.Kind.ShouldBe(proposal.Kind);
        persisted.TargetAgentId.ShouldBe(proposal.TargetAgentId);
        JsonSerializer.Serialize(persisted.ProposedDescriptor).ShouldBe(JsonSerializer.Serialize(proposal.ProposedDescriptor));
        persisted.Justification.ShouldBe(proposal.Justification);
        persisted.ProposedBy.ShouldBe(proposal.ProposedBy);
        persisted.ProposedAt.ShouldBe(proposal.ProposedAt);
        persisted.Status.ShouldBe(AgentProposalStatus.Pending);
        persisted.ReviewHistory.ShouldBeEmpty();
        persisted.ProposedDescriptor.ExtensionConfig["sample"].GetProperty("enabled").GetBoolean().ShouldBeTrue();
        (await File.ReadAllTextAsync(configMarker)).ShouldBe("{\"unchanged\":true}");
    }

    [Fact]
    public async Task ReviewAsync_Reopen_PreservesFirstDecisionAndAppendOnlyHistory()
    {
        var proposal = PendingUpdate();
        var reviewedAt = new DateTimeOffset(2026, 9, 25, 4, 5, 6, TimeSpan.Zero);
        var reviewer = CitizenId.Of(UserId.From("admin"));

        await using (var store = new SqliteAgentProposalStore(DatabasePath))
        {
            await store.CreateAsync(proposal);
            var result = await store.ReviewAsync(
                proposal.ProposalId,
                AgentProposalStatus.Approved,
                reviewer,
                "Approved after review",
                reviewedAt);

            result.Outcome.ShouldBe(AgentProposalReviewOutcome.Applied);
        }

        await using var reopened = new SqliteAgentProposalStore(DatabasePath);
        var persisted = await reopened.GetAsync(proposal.ProposalId);

        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(AgentProposalStatus.Approved);
        persisted.ReviewedBy.ShouldBe(reviewer);
        persisted.ReviewReason.ShouldBe("Approved after review");
        persisted.ReviewedAt.ShouldBe(reviewedAt);
        var history = persisted.ReviewHistory.ShouldHaveSingleItem();
        history.Status.ShouldBe(AgentProposalStatus.Approved);
        history.Reviewer.ShouldBe(reviewer);
        history.Reason.ShouldBe("Approved after review");
        history.ReviewedAt.ShouldBe(reviewedAt);
    }

    [Fact]
    public async Task ReviewAsync_ConcurrentOpposingDecisions_FirstDecisionWins()
    {
        var proposal = PendingUpdate();
        await using var first = new SqliteAgentProposalStore(DatabasePath);
        await using var second = new SqliteAgentProposalStore(DatabasePath);
        await first.CreateAsync(proposal);
        (await second.GetAsync(proposal.ProposalId)).ShouldNotBeNull();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewerA = CitizenId.Of(UserId.From("reviewer-a"));
        var reviewerB = CitizenId.Of(UserId.From("reviewer-b"));
        var atA = new DateTimeOffset(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);
        var atB = atA.AddSeconds(1);

        async Task<AgentProposalReviewResult> ReviewAsync(
            SqliteAgentProposalStore store,
            AgentProposalStatus status,
            CitizenId reviewer,
            string reason,
            DateTimeOffset reviewedAt)
        {
            await start.Task;
            return await store.ReviewAsync(proposal.ProposalId, status, reviewer, reason, reviewedAt);
        }

        var approve = ReviewAsync(first, AgentProposalStatus.Approved, reviewerA, "approve", atA);
        var reject = ReviewAsync(second, AgentProposalStatus.Rejected, reviewerB, "reject", atB);
        start.SetResult();
        var results = await Task.WhenAll(approve, reject);

        results.Count(result => result.Outcome == AgentProposalReviewOutcome.Applied).ShouldBe(1);
        results.Count(result => result.Outcome == AgentProposalReviewOutcome.AlreadyReviewed).ShouldBe(1);

        await using var reopened = new SqliteAgentProposalStore(DatabasePath);
        var persisted = await reopened.GetAsync(proposal.ProposalId);
        persisted.ShouldNotBeNull();
        var winningResult = results.Single(result => result.Outcome == AgentProposalReviewOutcome.Applied);
        winningResult.Proposal.ShouldNotBeNull();
        persisted.Status.ShouldBe(winningResult.Proposal.Status);
        persisted.ReviewedBy.ShouldBe(winningResult.Proposal.ReviewedBy);
        persisted.ReviewReason.ShouldBe(winningResult.Proposal.ReviewReason);
        persisted.ReviewedAt.ShouldBe(winningResult.Proposal.ReviewedAt);
        persisted.ReviewHistory.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ReviewAsync_RepeatedDecision_CannotOverwriteFirstDecision()
    {
        var proposal = PendingUpdate();
        await using var store = new SqliteAgentProposalStore(DatabasePath);
        await store.CreateAsync(proposal);

        var first = await store.ReviewAsync(
            proposal.ProposalId,
            AgentProposalStatus.Rejected,
            CitizenId.Of(UserId.From("first-reviewer")),
            "first reason",
            new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero));
        var repeated = await store.ReviewAsync(
            proposal.ProposalId,
            AgentProposalStatus.Approved,
            CitizenId.Of(UserId.From("second-reviewer")),
            "replacement reason",
            new DateTimeOffset(2026, 9, 25, 6, 1, 0, TimeSpan.Zero));

        first.Outcome.ShouldBe(AgentProposalReviewOutcome.Applied);
        repeated.Outcome.ShouldBe(AgentProposalReviewOutcome.AlreadyReviewed);
        repeated.Proposal.ShouldNotBeNull();
        repeated.Proposal.Status.ShouldBe(AgentProposalStatus.Rejected);
        repeated.Proposal.ReviewedBy.ShouldBe(CitizenId.Of(UserId.From("first-reviewer")));
        repeated.Proposal.ReviewReason.ShouldBe("first reason");
        repeated.Proposal.ReviewHistory.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CreateAsync_UsesVersionedSchemaAndFilesystemAwareWal()
    {
        var proposal = PendingUpdate();
        await using (var store = new SqliteAgentProposalStore(DatabasePath))
            await store.CreateAsync(proposal);

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        await connection.OpenAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(await version.ExecuteScalarAsync()).ShouldBe(SqliteAgentProposalStore.CurrentSchemaVersion);

        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        (await journal.ExecuteScalarAsync()).ShouldBe("wal");

    }

    [Fact]
    public void StoreConstructor_HasNoRegistryOrConfigurationDependency()
    {
        var parameters = typeof(SqliteAgentProposalStore).GetConstructors().ShouldHaveSingleItem().GetParameters();

        parameters.Select(parameter => parameter.ParameterType.FullName).ShouldNotContain(
            "BotNexus.Gateway.Abstractions.Agents.IAgentRegistry");
        parameters.ShouldAllBe(parameter =>
            !parameter.ParameterType.Name.Contains("ConfigurationWriter", StringComparison.Ordinal)
            && !parameter.ParameterType.Name.Contains("ConfigStore", StringComparison.Ordinal));
    }

    [Fact]
    public void AddBotNexusGateway_RegistersProposalStoreInWritableDataDirectory()
    {
        var dataDirectory = Path.Combine(_directory, "data");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new BotNexus.Gateway.Configuration.BotNexusHome(
            new System.IO.Abstractions.FileSystem(),
            Path.Combine(_directory, "home"),
            dataDirectory));

        services.AddBotNexusGateway();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IAgentProposalStore>();
        var sqlite = store.ShouldBeOfType<SqliteAgentProposalStore>();
        Path.GetFullPath(sqlite.DatabasePath).ShouldBe(
            Path.GetFullPath(Path.Combine(dataDirectory, "agent-proposals.sqlite")));
    }

    private static AgentProposal PendingUpdate() => new(
        Guid.NewGuid(),
        AgentProposalKind.Update,
        AgentId.From("existing-agent"),
        Descriptor("existing-agent"),
        "Update the complete descriptor",
        CitizenId.Of(AgentId.From("farnsworth")),
        new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero),
        AgentProposalStatus.Pending,
        null,
        null,
        null,
        []);

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DatabasePath);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
