using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>#4816: a session id must not bypass its parent conversation's client write fence.</summary>
public sealed class InspectableConversationSessionWriteTests
{
    [Fact]
    public async Task UserFacingLinkedSession_WithoutCallerStamp_RetainsCallerOwnershipFence()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_unstamped", ConversationVisibility.UserFacing));
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s_unstamped"), conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        await sessions.SaveAsync(session);
        var controller = InspectableConversationHttpWriteTests.Authenticate(new SessionsController(sessions, conversations: store));
        InspectableConversationHttpWriteTests.Status(await controller.Delete(session.SessionId.Value, CancellationToken.None)).ShouldBe(403);
        (await sessions.GetAsync(session.SessionId)).ShouldNotBeNull();
    }

    public static IEnumerable<object[]> SessionWrites()
    {
        foreach (var operation in new[] { "metadata", "delete", "suspend", "resume", "seal" })
        foreach (var visibility in Enum.GetValues<ConversationVisibility>())
            yield return [operation, visibility];
    }

    [Theory]
    [MemberData(nameof(SessionWrites))]
    public async Task SessionWrite_ParentConversationVisibility_RejectsBeforeMutationOrAllowsUserFacing(
        string operation, ConversationVisibility visibility)
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(InspectableConversationHttpWriteTests.CreateConversation("c_session", visibility));
        var sessions = new InMemorySessionStore();
        var sessionId = SessionId.From("s_child");
        var session = await sessions.GetOrCreateAsync(sessionId, conversation.AgentId);
        session.ConversationId = conversation.ConversationId;
        session.CallerId = "test-client";
        session.SessionType = SessionType.AgentSubAgent;
        session.Status = operation switch
        {
            "resume" => SessionStatus.Suspended,
            "seal" => SessionStatus.Expired,
            _ => SessionStatus.Active
        };
        session.Metadata["existing"] = "preserve";
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "preserve transcript" });
        await sessions.SaveAsync(session);
        var before = JsonSerializer.Serialize(await sessions.GetAsync(sessionId));
        var beforeConversation = InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId));
        var controller = InspectableConversationHttpWriteTests.Authenticate(new SessionsController(sessions, conversations: store));
        IActionResult result = operation switch
        {
            "metadata" => (await controller.PatchMetadata(sessionId.Value, JsonSerializer.SerializeToElement(new { existing = "changed" }), CancellationToken.None)).Result
                ?? throw new InvalidOperationException("Expected action result"),
            "delete" => await controller.Delete(sessionId.Value, CancellationToken.None),
            "suspend" => (await controller.Suspend(sessionId.Value, CancellationToken.None)).Result
                ?? throw new InvalidOperationException("Expected action result"),
            "resume" => (await controller.Resume(sessionId.Value, CancellationToken.None)).Result
                ?? throw new InvalidOperationException("Expected action result"),
            "seal" => await controller.Seal(sessionId.Value, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (visibility != ConversationVisibility.UserFacing)
        {
            InspectableConversationHttpWriteTests.Status(result).ShouldBe(visibility == ConversationVisibility.InternalHidden ? 404 : 403);
            JsonSerializer.Serialize(await sessions.GetAsync(sessionId)).ShouldBe(before);
            InspectableConversationHttpWriteTests.Snapshot(await store.GetAsync(conversation.ConversationId)).ShouldBe(beforeConversation);
        }
        else
        {
            InspectableConversationHttpWriteTests.Status(result).ShouldBeInRange(200, 299);
            JsonSerializer.Serialize(await sessions.GetAsync(sessionId)).ShouldNotBe(before);
        }
    }
}
