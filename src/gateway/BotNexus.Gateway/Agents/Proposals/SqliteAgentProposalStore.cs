using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents.Proposals;

/// <summary>
/// SQLite proposal ledger that owns no agent-lifecycle dependencies: creating or reviewing a row
/// cannot update platform configuration or an <c>IAgentRegistry</c>.
/// </summary>
public sealed class SqliteAgentProposalStore : IAgentProposalStore, IDisposable, IAsyncDisposable
{
    /// <summary>The schema version written and understood by this store.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly SqliteSchemaMigration[] Migrations = [];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly IFileSystem _fileSystem;
    private readonly SqliteWalMaintenance _walMaintenance;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    /// <summary>Path of the repository-owned proposal ledger, exposed for composition diagnostics.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Creates a ledger at <paramref name="databasePath"/>. The parent directory is created on first
    /// use and every operation obtains a busy-timeout-enabled connection from the shared factory.
    /// </summary>
    public SqliteAgentProposalStore(
        string databasePath,
        IFileSystem? fileSystem = null,
        INetworkPathDetector? networkPathDetector = null,
        ILogger<SqliteWalMaintenance>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        _fileSystem = fileSystem ?? new FileSystem();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _walMaintenance = networkPathDetector is null
            ? new SqliteWalMaintenance(_fileSystem, logger)
            : new SqliteWalMaintenance(networkPathDetector, logger);
    }

