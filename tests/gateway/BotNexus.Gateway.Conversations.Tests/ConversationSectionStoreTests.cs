using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Behaviour tests for the user-defined conversation section stores (issue #2124), run against both
/// the SQLite and in-memory implementations so the two stay in parity. Covers the full acceptance
/// surface: create / rename / reorder / collapse / delete sections, assign / remove conversations,
/// the at-most-one-section invariant, and delete returning conversations to their system section
/// without deleting them.
/// </summary>
public sealed class ConversationSectionStoreTests
{
    private static AgentId Agent(string id) => AgentId.From(id);

    /// <summary>Creates each store implementation under test, paired with a disposer.</summary>
    public static IEnumerable<object[]> Stores()
    {
        yield return ["memory"];
        yield return ["sqlite"];
    }

    private static (IConversationSectionStore Store, IDisposable? Disposable) Create(string kind)
    {
        if (kind == "memory")
            return (new InMemoryConversationSectionStore(), null);

        var path = Path.Combine(Path.GetTempPath(), $"botnexus-sections-{Guid.NewGuid():N}.db");
        var store = new SqliteConversationSectionStore(
            $"Data Source={path};Pooling=False",
            NullLogger<SqliteConversationSectionStore>.Instance);
        return (store, new FileDeleter(path));
    }

    private sealed class FileDeleter(string path) : IDisposable
    {
        public void Dispose()
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task CreateSection_Assigns_Sequential_Order_And_Persists(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var first = await store.CreateSectionAsync(NewSection(agent, "Work"));
            var second = await store.CreateSectionAsync(NewSection(agent, "Personal"));

            first.Order.ShouldBe(0);
            second.Order.ShouldBe(1);

            var listed = await store.ListSectionsAsync(agent);
            listed.Count.ShouldBe(2);
            listed[0].Name.ShouldBe("Work");
            listed[1].Name.ShouldBe("Personal");
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Sections_Are_Scoped_Per_Agent(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            await store.CreateSectionAsync(NewSection(Agent("agent-a"), "A-only"));
            await store.CreateSectionAsync(NewSection(Agent("agent-b"), "B-only"));

            var forA = await store.ListSectionsAsync(Agent("agent-a"));
            forA.Count.ShouldBe(1);
            forA[0].Name.ShouldBe("A-only");
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UpdateSection_Renames_And_Collapses(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var section = await store.CreateSectionAsync(NewSection(Agent("agent-a"), "Old"));

            var updated = await store.UpdateSectionAsync(section.SectionId, "New name", isCollapsed: true);
            updated.ShouldNotBeNull();
            updated!.Name.ShouldBe("New name");
            updated.IsCollapsed.ShouldBeTrue();

            // Partial update leaves the other field untouched.
            var collapsedOnly = await store.UpdateSectionAsync(section.SectionId, name: null, isCollapsed: false);
            collapsedOnly!.Name.ShouldBe("New name");
            collapsedOnly.IsCollapsed.ShouldBeFalse();
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UpdateSection_Nonexistent_Returns_Null(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var result = await store.UpdateSectionAsync(SectionId.Create(), "x", null);
            result.ShouldBeNull();
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reorder_Reassigns_Order_And_Appends_Omitted(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var a = await store.CreateSectionAsync(NewSection(agent, "A"));
            var b = await store.CreateSectionAsync(NewSection(agent, "B"));
            var c = await store.CreateSectionAsync(NewSection(agent, "C"));

            // Supply only B then A; C omitted must land after them, preserving its relative order.
            await store.ReorderSectionsAsync(agent, [b.SectionId, a.SectionId]);

            var listed = await store.ListSectionsAsync(agent);
            listed.Select(s => s.Name).ShouldBe(["B", "A", "C"]);
            listed[0].Order.ShouldBe(0);
            listed[1].Order.ShouldBe(1);
            listed[2].Order.ShouldBe(2);
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Assign_Enforces_At_Most_One_Section(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var s1 = await store.CreateSectionAsync(NewSection(agent, "S1"));
            var s2 = await store.CreateSectionAsync(NewSection(agent, "S2"));
            var conv = ConversationId.Create();

            await store.AssignConversationAsync(s1.SectionId, conv);
            await store.AssignConversationAsync(s2.SectionId, conv); // reassign

            var assignments = await store.GetAssignmentsAsync(agent);
            assignments.Count.ShouldBe(1);
            assignments[conv.Value].ShouldBe(s2.SectionId.Value);
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Assign_To_Missing_Section_Throws(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(
                async () => await store.AssignConversationAsync(SectionId.Create(), ConversationId.Create()));
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RemoveConversation_Clears_Assignment(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var section = await store.CreateSectionAsync(NewSection(agent, "S"));
            var conv = ConversationId.Create();
            await store.AssignConversationAsync(section.SectionId, conv);

            await store.RemoveConversationAsync(conv);

            (await store.GetAssignmentsAsync(agent)).ShouldBeEmpty();
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task DeleteSection_Returns_Conversations_To_System_Section(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var section = await store.CreateSectionAsync(NewSection(agent, "Temp"));
            var conv = ConversationId.Create();
            await store.AssignConversationAsync(section.SectionId, conv);

            await store.DeleteSectionAsync(section.SectionId);

            // Section gone, and its assignment dropped (conversation returns to its system section).
            (await store.GetSectionAsync(section.SectionId)).ShouldBeNull();
            (await store.GetAssignmentsAsync(agent)).ShouldBeEmpty();
            (await store.ListSectionsAsync(agent)).ShouldBeEmpty();
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task DeleteSection_Nonexistent_Noops(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            await store.DeleteSectionAsync(SectionId.Create()); // no throw
        }
        finally { disposable?.Dispose(); }
    }

    private static ConversationSection NewSection(AgentId agent, string name) => new()
    {
        SectionId = SectionId.Create(),
        AgentId = agent,
        Name = name
    };
}
