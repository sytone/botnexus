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


    [Fact]
    public async Task VersionOnePopulatedDatabase_MigratesWithoutLosingProposalOrReview()
    {
        Directory.CreateDirectory(_directory);
        var proposal = PendingUpdate();
        var descriptorJson = JsonSerializer.Serialize(proposal.ProposedDescriptor);
        var proposerJson = JsonSerializer.Serialize(proposal.ProposedBy);
        var reviewer = CitizenId.Of(UserId.From("migration-reviewer"));
        var reviewerJson = JsonSerializer.Serialize(reviewer);
        var reviewedAt = new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version = 1;
                CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO store_meta(key, value) VALUES ('schema_version', '1');
                CREATE TABLE agent_proposal (
                    proposal_id TEXT PRIMARY KEY, kind TEXT NOT NULL, target_agent_id TEXT NOT NULL,
                    proposed_descriptor_json TEXT NOT NULL, justification TEXT NOT NULL,
                    proposed_by_json TEXT NOT NULL, proposed_at TEXT NOT NULL, status TEXT NOT NULL,
                    reviewed_by_json TEXT NULL, review_reason TEXT NULL, reviewed_at TEXT NULL);
                CREATE TABLE agent_proposal_review (
                    review_id INTEGER PRIMARY KEY AUTOINCREMENT, proposal_id TEXT NOT NULL,
                    status TEXT NOT NULL, reviewer_json TEXT NOT NULL, reason TEXT NULL, reviewed_at TEXT NOT NULL);
                INSERT INTO agent_proposal VALUES ($id, 'update', $target, $descriptor, $justification,
                    $proposer, $proposedAt, 'approved', $reviewer, $reason, $reviewedAt);
                INSERT INTO agent_proposal_review (proposal_id, status, reviewer_json, reason, reviewed_at)
                    VALUES ($id, 'approved', $reviewer, $reason, $reviewedAt);
                """;
            command.Parameters.AddWithValue("$id", proposal.ProposalId.ToString("D"));
            command.Parameters.AddWithValue("$target", proposal.TargetAgentId.Value);
            command.Parameters.AddWithValue("$descriptor", descriptorJson);
            command.Parameters.AddWithValue("$justification", proposal.Justification);
            command.Parameters.AddWithValue("$proposer", proposerJson);
            command.Parameters.AddWithValue("$proposedAt", proposal.ProposedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$reviewer", reviewerJson);
            command.Parameters.AddWithValue("$reason", "migration evidence");
            command.Parameters.AddWithValue("$reviewedAt", reviewedAt.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteAgentProposalStore(DatabasePath);
        var migrated = await store.GetAsync(proposal.ProposalId);

        migrated.ShouldNotBeNull();
        migrated.ProposedDescriptor.DisplayName.ShouldBe(proposal.ProposedDescriptor.DisplayName);
        migrated.ProposedBy.ShouldBe(proposal.ProposedBy);
        migrated.ReviewedBy.ShouldBe(reviewer);
        migrated.ReviewReason.ShouldBe("migration evidence");
        migrated.ReviewHistory.ShouldHaveSingleItem().Reviewer.ShouldBe(reviewer);
        migrated.ApplicationStatus.ShouldBe(AgentProposalApplicationStatus.Pending);
        migrated.ApplicationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task ApplyingAttempt_RequiresEvidenceReconciliationAndNeverBlindlyReclaims()
    {
        var proposal = PendingUpdate();
        await using var store = new SqliteAgentProposalStore(DatabasePath);
        await store.CreateAsync(proposal);
        await store.ReviewAsync(proposal.ProposalId, AgentProposalStatus.Approved,
            CitizenId.Of(UserId.From("reviewer")), null, DateTimeOffset.UtcNow);
        (await store.TryBeginApplicationAsync(proposal.ProposalId)).Outcome.ShouldBe(AgentProposalApplicationClaimOutcome.Claimed);

        (await store.TryBeginApplicationAsync(proposal.ProposalId)).Outcome.ShouldBe(AgentProposalApplicationClaimOutcome.NotClaimed);
        var reconciled = await store.ReconcileApplicationAsync(
            proposal.ProposalId, AgentProposalReconciliationDecision.NotApplied,
            CitizenId.Of(UserId.From("operator")), "config and registry verified absent", DateTimeOffset.UtcNow);

        reconciled.Outcome.ShouldBe(AgentProposalReconciliationOutcome.Reconciled);
        reconciled.Proposal.ShouldNotBeNull();
        reconciled.Proposal.ApplicationStatus.ShouldBe(AgentProposalApplicationStatus.ReconciledNotApplied);
        var evidence = reconciled.Proposal.ApplicationError;
        evidence.ShouldNotBeNull();
        evidence.ShouldContain("config and registry verified absent");
        (await store.TryBeginApplicationAsync(proposal.ProposalId)).Outcome.ShouldBe(AgentProposalApplicationClaimOutcome.Claimed);
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
