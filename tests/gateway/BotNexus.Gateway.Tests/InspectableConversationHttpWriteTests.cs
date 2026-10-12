using System.Security.Claims;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>#4816: authenticated client writes must use stored visibility, not provenance or UI state.</summary>
public sealed class InspectableConversationHttpWriteTests
{
    public static IEnumerable<object[]> Mutations()
    {
        string[] operations = ["title", "purpose", "instructions", "add-binding", "remove-binding",
            "move-source", "move-target", "archive", "reset", "set-override", "clear-override",
            "pin", "unpin", "canvas-html", "canvas-set", "canvas-delete"];
        foreach (var operation in operations)
        foreach (var visibility in Enum.GetValues<ConversationVisibility>())
            yield return [operation, visibility];
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task Mutation_StoredVisibility_RejectsBeforeChangingStateOrAllowsUserFacing(
        string operation, ConversationVisibility visibility)
    {
        var store = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var source = await store.CreateAsync(CreateConversation("c_source", visibility));
        var target = await store.CreateAsync(CreateConversation("c_target", ConversationVisibility.UserFacing));
        // A move INTO a read-only target must be rejected even when its source is writable.
        if (operation == "move-target")
        {
            source = await store.CreateAsync(CreateConversation("c_writable_source", ConversationVisibility.UserFacing));
            target = await store.CreateAsync(CreateConversation("c_guarded_target", visibility));
        }
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("b_existing"),
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("existing-address")
        };
        (await store.AddBindingAsync(source.ConversationId, binding)).ShouldBeTrue();
        (await store.SetCanvasStateKeyAsync(source.ConversationId, "key", JsonSerializer.SerializeToElement("original"))).ShouldBeTrue();
        var reset = new RecordingResetService();
        var controller = Authenticate(new ConversationsController(store, sessions, resetService: reset));
        var canvas = Authenticate(new ConversationCanvasController(store));
        var beforeSource = Snapshot(await store.GetAsync(source.ConversationId));
        var beforeTarget = Snapshot(await store.GetAsync(target.ConversationId));
        var beforeCanvasState = JsonSerializer.Serialize(await store.GetCanvasStateAsync(source.ConversationId));
        var id = source.ConversationId.Value;
        var ct = CancellationToken.None;

        ActionResult result = operation switch
        {
            "title" => await controller.Patch(id, new(Title: "changed"), ct),
            "purpose" => await controller.Patch(id, new(Purpose: "changed"), ct),
            "instructions" => await controller.Patch(id, new(Instructions: "changed"), ct),
            "add-binding" => await controller.AddBinding(id, new("signalr", "new-address", "Interactive", "Single", null), ct),
            "remove-binding" => await controller.RemoveBinding(id, binding.BindingId.Value, ct),
            "move-source" or "move-target" => await controller.MoveBinding(id, binding.BindingId.Value, new(target.ConversationId.Value), ct),
            "archive" => await controller.Archive(id, ct),
            "reset" => await controller.Reset(id, ct),
            "set-override" => await controller.SetOverride(id, new(Model: "changed-model", Thinking: "high", ContextWindow: 4096,
                ToolOverrideJson: "{\"disabledTools\":[\"exec\"]}", ApplyToolOverride: true), ct),
            "clear-override" => await controller.ClearOverride(id, ct),
            "pin" => await controller.Pin(id, ct),
            "unpin" => await controller.Unpin(id, ct),
            "canvas-html" => await canvas.PutCanvas("agent-a", id, "changed", ct),
            "canvas-set" => await canvas.SetCanvasStateKey(id, "key", JsonSerializer.SerializeToElement("changed"), ct),
            "canvas-delete" => await canvas.DeleteCanvasStateKey(id, "key", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        if (visibility != ConversationVisibility.UserFacing)
        {
            // Hidden rows must not become discoverable through writes. Inspectable rows remain visible,
            // so a forbidden write is 403 rather than disguising the visible row as missing.
            Status(result).ShouldBe(visibility == ConversationVisibility.InternalHidden ? 404 : 403);
            Snapshot(await store.GetAsync(source.ConversationId)).ShouldBe(beforeSource);
            Snapshot(await store.GetAsync(target.ConversationId)).ShouldBe(beforeTarget);
            reset.Calls.ShouldBe(0);
            JsonSerializer.Serialize(await store.GetCanvasStateAsync(source.ConversationId)).ShouldBe(beforeCanvasState);
            (await sessions.ListAsync()).ShouldBeEmpty();
        }
        else
        {
            Status(result).ShouldBeInRange(200, 299);
            if (operation == "reset") reset.Calls.ShouldBe(1);
            else if (operation is "canvas-set" or "canvas-delete")
                JsonSerializer.Serialize(await store.GetCanvasStateAsync(source.ConversationId)).ShouldNotBe(beforeCanvasState);
            else Snapshot(await store.GetAsync(source.ConversationId)).ShouldNotBe(beforeSource);
        }
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("canvas")]
    [InlineData("override")]
    [InlineData("archive")]
    [InlineData("reset")]
    public async Task Mutation_CallerScopedToDifferentAgent_DoesNotRevealOrMutateConversation(string operation)
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("c_scope", ConversationVisibility.UserFacing));
        var before = Snapshot(conversation);
        var reset = new RecordingResetService();
        var controller = Authenticate(new ConversationsController(store, new InMemorySessionStore(), resetService: reset), "agent-b");
        var canvas = Authenticate(new ConversationCanvasController(store), "agent-b");
        var id = conversation.ConversationId.Value;
        ActionResult result = operation switch
        {
            "patch" => await controller.Patch(id, new(Title: "changed"), CancellationToken.None),
            "canvas" => await canvas.PutCanvas("agent-b", id, "changed", CancellationToken.None),
            "override" => await controller.ClearOverride(id, CancellationToken.None),
            "archive" => await controller.Archive(id, CancellationToken.None),
            "reset" => await controller.Reset(id, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Status(result).ShouldBe(404);
        Snapshot(await store.GetAsync(conversation.ConversationId)).ShouldBe(before);
        reset.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Get_InspectableConversation_RemainsVisible()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("c_visible", ConversationVisibility.InspectableReadOnly));
        var controller = Authenticate(new ConversationsController(store, new InMemorySessionStore()));
        var response = (await controller.Get(conversation.ConversationId.Value, CancellationToken.None))
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ConversationResponse>();
        response.Visibility.ShouldBe(nameof(ConversationVisibility.InspectableReadOnly));
    }

    [Fact]
    public async Task TrustedStoreWrite_InspectableConversation_RemainsPermitted()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync(CreateConversation("c_internal", ConversationVisibility.InspectableReadOnly));
        var updated = await store.PatchMetadataAsync(conversation.ConversationId,
            new ConversationMetadataPatch { Title = FieldUpdate<string>.Set("runtime update") });
        updated.ShouldNotBeNull().Title.ShouldBe("runtime update");
        updated.Visibility.ShouldBe(ConversationVisibility.InspectableReadOnly);
    }