    /// <inheritdoc />
    public async Task CreateAsync(AgentProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateNewProposal(proposal);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_proposal (
                proposal_id, kind, target_agent_id, proposed_descriptor_json, justification,
                proposed_by_json, proposed_at, status, reviewed_by_json, review_reason, reviewed_at)
            VALUES (
                $proposalId, $kind, $targetAgentId, $descriptor, $justification,
                $proposedBy, $proposedAt, $status, NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$proposalId", proposal.ProposalId.ToString("D"));
        command.Parameters.AddWithValue("$kind", proposal.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$targetAgentId", proposal.TargetAgentId.Value);
        command.Parameters.AddWithValue("$descriptor", JsonSerializer.Serialize(proposal.ProposedDescriptor, JsonOptions));
        command.Parameters.AddWithValue("$justification", proposal.Justification);
        command.Parameters.AddWithValue("$proposedBy", JsonSerializer.Serialize(proposal.ProposedBy, JsonOptions));
        command.Parameters.AddWithValue("$proposedAt", FormatTimestamp(proposal.ProposedAt));
        command.Parameters.AddWithValue("$status", "pending");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentProposal?> GetAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        ValidateProposalId(proposalId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, proposalId, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentProposal>> ListAsync(
        AgentProposalStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = status is null
            ? "SELECT proposal_id FROM agent_proposal ORDER BY proposed_at DESC, proposal_id ASC;"
            : "SELECT proposal_id FROM agent_proposal WHERE status = $status ORDER BY proposed_at DESC, proposal_id ASC;";
        if (status is not null)
            command.Parameters.AddWithValue("$status", FormatStatus(status.Value));

        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(Guid.Parse(reader.GetString(0)));
        }

        var proposals = new List<AgentProposal>(ids.Count);
        foreach (var id in ids)
        {
            var proposal = await LoadAsync(connection, id, transaction: null, cancellationToken).ConfigureAwait(false);
            if (proposal is not null)
                proposals.Add(proposal);
        }

        return proposals;
    }

    /// <inheritdoc />
    public async Task<AgentProposalReviewResult> ReviewAsync(
        Guid proposalId,
        AgentProposalStatus decision,
        CitizenId reviewer,
        string? reason,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateProposalId(proposalId);
        if (decision is AgentProposalStatus.Pending)
            throw new ArgumentException("A review decision must be approved or rejected.", nameof(decision));
        if (!reviewer.IsValid)
            throw new ArgumentException("Reviewer must be a valid citizen identity.", nameof(reviewer));

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_proposal
            SET status = $status,
                reviewed_by_json = $reviewedBy,
                review_reason = $reason,
                reviewed_at = $reviewedAt
            WHERE proposal_id = $proposalId AND status = 'pending';
            """;
        update.Parameters.AddWithValue("$status", FormatStatus(decision));
        update.Parameters.AddWithValue("$reviewedBy", JsonSerializer.Serialize(reviewer, JsonOptions));
        update.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        update.Parameters.AddWithValue("$reviewedAt", FormatTimestamp(reviewedAt));
        update.Parameters.AddWithValue("$proposalId", proposalId.ToString("D"));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        AgentProposalReviewOutcome outcome;
        if (changed == 1)
        {
            await using var append = connection.CreateCommand();
            append.Transaction = transaction;
            append.CommandText = """
                INSERT INTO agent_proposal_review (
                    proposal_id, status, reviewer_json, reason, reviewed_at)
                VALUES ($proposalId, $status, $reviewer, $reason, $reviewedAt);
                """;
            append.Parameters.AddWithValue("$proposalId", proposalId.ToString("D"));
            append.Parameters.AddWithValue("$status", FormatStatus(decision));
            append.Parameters.AddWithValue("$reviewer", JsonSerializer.Serialize(reviewer, JsonOptions));
            append.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            append.Parameters.AddWithValue("$reviewedAt", FormatTimestamp(reviewedAt));
            await append.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            outcome = AgentProposalReviewOutcome.Applied;
        }
        else
        {
            outcome = await ExistsAsync(connection, transaction, proposalId, cancellationToken).ConfigureAwait(false)
                ? AgentProposalReviewOutcome.AlreadyReviewed
                : AgentProposalReviewOutcome.NotFound;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var authoritative = outcome is AgentProposalReviewOutcome.NotFound
            ? null
            : await LoadAsync(connection, proposalId, transaction: null, cancellationToken).ConfigureAwait(false);
        return new AgentProposalReviewResult(outcome, authoritative);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var directory = _fileSystem.Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                _fileSystem.Directory.CreateDirectory(directory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await _walMaintenance.ApplyJournalModeAsync(
                connection,
                DatabasePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_proposal (
                    proposal_id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK (kind IN ('create', 'update')),
                    target_agent_id TEXT NOT NULL,
                    proposed_descriptor_json TEXT NOT NULL,
                    justification TEXT NOT NULL,
                    proposed_by_json TEXT NOT NULL,
                    proposed_at TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'rejected')),
                    reviewed_by_json TEXT NULL,
                    review_reason TEXT NULL,
                    reviewed_at TEXT NULL,
                    CHECK (
                        (status = 'pending' AND reviewed_by_json IS NULL AND reviewed_at IS NULL)
                        OR
                        (status IN ('approved', 'rejected') AND reviewed_by_json IS NOT NULL AND reviewed_at IS NOT NULL)
                    )
                );
                CREATE TABLE IF NOT EXISTS agent_proposal_review (
                    review_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    proposal_id TEXT NOT NULL REFERENCES agent_proposal(proposal_id) ON DELETE CASCADE,
                    status TEXT NOT NULL CHECK (status IN ('approved', 'rejected')),
                    reviewer_json TEXT NOT NULL,
                    reason TEXT NULL,
                    reviewed_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_agent_proposal_status_proposed_at
                    ON agent_proposal(status, proposed_at DESC);
                CREATE INDEX IF NOT EXISTS ix_agent_proposal_review_proposal
                    ON agent_proposal_review(proposal_id, review_id);
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            SqliteSchemaMigrator.Apply(connection, CurrentSchemaVersion, Migrations);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM agent_proposal WHERE proposal_id = $proposalId;";
        command.Parameters.AddWithValue("$proposalId", proposalId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<AgentProposal?> LoadAsync(
        SqliteConnection connection,
        Guid proposalId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, target_agent_id, proposed_descriptor_json, justification,
                   proposed_by_json, proposed_at, status, reviewed_by_json, review_reason, reviewed_at
            FROM agent_proposal
            WHERE proposal_id = $proposalId;
            """;
        command.Parameters.AddWithValue("$proposalId", proposalId.ToString("D"));

        AgentProposalKind kind;
        AgentId targetAgentId;
        AgentDescriptor descriptor;
        string justification;
        CitizenId proposedBy;
        DateTimeOffset proposedAt;
        AgentProposalStatus status;
        CitizenId? reviewedBy;
        string? reason;
        DateTimeOffset? reviewedAt;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            kind = ParseKind(reader.GetString(0));
            targetAgentId = AgentId.From(reader.GetString(1));
            descriptor = DeserializeRequired<AgentDescriptor>(reader.GetString(2), "proposed descriptor");
            justification = reader.GetString(3);
            proposedBy = DeserializeRequired<CitizenId>(reader.GetString(4), "proposer");
            proposedAt = ParseTimestamp(reader.GetString(5));
            status = ParseStatus(reader.GetString(6));
            reviewedBy = reader.IsDBNull(7)
                ? null
                : DeserializeRequired<CitizenId>(reader.GetString(7), "reviewer");
            reason = reader.IsDBNull(8) ? null : reader.GetString(8);
            reviewedAt = reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9));
        }

        var history = await LoadHistoryAsync(connection, transaction, proposalId, cancellationToken).ConfigureAwait(false);
        return new AgentProposal(
            proposalId,
            kind,
            targetAgentId,
            descriptor,
            justification,
            proposedBy,
            proposedAt,
            status,
            reviewedBy,
            reason,
            reviewedAt,
            history);
    }

