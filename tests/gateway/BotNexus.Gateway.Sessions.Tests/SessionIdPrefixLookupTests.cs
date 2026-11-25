using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class SessionIdPrefixLookupTests
{
    [Fact]
    public async Task FileStore_MatchesCanonicalOwnerAndLiteralPrefix_WithoutReadingHistory()
    {
        var files = new MockFileSystem();
        var path = Path.Combine(Path.GetTempPath(), "session-prefix");
        var conversations = new InMemoryConversationStore();
        var agent = AgentId.From("agent-a");
        var otherAgent = AgentId.From("agent-b");
        var owned = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = agent, Title = "owned"
        });
        var other = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = otherAgent, Title = "other"
        });
        var writer = new FileSessionStore(path, NullLogger<FileSessionStore>.Instance, files, conversations);
        await SeedAsync(writer, "cron:job%_:owned", agent, owned.ConversationId);
        await SeedAsync(writer, "cron:job%_:other", otherAgent, other.ConversationId);
        await SeedAsync(writer, "cron:jobXX:not-literal", agent, owned.ConversationId);
        await SeedAsync(writer, "CRON:job%_:wrong-case", agent, owned.ConversationId);
        files.File.WriteAllText(
            files.Path.Combine(path, SessionFileNames.HistoryFileName("cron:job%_:owned")), "not valid json");
        ISessionStore reader = new FileSessionStore(path, NullLogger<FileSessionStore>.Instance, files, conversations);

        var ids = await reader.ListIdsByPrefixAsync(agent, "cron:job%_:");

        ids.Select(id => id.Value).ShouldBe(["cron:job%_:owned"]);
    }

    [Fact]
    public async Task InMemoryStore_UsesConversationOwnerRatherThanStaleSessionAgent()
    {
        var conversations = new InMemoryConversationStore();
        var agent = AgentId.From("agent-a");
        var otherAgent = AgentId.From("agent-b");
        var conversation = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = otherAgent, Title = "other"
        });
        ISessionStore store = new InMemorySessionStore(null, conversations);
        await SeedAsync(store, "cron:job:stale", agent, conversation.ConversationId);

        (await store.ListIdsByPrefixAsync(agent, "cron:job:")).ShouldBeEmpty();
        (await store.ListIdsByPrefixAsync(otherAgent, "cron:job:"))
            .Select(id => id.Value).ShouldBe(["cron:job:stale"]);
    }

    private static async Task SeedAsync(ISessionStore store, string id, AgentId agent, ConversationId conversation)
    {
        var session = await store.GetOrCreateAsync(SessionId.From(id), agent);
        session.ConversationId = conversation;
        await store.SaveAsync(session);
    }
}
