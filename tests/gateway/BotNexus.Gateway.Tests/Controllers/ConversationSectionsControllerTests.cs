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

    private static ConversationSectionsController CreateController(out IConversationSectionStore store)
    {
        store = new InMemoryConversationSectionStore();
        return new ConversationSectionsController(store);
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
        var controller = CreateController(out var store);
        var section = await store.CreateSectionAsync(new ConversationSection
        {
            SectionId = SectionId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Name = "Temp"
        });
        var conv = ConversationId.Create();

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
        var controller = CreateController(out _);

        var result = await controller.Assign(AgentId, SectionId.Create().Value, ConversationId.Create().Value, CancellationToken.None);

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
        var controller = CreateController(out var store);
        var agent = BotNexus.Domain.Primitives.AgentId.From(AgentId);
        var section = await store.CreateSectionAsync(new ConversationSection { SectionId = SectionId.Create(), AgentId = agent, Name = "S" });
        var conv = ConversationId.Create();
        await store.AssignConversationAsync(section.SectionId, conv);

        var result = await controller.Unassign(AgentId, conv.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAssignmentsAsync(agent)).ShouldBeEmpty();
    }
}
