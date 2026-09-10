using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Memory;

/// <summary>
/// <c>GET /api/memory/shared</c> — who can reach each shared store (#3232).
///
/// <remarks>
/// The endpoint reports ACCESS, not contents. What a store holds is already searchable; what was
/// invisible everywhere outside config.json is the relationship — a store several agents write to
/// is a channel one agent can use to influence what the others believe, and an operator has to be
/// able to see that at a glance.
/// </remarks>
/// </summary>
public sealed class SharedMemoryEndpointTests
{
    private static MemoryController Build(
        IReadOnlyList<SharedMemoryStoreConfig>? stores,
        params string[] agentIds)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(
            [.. agentIds.Select(id => new AgentDescriptor
            {
                AgentId = AgentId.From(id),
                DisplayName = id,
                ModelId = "test-model",
                ApiProvider = "test"
            })]);

        ISharedMemoryStoreRegistry? shared = stores is null
            ? null
            : new SharedMemoryStoreRegistry(
                stores,
                Path.Combine(Path.GetTempPath(), "shared-endpoint"),
                new MockFileSystem());

        return new MemoryController(
            registry.Object,
            Mock.Of<IMemoryStoreFactory>(),
            NullLogger<MemoryController>.Instance,
            shared);
    }

    private static IReadOnlyList<SharedMemoryStoreDto> Read(IActionResult result) =>
        (IReadOnlyList<SharedMemoryStoreDto>)((OkObjectResult)result).Value!;

    [Fact]
    public void A_gateway_with_no_registry_answers_empty_rather_than_failing()
    {
        // Every other consumer resolves the registry optionally; this one must too, or a gateway
        // that has no shared memory returns 500 on a page that merely asked.
        var result = Build(stores: null, "gantry-manager").ListSharedStores();

        Read(result).ShouldBeEmpty();
    }

    [Fact]
    public void Configured_but_empty_is_also_just_empty()
    {
        Read(Build([], "gantry-manager").ListSharedStores()).ShouldBeEmpty();
    }

    [Fact]
    public void A_wildcard_is_resolved_against_the_actual_roster()
    {
        // "*" reads as harmless until you notice how many agents are on the roster. The resolved
        // count is the difference between "*" and "all 3 agents".
        var controller = Build(
            [new SharedMemoryStoreConfig { Name = "platform", Readers = ["*"], Writers = ["gantry-manager"] }],
            "gantry-manager", "harbor-relay", "dock-scheduler");

        var store = Read(controller.ListSharedStores()).ShouldHaveSingleItem();

        store.ReaderCount.ShouldBe(3);
        store.WriterCount.ShouldBe(1);
    }

    [Fact]
    public void The_configured_lists_are_returned_verbatim_alongside_the_counts()
    {
        // The count says how wide the grant is now; the list says what was written down. An
        // operator checking a grant needs both - a roster change silently moves the count.
        var controller = Build(
            [new SharedMemoryStoreConfig { Name = "platform", Readers = ["*"], Writers = ["gantry-manager"] }],
            "gantry-manager", "harbor-relay");

        var store = Read(controller.ListSharedStores()).ShouldHaveSingleItem();

        store.Readers.ShouldBe(["*"]);
        store.Writers.ShouldBe(["gantry-manager"]);
    }

    [Fact]
    public void Readers_and_writers_are_reported_separately()
    {
        // The failure this guards is a projection that fills both from one list, showing a
        // curated store as one every reader can also write.
        var controller = Build(
            [new SharedMemoryStoreConfig { Name = "platform", Readers = ["*"], Writers = [] }],
            "gantry-manager", "harbor-relay");

        var store = Read(controller.ListSharedStores()).ShouldHaveSingleItem();

        store.ReaderCount.ShouldBe(2);
        store.WriterCount.ShouldBe(0, "an empty writers list grants nobody, wildcard readers or not");
    }

    [Fact]
    public void Stores_come_back_in_a_stable_order()
    {
        var controller = Build(
            [
                new SharedMemoryStoreConfig { Name = "zulu" },
                new SharedMemoryStoreConfig { Name = "alpha" },
                new SharedMemoryStoreConfig { Name = "Mike" }
            ],
            "gantry-manager");

        Read(controller.ListSharedStores()).Select(s => s.Name).ShouldBe(["alpha", "Mike", "zulu"]);
    }

    [Fact]
    public void Retention_rides_along_because_it_changes_what_the_store_is()
    {
        var controller = Build(
            [new SharedMemoryStoreConfig { Name = "scratch", RetentionDays = 7 }],
            "gantry-manager");

        Read(controller.ListSharedStores()).ShouldHaveSingleItem().RetentionDays.ShouldBe(7);
    }
}
