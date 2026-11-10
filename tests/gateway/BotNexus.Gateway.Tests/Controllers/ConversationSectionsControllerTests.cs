using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Tests for <see cref="ConversationSectionsController"/> - the REST surface for user-defined
/// conversation sections (issue #2124). Exercises happy and sad paths for create/rename/reorder/
/// collapse/delete and assign/remove, all backed by the in-memory section store.
/// </summary>
public sealed class ConversationSectionsControllerTests
{
    private const string AgentId = "agent-sec";

    private static ConversationSectionsController CreateController(out IConversationSectionStore store, IConversationStore? conversations = null)
    {
        store = new InMemoryConversationSectionStore();
        return new ConversationSectionsController(store, conversations ?? new InMemoryConversationStore());
    }

    [Fact]
    public async Task Create_Returns_Created_With_Section()
    {
        var controller = CreateController(out _);

        var result = await controller.Create(AgentId, new CreateSectionRequest("Work"), CancellationToken.None);

        var created = result.ShouldBeOfType<CreatedAtActionResult>();
        var dto = created.Value.ShouldBeOfType<SectionDto>();
        dto.Name.ShouldBe("Work");
        dto.Order.ShouldBe(0);
    }

    [Fact]
    public async Task Create_Blank_Name_Returns_BadRequest()
    {
        var controller = CreateController(out _);

        var result = await controller.Create(AgentId, new CreateSectionRequest("   "), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_Returns_Sections_And_Assignments()
    {
        var controller = CreateController(out var store);
        var section = await store.CreateSectionAsync(new ConversationSection
        {
            SectionId = SectionId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Name = "S"
        });
        var conv = ConversationId.Create();
        await store.AssignConversationAsync(section.SectionId, conv);

        var result = await controller.List(AgentId, CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value.ShouldBeOfType<SectionListResponse>();
        payload.Sections.Count.ShouldBe(1);
        payload.Assignments[conv.Value].ShouldBe(section.SectionId.Value);
    }

    [Fact]
    public async Task Update_Renames_Section()
    {
        var controller = CreateController(out var store);
        var section = await store.CreateSectionAsync(new ConversationSection
        {
            SectionId = SectionId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Name = "Old"
        });

        var result = await controller.Update(AgentId, section.SectionId.Value, new UpdateSectionRequest(Name: "New"), CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<SectionDto>().Name.ShouldBe("New");
    }

    [Fact]
    public async Task Update_Empty_Request_Returns_BadRequest()
    {
        var controller = CreateController(out _);

        var result = await controller.Update(AgentId, "sec_x", new UpdateSectionRequest(), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Nonexistent_Returns_NotFound()
    {
        var controller = CreateController(out _);

        var result = await controller.Update(AgentId, SectionId.Create().Value, new UpdateSectionRequest(Name: "x"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Assign_Then_Delete_Returns_Conversation_To_System_Section()
    {
        var conv = ConversationId.Create();
        var conversations = await SignalRHubTests.CreateUserFacingStoreAsync(conv.Value, AgentId);
        var controller = CreateController(out var store, conversations);
        var section = await store.CreateSectionAsync(new ConversationSection
        {
            SectionId = SectionId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Name = "Temp"
        });

        (await controller.Assign(AgentId, section.SectionId.Value, conv.Value, CancellationToken.None))
            .ShouldBeOfType<NoContentResult>();

        (await controller.Delete(AgentId, section.SectionId.Value, CancellationToken.None))
            .ShouldBeOfType<NoContentResult>();

        var assignments = await store.GetAssignmentsAsync(BotNexus.Domain.Primitives.AgentId.From(AgentId));
        assignments.ShouldBeEmpty();
    }

    [Fact]
    public async Task Assign_To_Missing_Section_Returns_NotFound()
    {
        var conv = ConversationId.Create();
        var conversations = await SignalRHubTests.CreateUserFacingStoreAsync(conv.Value, AgentId);
        var controller = CreateController(out _, conversations);

        var result = await controller.Assign(AgentId, SectionId.Create().Value, conv.Value, CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Reorder_Returns_NoContent()
    {
        var controller = CreateController(out var store);
        var agent = BotNexus.Domain.Primitives.AgentId.From(AgentId);
        var a = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = agent, Name = "A" });
        var b = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = agent, Name = "B" });

        var result = await controller.Reorder(AgentId, new ReorderSectionsRequest([b.SectionId.Value, a.SectionId.Value]), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        var listed = await store.ListSectionsAsync(agent);
        listed[0].Name.ShouldBe("B");
    }

    [Fact]
    public async Task Unassign_Returns_NoContent()
    {
        var conv = ConversationId.Create();
        var conversations = await SignalRHubTests.CreateUserFacingStoreAsync(conv.Value, AgentId);
        var controller = CreateController(out var store, conversations);
        var agent = BotNexus.Domain.Primitives.AgentId.From(AgentId);
        var section = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = agent, Name = "S" });
        await store.AssignConversationAsync(section.SectionId, conv);

        var result = await controller.Unassign(AgentId, conv.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAssignmentsAsync(agent)).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_Adds_GroupOrder_Without_Changing_Legacy_Response_Constructor()
    {
        var controller = CreateController(out _);
        var payload = (await controller.List(AgentId, CancellationToken.None)).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<SectionListResponse>();
        var order = payload.GroupOrder;
        order.ShouldNotBeNull();
        order.Select(e => e.System).ShouldBe(["Pinned", "Conversations", "Scheduled", "Webhooks"]);
        payload.Sections.ShouldBeEmpty();
        new SectionListResponse([], new Dictionary<string, string>()).GroupOrder.ShouldBeNull();
    }

    [Fact]
    public async Task GroupOrder_Returns_Normalized_Order_And_Preserves_Omitted_Groups()
    {
        var controller = CreateController(out var store);
        var agent = BotNexus.Domain.Primitives.AgentId.From(AgentId);
        var section = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = agent, Name = "Pinned" });
        var result = await controller.ReorderGroups(AgentId,
            new ReorderSectionGroupsRequest([new SectionGroupOrderDto(SectionId: section.SectionId.Value), new SectionGroupOrderDto(System: "Webhooks")]), CancellationToken.None);
        var order = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<SectionGroupOrderDto>>();
        order.ShouldNotBeNull();
        order.Select(e => e.System ?? e.SectionId).ShouldBe([section.SectionId.Value, "Webhooks", "Pinned", "Conversations", "Scheduled"]);
        (await store.ListSectionsAsync(agent)).ShouldHaveSingleItem().Name.ShouldBe("Pinned");
    }

    [Fact]
    public async Task GroupOrder_Rejects_Invalid_Union_Enum_Duplicate_Unknown_And_Foreign_Without_Mutation()
    {
        var controller = CreateController(out var store);
        var agent = BotNexus.Domain.Primitives.AgentId.From(AgentId);
        var foreign = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = BotNexus.Domain.Primitives.AgentId.From("foreign"), Name = "Foreign" });
        var prior = await store.GetGroupOrderAsync(agent);
        IReadOnlyList<SectionGroupOrderDto?>?[] invalid = [null,
            [null], [new()], [new(System: "Pinned", SectionId: "sec_a")], [new(System: "Unknown")],
            [new(System: "0")], [new(SectionId: " ")], [new(SectionId: "sec_missing")],
            [new(SectionId: foreign.SectionId.Value)], [new(System: "Pinned"), new(System: "Pinned")]];
        foreach (var entries in invalid)
        {
            (await controller.ReorderGroups(AgentId, new ReorderSectionGroupsRequest(entries), CancellationToken.None))
                .ShouldBeOfType<BadRequestObjectResult>();
            (await store.GetGroupOrderAsync(agent)).ShouldBe(prior);
        }
    }

    [Fact]
    public async Task Wrong_Route_Agent_Cannot_Update_Delete_Or_Assign_Foreign_Section()
    {
        var conversation = ConversationId.Create();
        var conversations = await SignalRHubTests.CreateUserFacingStoreAsync(conversation.Value, AgentId);
        var controller = CreateController(out var store, conversations);
        var foreignAgent = BotNexus.Domain.Primitives.AgentId.From("foreign");
        var foreign = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = foreignAgent, Name = "Kept" });
        (await controller.Update(AgentId, foreign.SectionId.Value, new UpdateSectionRequest("Changed", true), CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await controller.Delete(AgentId, foreign.SectionId.Value, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await controller.Assign(AgentId, foreign.SectionId.Value, conversation.Value, CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
        var after = await store.GetSectionAsync(foreign.SectionId);
        after.ShouldNotBeNull();
        after.Name.ShouldBe("Kept");
        after.IsCollapsed.ShouldBeFalse();
        (await store.GetAssignmentsAsync(foreignAgent)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Pinned")]
    [InlineData("Conversations")]
    [InlineData("Scheduled")]
    [InlineData("Webhooks")]
    public async Task BuiltIns_Are_Not_Custom_Mutation_Targets(string id)
    {
        var conversation = ConversationId.Create();
        var conversations = await SignalRHubTests.CreateUserFacingStoreAsync(conversation.Value, AgentId);
        var controller = CreateController(out _, conversations);
        (await controller.Update(AgentId, id, new UpdateSectionRequest("Changed"), CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await controller.Delete(AgentId, id, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await controller.Assign(AgentId, id, conversation.Value, CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
    }

    [Theory]
    [InlineData(ConversationVisibility.InspectableReadOnly, 403)]
    [InlineData(ConversationVisibility.InternalHidden, 404)]
    public async Task Assignment_Retains_Stored_Visibility_Guard(ConversationVisibility visibility, int status)
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Visibility = visibility
        });
        var controller = CreateController(out var store, conversations);
        var section = await store.CreateSectionAsync(new ConversationSection
        {
            SectionId = SectionId.Create(), AgentId = conversation.AgentId, Name = "Kept"
        });
        await store.AssignConversationAsync(section.SectionId, conversation.ConversationId);
        var before = await store.GetAssignmentsAsync(conversation.AgentId);
        foreach (var result in new[] {
            await controller.Assign(AgentId, section.SectionId.Value, conversation.ConversationId.Value, CancellationToken.None),
            await controller.Unassign(AgentId, conversation.ConversationId.Value, CancellationToken.None) })
        {
            var actualStatus = result is StatusCodeResult code ? code.StatusCode : result.ShouldBeOfType<ObjectResult>().StatusCode;
            actualStatus.ShouldBe(status);
        }
        (await store.GetAssignmentsAsync(conversation.AgentId)).ShouldBe(before);
    }

    [Fact]
    public async Task Assign_Retains_Conversation_Route_Ownership_Guard()
    {
        var conversation = ConversationId.Create();
        var conversations = await SignalRHubTests.CreateUserFacingStoreAsync(conversation.Value, "different-agent");
        var controller = CreateController(out var store, conversations);
        var agent = BotNexus.Domain.Primitives.AgentId.From(AgentId);
        var section = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = agent, Name = "Kept" });
        (await controller.Assign(AgentId, section.SectionId.Value, conversation.Value, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await store.GetAssignmentsAsync(agent)).ShouldBeEmpty();
    }

    [Fact]
    public void GroupOrder_Wire_Entries_Contain_Only_Their_Identity()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        System.Text.Json.JsonSerializer.Serialize(new SectionGroupOrderDto(System: "Pinned"), options).ShouldBe("{\"system\":\"Pinned\"}");
        System.Text.Json.JsonSerializer.Serialize(new SectionGroupOrderDto(SectionId: "sec_a"), options).ShouldBe("{\"sectionId\":\"sec_a\"}");
    }
}
