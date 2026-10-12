using System.IO.Abstractions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class AgentRunStoreParity4796Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrelationAndIncompleteFlag_RoundTripWithoutInventingMeasurementCapability(bool file)
    {
        var directory = Path.Combine(Path.GetTempPath(), nameof(AgentRunStoreParity4796Tests), Guid.NewGuid().ToString("N"));
        var conversations = new InMemoryConversationStore();
        ISessionStore Create() => file
            ? new FileSessionStore(directory, NullLogger<FileSessionStore>.Instance, new FileSystem(), conversations)
            : new InMemorySessionStore(null, conversations);
        try
        {
            var store = Create();
            var id = SessionId.From("parity");
            var run = AgentRunId.From("parity-run");
            var session = await store.GetOrCreateAsync(id, AgentId.From("agent"));
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolCallId = "reused", ToolName = "probe",
                Content = "incomplete", AgentRunId = run, ToolIsIncomplete = true
            });
            await store.SaveAsync(session);
            var reopened = file ? Create() : store;
            var row = (await reopened.GetAsync(id)).ShouldNotBeNull().GetHistorySnapshot().ShouldHaveSingleItem();
            row.AgentRunId.ShouldBe(run);
            row.ToolIsIncomplete.ShouldBeTrue();
            (reopened is IAgentRunEvidenceStore).ShouldBeFalse("unsupported stores must not invent durable measurement");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
