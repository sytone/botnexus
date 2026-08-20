using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Seam.Tests.Sessions;

/// <summary>
/// Real on-disk SQLite database for session seam tests (issue #3327, clauses 1-2).
/// </summary>
/// <remarks>
/// <para>
/// The guarantees under test live in SQL and in the store's striped per-session lock: the
/// unconditional <c>INSERT … ON CONFLICT DO UPDATE</c> upsert plus full-history rewrite behind
/// <see cref="SqliteSessionStore.SaveAsync(GatewaySession, System.Threading.CancellationToken)"/>,
/// the lock-scoped fence re-read behind the fenced overload, the insert-only append, the
/// read-merge-write metadata patch, and the conditional status <c>UPDATE</c>. A mock or an
/// in-memory double would re-implement all of that and therefore could not regress it — which is
/// exactly how the original webhook conversation-pin regression escaped.
/// </para>
/// <para>
/// <c>Pooling=False</c> keeps every store instance on its own connection, so a "fresh store"
/// verification read genuinely goes to disk rather than being answered by the writer's own
/// <see cref="SqliteSessionStore"/> LRU cache. The conversation store is shared across instances
/// on purpose: P9-I (#674) hydrates <see cref="GatewaySession.AgentId"/> from
/// <see cref="Conversation.AgentId"/> on every load, so each store must resolve the same
/// conversation rows.
/// </para>
/// </remarks>
internal sealed class SessionSeamStoreFixture : IDisposable
{
    private readonly InMemoryConversationStore _conversations = new();

    public SessionSeamStoreFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"botnexus-session-seam-{Guid.NewGuid():N}.db");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    /// <summary>Creates a store with its OWN cache — call twice to get two independent actors.</summary>
    public SqliteSessionStore CreateStore()
        => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    /// <summary>
    /// Creates the conversation a session will be bound to. Must exist before the session is
    /// reloaded, otherwise AgentId hydration has nothing to resolve against.
    /// </summary>
    public async Task<ConversationId> CreateConversationAsync(AgentId agentId)
    {
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
        });
        return conversationId;
    }

    /// <summary>
    /// Seeds a persisted session bound to a fresh conversation and returns the writer store, the
    /// live session and the run fence captured exactly as the gateway captures it at run start.
    /// </summary>
    public async Task<SeededSession> SeedAsync(string sessionIdValue, string agentIdValue = "seam-agent")
    {
        var agentId = AgentId.From(agentIdValue);
        var conversationId = await CreateConversationAsync(agentId);

        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionIdValue), agentId);
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "seed-turn" });
        await store.SaveAsync(session);

        return new SeededSession(store, session, SessionWriteFence.Capture(session), conversationId);
    }

    public void Dispose()
    {
        // NOT ClearAllPools(): process-global, disposes sibling tests' live handles (#3324, #3392,
        // #3475). This fixture opens with Pooling=False, so the scoped clear is a cheap no-op that
        // still releases anything an ad-hoc connection pooled under the same string.
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        if (File.Exists(DatabasePath))
        {
            // SQLite file handles can linger briefly on Windows; cleanup is best effort and must
            // never turn into a test failure of its own.
            try { File.Delete(DatabasePath); }
            catch (IOException) { }
        }
    }
}

/// <summary>A seeded session plus the actor that wrote it and the fence captured at run start.</summary>
internal sealed record SeededSession(
    SqliteSessionStore Store,
    GatewaySession Session,
    SessionWriteFence Fence,
    ConversationId ConversationId);