    internal static Conversation CreateConversation(string id, ConversationVisibility visibility) => new()
    {
        ConversationId = ConversationId.From(id), AgentId = AgentId.From("agent-a"),
        // Deliberately ordinary Channel/HumanAgent provenance: visibility alone must close writes.
        Visibility = visibility, Title = "original", Purpose = "original purpose", Instructions = "original instructions",
        ModelOverride = "original-model", ThinkingOverride = "low", ContextWindowOverride = 2048,
        CanvasHtml = "original", IsPinned = true
    };

    internal static string Snapshot(Conversation? conversation) => JsonSerializer.Serialize(conversation);

    internal static T Authenticate<T>(T controller, string allowedAgent = "agent-a") where T : ControllerBase
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-client")], "TestAuth"))
        };
        context.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity
        {
            CallerId = "test-client", AllowedAgents = [allowedAgent]
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    internal static int Status(IActionResult result) => result switch
    {
        ObjectResult value => value.StatusCode ?? 200,
        StatusCodeResult value => value.StatusCode,
        _ => throw new InvalidOperationException($"Unexpected result: {result.GetType().Name}")
    };

    private sealed class RecordingResetService : IConversationResetService
    {
        public int Calls { get; private set; }
        public Task<ConversationResetResult> ResetActiveSessionAsync(ConversationId conversationId,
            SessionId? expectedActiveSessionId = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ConversationResetResult(ConversationResetOutcome.NoActiveSession, null, null));
        }
    }
}