    private static async Task<IReadOnlyList<AgentProposalReview>> LoadHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, reviewer_json, reason, reviewed_at
            FROM agent_proposal_review
            WHERE proposal_id = $proposalId
            ORDER BY review_id ASC;
            """;
        command.Parameters.AddWithValue("$proposalId", proposalId.ToString("D"));

        var history = new List<AgentProposalReview>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(new AgentProposalReview(
                ParseStatus(reader.GetString(0)),
                DeserializeRequired<CitizenId>(reader.GetString(1), "reviewer"),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return history;
    }

    private static T DeserializeRequired<T>(string json, string field)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException($"Agent proposal {field} JSON deserialized to null.");

    private static void ValidateNewProposal(AgentProposal proposal)
    {
        ValidateProposalId(proposal.ProposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.Justification);
        ArgumentNullException.ThrowIfNull(proposal.ProposedDescriptor);
        if (!proposal.ProposedBy.IsValid)
            throw new ArgumentException("Proposer must be a valid citizen identity.", nameof(proposal));
        if (proposal.TargetAgentId != proposal.ProposedDescriptor.AgentId)
            throw new ArgumentException("Target agent must match the complete proposed descriptor AgentId.", nameof(proposal));
        if (proposal.Status is not AgentProposalStatus.Pending
            || proposal.ReviewedBy is not null
            || proposal.ReviewedAt is not null
            || proposal.ReviewReason is not null
            || proposal.ReviewHistory.Count != 0)
        {
            throw new ArgumentException("A new proposal must be pending and have no review state or history.", nameof(proposal));
        }
    }

    private static void ValidateProposalId(Guid proposalId)
    {
        if (proposalId == Guid.Empty)
            throw new ArgumentException("Proposal identifier cannot be empty.", nameof(proposalId));
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatStatus(AgentProposalStatus status) => status switch
    {
        AgentProposalStatus.Pending => "pending",
        AgentProposalStatus.Approved => "approved",
        AgentProposalStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown proposal status."),
    };

    private static AgentProposalStatus ParseStatus(string value) => value switch
    {
        "pending" => AgentProposalStatus.Pending,
        "approved" => AgentProposalStatus.Approved,
        "rejected" => AgentProposalStatus.Rejected,
        _ => throw new InvalidDataException($"Unknown agent proposal status '{value}'."),
    };

    private static AgentProposalKind ParseKind(string value) => value switch
    {
        "create" => AgentProposalKind.Create,
        "update" => AgentProposalKind.Update,
        _ => throw new InvalidDataException($"Unknown agent proposal kind '{value}'."),
    };

    private SqliteConnection CreateConnection()
        => SqliteConnectionFactory.CreateForStoreKind(_connectionString, "agent-proposals");

    /// <summary>Releases initialization coordination resources.</summary>
    public void Dispose() => _initializeLock.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
