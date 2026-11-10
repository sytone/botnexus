using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    [InlineData("memory", false)]
    [InlineData("memory", true)]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    public async Task CreateSection_Empty_Or_Reordered_Groups_Returns_Exact_Stored_Record(string kind, bool hasExistingGroups)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            if (hasExistingGroups)
            {
                var existing = await store.CreateSectionAsync(NewSection(agent, "Existing"));
                await store.ReorderGroupsAsync(agent, [Custom(existing)]);
            }
            var priorOrder = await store.GetGroupOrderAsync(agent);
            var input = NewSection(agent, "New section") with
            {
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
                WorldId = "test-world",
                IsCollapsed = true
            };

            var created = await store.CreateSectionAsync(input);
            var fetched = await store.GetSectionAsync(created.SectionId);
            var listed = await store.ListSectionsAsync(agent);

            fetched.ShouldNotBeNull();
            created.UpdatedAt.ShouldBe(fetched.UpdatedAt);
            created.ShouldBe(fetched);
            created.ShouldBe(listed.Single(s => s.SectionId == created.SectionId));
            created.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);
            created.UpdatedAt.ShouldBeGreaterThanOrEqualTo(created.CreatedAt);
            created.Order.ShouldBe(hasExistingGroups ? 1 : 0);
            created.Name.ShouldBe(input.Name);
            created.WorldId.ShouldBe(input.WorldId);
            created.IsCollapsed.ShouldBeTrue();
            (await store.GetGroupOrderAsync(agent)).ShouldBe(priorOrder.Concat([Custom(created)]));
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

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task GroupOrder_Defaults_To_All_BuiltIns_Then_Customs(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            (await store.GetGroupOrderAsync(agent)).ShouldBe(BuiltIns());
            var a = await store.CreateSectionAsync(NewSection(agent, "Pinned"));
            var b = await store.CreateSectionAsync(NewSection(agent, "B"));
            (await store.GetGroupOrderAsync(agent)).ShouldBe(
                BuiltIns().Concat([Custom(a), Custom(b)]));
            (await store.ListSectionsAsync(agent)).Count.ShouldBe(2);
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task GroupOrder_Interleaves_And_Legacy_Reorder_Changes_Only_Custom_Slots(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var a = await store.CreateSectionAsync(NewSection(agent, "A"));
            var b = await store.CreateSectionAsync(NewSection(agent, "B"));
            var initial = new[] { SystemGroup(BuiltInConversationSection.Pinned), Custom(a),
                SystemGroup(BuiltInConversationSection.Scheduled), Custom(b),
                SystemGroup(BuiltInConversationSection.Conversations), SystemGroup(BuiltInConversationSection.Webhooks) };
            (await store.ReorderGroupsAsync(agent, initial)).ShouldBe(initial);
            await store.ReorderSectionsAsync(agent, [b.SectionId, b.SectionId, SectionId.Create()]);
            (await store.GetGroupOrderAsync(agent)).ShouldBe(new[] { initial[0], Custom(b), initial[2], Custom(a), initial[4], initial[5] });
            (await store.ListSectionsAsync(agent)).Select(s => s.SectionId).ShouldBe([b.SectionId, a.SectionId]);
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task GroupOrder_Omitted_Groups_Append_In_Prior_Order_And_Create_Appends_After_System(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var a = await store.CreateSectionAsync(NewSection(agent, "A"));
            var prior = await store.ReorderGroupsAsync(agent, [Custom(a)]);
            var b = await store.CreateSectionAsync(NewSection(agent, "B"));
            (await store.GetGroupOrderAsync(agent)).ShouldBe(prior.Concat([Custom(b)]));
            // A stale client omits the newly created B; it cannot erase B's canonical slot.
            (await store.ReorderGroupsAsync(agent, [SystemGroup(BuiltInConversationSection.Webhooks)]))
                .ShouldBe(new[] { SystemGroup(BuiltInConversationSection.Webhooks) }
                    .Concat(prior.Where(e => e.System != BuiltInConversationSection.Webhooks)).Concat([Custom(b)]));
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task GroupOrder_Invalid_Reorders_Do_Not_Partially_Mutate_Either_Order(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var a = await store.CreateSectionAsync(NewSection(agent, "A"));
            var foreign = await store.CreateSectionAsync(NewSection(Agent("agent-b"), "Foreign"));
            var before = await store.GetGroupOrderAsync(agent);
            var foreignBefore = await store.GetGroupOrderAsync(foreign.AgentId);
            IReadOnlyList<ConversationSectionOrderEntry>[] invalid = [
                [Custom(a), Custom(a)],
                [SystemGroup(BuiltInConversationSection.Scheduled), SystemGroup(BuiltInConversationSection.Scheduled)],
                [Custom(a), Custom(foreign)],
                [Custom(a), ConversationSectionOrderEntry.ForSection(SectionId.Create())]];
            foreach (var order in invalid)
            {
                await Should.ThrowAsync<ArgumentException>(async () => await store.ReorderGroupsAsync(agent, order));
                (await store.GetGroupOrderAsync(agent)).ShouldBe(before);
                (await store.ListSectionsAsync(agent)).ShouldHaveSingleItem().Order.ShouldBe(0);
                (await store.GetGroupOrderAsync(foreign.AgentId)).ShouldBe(foreignBefore);
            }
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Delete_Removes_Only_Custom_Order_And_Assignments(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var a = await store.CreateSectionAsync(NewSection(agent, "A"));
            var b = await store.CreateSectionAsync(NewSection(agent, "B"));
            var conversation = ConversationId.Create();
            await store.AssignConversationAsync(a.SectionId, conversation);
            var prior = await store.ReorderGroupsAsync(agent, [Custom(a), SystemGroup(BuiltInConversationSection.Webhooks), Custom(b)]);
            await store.DeleteSectionAsync(a.SectionId);
            (await store.GetGroupOrderAsync(agent)).ShouldBe(prior.Where(e => e != Custom(a)));
            (await store.GetAssignmentsAsync(agent)).ShouldBeEmpty();
            (await store.ListSectionsAsync(agent)).ShouldHaveSingleItem().Order.ShouldBe(0);
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task BuiltIn_Identity_Is_Not_A_Custom_Row(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            foreach (var system in Enum.GetValues<BuiltInConversationSection>())
            {
                var id = SectionId.From(system.ToString());
                (await store.UpdateSectionAsync(id, "renamed", true)).ShouldBeNull();
                await store.DeleteSectionAsync(id);
                await Should.ThrowAsync<InvalidOperationException>(async () => await store.AssignConversationAsync(id, ConversationId.Create()));
            }
            (await store.GetGroupOrderAsync(Agent("agent-a"))).ShouldBe(BuiltIns());
        }
        finally { disposable?.Dispose(); }
    }

    [Theory]
    [InlineData("sec_custom")]
    [InlineData("Pinned")]
    [InlineData("  custom-with-whitespace  ")]
    public void ForSection_InitializedId_PreservesCustomIdentity(string value)
    {
        var sectionId = SectionId.From(value);
        var entry = ConversationSectionOrderEntry.ForSection(sectionId);

        entry.System.ShouldBeNull();
        entry.SectionId.ShouldBe(sectionId);
        entry.ShouldBe(ConversationSectionOrderEntry.ForSection(SectionId.From(value.Trim())));
    }

    [Fact]
    public void GroupIdentity_Rejects_Invalid_Enum_And_Serializes_As_String()
    {
        Should.Throw<ArgumentException>(() => ConversationSectionOrderEntry.ForSystem((BuiltInConversationSection)999));
        JsonSerializer.Serialize(BuiltInConversationSection.Pinned).ShouldBe("\"Pinned\"");
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<BuiltInConversationSection>("\"Unknown\""));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<BuiltInConversationSection>("0"));
        Custom(NewSection(Agent("agent-a"), "Pinned")).ShouldNotBe(SystemGroup(BuiltInConversationSection.Pinned));
    }

    [Fact]
    public async Task Sqlite_Upgrades_Legacy_Rows_And_Reopens_Unified_Order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"botnexus-sections-upgrade-{Guid.NewGuid():N}.db");
        using var cleanup = new FileDeleter(path);
        var connectionString = $"Data Source={path};Pooling=False";
        var agent = Agent("agent-a");
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE conversation_sections (id TEXT PRIMARY KEY, agent_id TEXT NOT NULL,
                    world_id TEXT NOT NULL DEFAULT '', name TEXT NOT NULL, section_order INTEGER NOT NULL,
                    is_collapsed INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE conversation_section_assignments (conversation_id TEXT PRIMARY KEY,
                    section_id TEXT NOT NULL, assigned_at TEXT NOT NULL,
                    FOREIGN KEY(section_id) REFERENCES conversation_sections(id) ON DELETE CASCADE);
                INSERT INTO conversation_sections VALUES ('legacy','agent-a','world-stamp','Kept',7,1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO conversation_section_assignments VALUES ('conv-legacy','legacy','2026-01-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
        var legacy = (await store.ListSectionsAsync(agent)).ShouldHaveSingleItem();
        legacy.Name.ShouldBe("Kept");
        legacy.IsCollapsed.ShouldBeTrue();
        legacy.WorldId.ShouldBe("world-stamp");
        (await store.GetGroupOrderAsync(agent)).ShouldBe(BuiltIns().Concat([Custom(legacy)]));
        var expected = await store.ReorderGroupsAsync(agent, [Custom(legacy), SystemGroup(BuiltInConversationSection.Webhooks)]);
        var reopened = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
        (await reopened.GetGroupOrderAsync(agent)).ShouldBe(expected);
        (await reopened.GetAssignmentsAsync(agent))["conv-legacy"].ShouldBe("legacy");
        var persisted = (await reopened.ListSectionsAsync(agent)).ShouldHaveSingleItem();
        persisted.SectionId.ShouldBe(legacy.SectionId);
        persisted.Name.ShouldBe(legacy.Name);
        persisted.CreatedAt.ShouldBe(legacy.CreatedAt);
        persisted.IsCollapsed.ShouldBeTrue();
    }

    [Fact]
    public async Task Sqlite_Delete_And_Create_Roll_Back_When_Order_Write_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"botnexus-sections-rollback-{Guid.NewGuid():N}.db");
        using var cleanup = new FileDeleter(path);
        var connectionString = $"Data Source={path};Pooling=False";
        var store = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
        var agent = Agent("agent-a");
        var section = await store.CreateSectionAsync(NewSection(agent, "Kept"));
        var conversation = ConversationId.Create();
        await store.AssignConversationAsync(section.SectionId, conversation);
        var prior = await store.GetGroupOrderAsync(agent);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_group_order BEFORE INSERT ON conversation_section_group_order
                BEGIN SELECT RAISE(ABORT, 'injected order failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Should.ThrowAsync<SqliteException>(async () => await store.DeleteSectionAsync(section.SectionId));
        (await store.GetSectionAsync(section.SectionId)).ShouldNotBeNull();
        (await store.GetAssignmentsAsync(agent))[conversation.Value].ShouldBe(section.SectionId.Value);
        (await store.GetGroupOrderAsync(agent)).ShouldBe(prior);
        var attempted = NewSection(agent, "Must not survive");
        await Should.ThrowAsync<SqliteException>(async () => await store.CreateSectionAsync(attempted));
        (await store.GetSectionAsync(attempted.SectionId)).ShouldBeNull();
        (await store.GetGroupOrderAsync(agent)).ShouldBe(prior);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Concurrent_Create_Reorder_Delete_Leaves_Complete_Unique_Order(string kind)
    {
        var (store, disposable) = Create(kind);
        try
        {
            var agent = Agent("agent-a");
            var created = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(i => store.CreateSectionAsync(NewSection(agent, $"S{i}"))));
            var ids = created.Select(s => s.SectionId).ToHashSet();
            var initial = await store.GetGroupOrderAsync(agent);
            initial.Count.ShouldBe(16);
            initial.Distinct().Count().ShouldBe(16);
            initial.Where(e => e.SectionId.HasValue).Select(e => e.SectionId.GetValueOrDefault()).ToHashSet().SetEquals(ids).ShouldBeTrue();
            await Task.WhenAll(
                store.ReorderGroupsAsync(agent, [SystemGroup(BuiltInConversationSection.Webhooks)]),
                store.DeleteSectionAsync(created[0].SectionId),
                store.AssignConversationAsync(created[1].SectionId, ConversationId.Create()));
            var final = await store.GetGroupOrderAsync(agent);
            final.Count.ShouldBe(15);
            final.Distinct().Count().ShouldBe(15);
            final[0].ShouldBe(SystemGroup(BuiltInConversationSection.Webhooks));
            final.ShouldNotContain(Custom(created[0]));
            (await store.ListSectionsAsync(agent)).Select(s => s.Order).ShouldBe(Enumerable.Range(0, 11));
        }
        finally { disposable?.Dispose(); }
    }

    [Fact]
    public async Task Sqlite_Read_Prunes_Stale_And_Foreign_Metadata_And_Appends_Missing_Groups_Without_Write()
    {
        var path = Path.Combine(Path.GetTempPath(), $"botnexus-sections-normalize-{Guid.NewGuid():N}.db");
        using var cleanup = new FileDeleter(path);
        var connectionString = $"Data Source={path};Pooling=False";
        var store = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
        var agent = Agent("agent-a");
        var owned = await store.CreateSectionAsync(NewSection(agent, "Kept"));
        var foreign = await store.CreateSectionAsync(NewSection(Agent("agent-b"), "Foreign"));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Simulate older/corrupted metadata with FK enforcement disabled only in this isolated seed.
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            DELETE FROM conversation_section_group_order WHERE agent_id = 'agent-a';
            INSERT INTO conversation_section_group_order VALUES ('agent-a',0,'Webhooks',NULL);
            INSERT INTO conversation_section_group_order VALUES ('agent-a',1,NULL,'deleted');
            INSERT INTO conversation_section_group_order VALUES ('agent-a',2,NULL,$foreign);
            INSERT INTO conversation_section_group_order VALUES ('agent-a',3,'Unknown',NULL);
            """;
        command.Parameters.AddWithValue("$foreign", foreign.SectionId.Value);
        await command.ExecuteNonQueryAsync();
        (await store.GetGroupOrderAsync(agent)).ShouldBe(new[] { SystemGroup(BuiltInConversationSection.Webhooks) }
            .Concat(BuiltIns().Where(e => e.System != BuiltInConversationSection.Webhooks)).Concat([Custom(owned)]));
        command.CommandText = "SELECT COUNT(*) FROM conversation_section_group_order WHERE agent_id = 'agent-a'";
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(4);
        // A successful mutation persists the complete repaired order, not stale references.
        var repaired = await store.ReorderGroupsAsync(agent, []);
        command.CommandText = "SELECT COUNT(*) FROM conversation_section_group_order WHERE agent_id = 'agent-a'";
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(repaired.Count);
    }

    /// <summary>Exercises mutations from released worker contexts, including SQLite's database-wide boundary.</summary>
    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("sqlite-multi")]
    public async Task Concurrent_Released_Create_Reorder_Delete_Preserves_Membership_And_Assignments(string kind)
    {
        var (stores, disposable) = CreateConcurrentStores(kind);
        try
        {
            var agent = Agent("agent-a");
            foreach (var reader in stores)
                await reader.GetGroupOrderAsync(agent); // Complete per-instance schema initialization before release.
            // Repeated fresh contenders increase exercised schedules without using timing as an assertion.
            for (var round = 0; round < 8; round++)
            {
                var kept = await stores[0].CreateSectionAsync(NewSection(agent, $"Kept-{round}"));
                var deleted = await stores[0].CreateSectionAsync(NewSection(agent, $"Deleted-{round}"));
                var removedConversation = ConversationId.Create();
                var retainedConversation = ConversationId.Create();
                await stores[0].AssignConversationAsync(deleted.SectionId, removedConversation);
                var incoming = NewSection(agent, $"Created-{round}");
                var before = await stores[0].ListSectionsAsync(agent);
                var expectedIds = before.Select(s => s.SectionId).Where(id => id != deleted.SectionId)
                    .Append(incoming.SectionId).ToHashSet();
                await RunReleasedWorkersAsync(
                    async ct => { await stores[0].CreateSectionAsync(incoming, ct); },
                    async ct => { await stores[1].ReorderGroupsAsync(agent,
                        [SystemGroup(BuiltInConversationSection.Webhooks), Custom(kept)], ct); },
                    ct => stores[2].DeleteSectionAsync(deleted.SectionId, ct),
                    ct => stores[3].AssignConversationAsync(kept.SectionId, retainedConversation, ct));

                await AssertCompleteOrderAsync(stores[0], agent, expectedIds);
                var order = await stores[0].GetGroupOrderAsync(agent);
                order[0].ShouldBe(SystemGroup(BuiltInConversationSection.Webhooks));
                order.ShouldContain(Custom(incoming)); // A stale reorder must not lose the concurrently created slot.
                order.ShouldNotContain(Custom(deleted));
                (await stores[0].GetSectionAsync(deleted.SectionId)).ShouldBeNull();
                var assignments = await stores[0].GetAssignmentsAsync(agent);
                assignments.ShouldNotContainKey(removedConversation.Value);
                assignments[retainedConversation.Value].ShouldBe(kept.SectionId.Value);
                assignments.Count.ShouldBe(round + 1);
                assignments.Values.ShouldAllBe(id => expectedIds.Any(sectionId => sectionId.Value == id));
                // Every independent SQLite instance must see the same committed representations.
                foreach (var reader in stores)
                {
                    (await reader.GetGroupOrderAsync(agent)).ShouldBe(order);
                    await AssertCompleteOrderAsync(reader, agent, expectedIds);
                    (await reader.GetAssignmentsAsync(agent)).OrderBy(pair => pair.Key)
                        .ShouldBe(assignments.OrderBy(pair => pair.Key));
                }
            }
        }
        finally { disposable?.Dispose(); }
    }

    /// <summary>Checks either legal serial outcome of competing legacy and unified order writers.</summary>
    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("sqlite-multi")]
    public async Task Concurrent_Released_Legacy_And_Unified_Reorders_Keep_Both_Representations_In_Sync(string kind)
    {
        var (stores, disposable) = CreateConcurrentStores(kind);
        try
        {
            var agent = Agent("agent-a");
            foreach (var reader in stores)
                await reader.GetGroupOrderAsync(agent);
            var a = await stores[0].CreateSectionAsync(NewSection(agent, "A"));
            var b = await stores[0].CreateSectionAsync(NewSection(agent, "B"));
            var c = await stores[0].CreateSectionAsync(NewSection(agent, "C"));
            var conversation = ConversationId.Create();
            await stores[0].AssignConversationAsync(a.SectionId, conversation);
            var prior = new[] { SystemGroup(BuiltInConversationSection.Pinned), Custom(a),
                SystemGroup(BuiltInConversationSection.Scheduled), Custom(b),
                SystemGroup(BuiltInConversationSection.Conversations), Custom(c), SystemGroup(BuiltInConversationSection.Webhooks) };
            var unified = new[] { SystemGroup(BuiltInConversationSection.Webhooks), Custom(c),
                SystemGroup(BuiltInConversationSection.Pinned), Custom(a),
                SystemGroup(BuiltInConversationSection.Scheduled), Custom(b), SystemGroup(BuiltInConversationSection.Conversations) };
            var legacyLast = new[] { unified[0], Custom(b), unified[2], Custom(c), unified[4], Custom(a), unified[6] };
            for (var round = 0; round < 8; round++)
            {
                await stores[0].ReorderGroupsAsync(agent, prior);
                await RunReleasedWorkersAsync(
                    ct => stores[0].ReorderSectionsAsync(agent, [b.SectionId, c.SectionId, a.SectionId], ct),
                    async ct => { await stores[1].ReorderGroupsAsync(agent, unified, ct); });
                var final = await stores[0].GetGroupOrderAsync(agent);
                (final.SequenceEqual(unified) || final.SequenceEqual(legacyLast)).ShouldBeTrue();
                foreach (var reader in stores)
                {
                    (await reader.GetGroupOrderAsync(agent)).ShouldBe(final);
                    await AssertCompleteOrderAsync(reader, agent, new HashSet<SectionId> { a.SectionId, b.SectionId, c.SectionId });
                    var assignments = await reader.GetAssignmentsAsync(agent);
                    assignments.Count.ShouldBe(1);
                    assignments[conversation.Value].ShouldBe(a.SectionId.Value);
                }
            }
        }
        finally { disposable?.Dispose(); }
    }

    /// <summary>Requires rollback after a changed custom Order, not merely validation before the first write.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_Reorders_Roll_Back_Exact_Persisted_State_After_Late_Custom_Order_Write(bool unified)
    {
        var path = Path.Combine(Path.GetTempPath(), $"botnexus-sections-late-rollback-{Guid.NewGuid():N}.db");
        using var cleanup = new FileDeleter(path);
        var connectionString = $"Data Source={path};Pooling=False;Default Timeout=5";
        var store = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
        var agent = Agent("agent-a");
        var a = await store.CreateSectionAsync(NewSection(agent, "A"));
        var b = await store.CreateSectionAsync(NewSection(agent, "B"));
        var c = await store.CreateSectionAsync(NewSection(agent, "C"));
        await store.UpdateSectionAsync(b.SectionId, "B collapsed", true);
        var conversationA = ConversationId.Create();
        var conversationC = ConversationId.Create();
        await store.AssignConversationAsync(a.SectionId, conversationA);
        await store.AssignConversationAsync(c.SectionId, conversationC);
        var prior = new[] { SystemGroup(BuiltInConversationSection.Pinned), Custom(b),
            SystemGroup(BuiltInConversationSection.Scheduled), Custom(a),
            SystemGroup(BuiltInConversationSection.Conversations), Custom(c), SystemGroup(BuiltInConversationSection.Webhooks) };
        await store.ReorderGroupsAsync(agent, prior);
        var priorC = await store.GetSectionAsync(c.SectionId)
            ?? throw new InvalidOperationException("Seeded section C is missing.");
        priorC.Order.ShouldBe(2);
        var requested = new[] { SystemGroup(BuiltInConversationSection.Webhooks), Custom(c),
            SystemGroup(BuiltInConversationSection.Pinned), Custom(a),
            SystemGroup(BuiltInConversationSection.Scheduled), Custom(b), SystemGroup(BuiltInConversationSection.Conversations) };
        var expected = unified ? requested : new[] { prior[0], Custom(c), prior[2], Custom(a), prior[4], Custom(b), prior[6] };
        var before = await ReadPersistedSectionStateAsync(connectionString);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // The marker is produced only if C's legacy Order has changed from 2 to 0 and its new
            // metadata row is already present. Failing earlier produces a different error, failing the test.
            // IDs come from this isolated fixture; trigger DDL cannot bind parameters.
            var aId = a.SectionId.Value.Replace("'", "''", StringComparison.Ordinal);
            var cId = c.SectionId.Value.Replace("'", "''", StringComparison.Ordinal);
            command.CommandText = $"""
                CREATE TRIGGER reject_late_reorder BEFORE INSERT ON conversation_section_group_order
                WHEN NEW.section_id = '{aId}'
                BEGIN
                    SELECT CASE WHEN
                        (SELECT section_order FROM conversation_sections WHERE id = '{cId}') = 0
                        AND EXISTS (SELECT 1 FROM conversation_section_group_order WHERE section_id = '{cId}')
                    THEN RAISE(ABORT, 'injected after custom order update')
                    ELSE RAISE(ABORT, 'injection reached before required custom update') END;
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        async Task ReorderAsync()
        {
            if (unified)
                await store.ReorderGroupsAsync(agent, requested);
            else
                await store.ReorderSectionsAsync(agent, [c.SectionId, a.SectionId, b.SectionId]);
        }

        try
        {
            var error = await Should.ThrowAsync<SqliteException>(ReorderAsync);
            error.Message.ShouldContain("injected after custom order update");
            // Raw persisted rows include every metadata position, section property, both timestamps,
            // and assignment timestamps. A normalized API read alone could conceal partial metadata loss.
            (await ReadPersistedSectionStateAsync(connectionString)).ShouldBe(before);
            (await store.GetGroupOrderAsync(agent)).ShouldBe(prior);
            (await store.ListSectionsAsync(agent)).Select(s => s.SectionId).ShouldBe([b.SectionId, a.SectionId, c.SectionId]);
            (await store.ListSectionsAsync(agent)).Select(s => s.Order).ShouldBe([0, 1, 2]);
            var reopened = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
            (await reopened.GetGroupOrderAsync(agent)).ShouldBe(prior);
            (await reopened.ListSectionsAsync(agent)).Select(s => s.SectionId).ShouldBe([b.SectionId, a.SectionId, c.SectionId]);
            (await reopened.ListSectionsAsync(agent)).Select(s => s.Order).ShouldBe([0, 1, 2]);
            var assignments = await reopened.GetAssignmentsAsync(agent);
            assignments.Count.ShouldBe(2);
            assignments[conversationA.Value].ShouldBe(a.SectionId.Value);
            assignments[conversationC.Value].ShouldBe(c.SectionId.Value);
            await AssertCompleteOrderAsync(reopened, agent, new HashSet<SectionId> { a.SectionId, b.SectionId, c.SectionId });
            (await ReadPersistedSectionStateAsync(connectionString)).ShouldBe(before);
        }
        finally
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER IF EXISTS reject_late_reorder";
            await command.ExecuteNonQueryAsync();
        }

        await ReorderAsync(); // The same candidate request succeeds once the isolated fault is removed.
        var afterRetry = new SqliteConversationSectionStore(connectionString, NullLogger<SqliteConversationSectionStore>.Instance);
        (await afterRetry.GetGroupOrderAsync(agent)).ShouldBe(expected);
        (await afterRetry.ListSectionsAsync(agent)).Select(s => s.SectionId).ShouldBe([c.SectionId, a.SectionId, b.SectionId]);
        await AssertCompleteOrderAsync(afterRetry, agent, new HashSet<SectionId> { a.SectionId, b.SectionId, c.SectionId });
        (await ReadPersistedSectionStateAsync(connectionString)).Assignments.ShouldBe(before.Assignments);
    }

    private static (IConversationSectionStore[] Stores, IDisposable? Disposable) CreateConcurrentStores(string kind)
    {
        if (kind != "sqlite-multi")
        {
            var (store, disposable) = Create(kind);
            return ([store, store, store, store], disposable);
        }
        var path = Path.Combine(Path.GetTempPath(), $"botnexus-sections-shared-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False;Default Timeout=5";
        return (Enumerable.Range(0, 4).Select(_ => (IConversationSectionStore)new SqliteConversationSectionStore(
            connectionString, NullLogger<SqliteConversationSectionStore>.Instance)).ToArray(), new FileDeleter(path));
    }

    private static async Task RunReleasedWorkersAsync(params Func<CancellationToken, Task>[] operations)
    {
        using var release = new ManualResetEventSlim(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = operations.Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        // Dedicated contexts matter: memory methods complete synchronously, so constructing WhenAll
        // arguments on the test thread never contends. Each worker signals before a bounded release wait.
        var workers = operations.Select((operation, index) => Task.Factory.StartNew(async () =>
        {
            ready[index].TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(10), deadline.Token))
                throw new TimeoutException("Concurrent section worker was not released.");
            await operation(deadline.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
        var completion = Task.WhenAll(workers);
        try
        {
            await Task.WhenAll(ready.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            // Readiness failure cannot strand peers at the gate. Drain even failed workers before
            // disposing signals or the caller's isolated database. The native SQLite busy timeout
            // and cooperative cancellation bound normal store operations; the drain has a diagnostic deadline too.
            release.Set();
            deadline.Cancel();
            await completion.WaitAsync(TimeSpan.FromSeconds(40));
        }
    }

    private static async Task AssertCompleteOrderAsync(IConversationSectionStore store, AgentId agent, HashSet<SectionId> expectedIds)
    {
        var order = await store.GetGroupOrderAsync(agent);
        var sections = await store.ListSectionsAsync(agent);
        BuiltIns().Count.ShouldBe(4);
        order.Count.ShouldBe(4 + expectedIds.Count);
        order.Distinct().Count().ShouldBe(order.Count);
        order.Where(entry => entry.System.HasValue).ToHashSet().SetEquals(BuiltIns()).ShouldBeTrue();
        order.Where(entry => entry.SectionId.HasValue).Select(entry => entry.SectionId.GetValueOrDefault())
            .ToHashSet().SetEquals(expectedIds).ShouldBeTrue();
        sections.Select(section => section.SectionId).ToHashSet().SetEquals(expectedIds).ShouldBeTrue();
        sections.Count.ShouldBe(expectedIds.Count);
        sections.Select(section => section.Order).ShouldBe(Enumerable.Range(0, expectedIds.Count));
        sections.Select(section => section.SectionId).ShouldBe(order.Where(entry => entry.SectionId.HasValue)
            .Select(entry => entry.SectionId.GetValueOrDefault()));
    }

    private static async Task<(string Sections, string GroupOrder, string Assignments)> ReadPersistedSectionStateAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return (
            await ReadRowsAsync("SELECT id, agent_id, world_id, name, section_order, is_collapsed, created_at, updated_at FROM conversation_sections ORDER BY id"),
            await ReadRowsAsync("SELECT agent_id, group_order, system_group, section_id FROM conversation_section_group_order ORDER BY agent_id, group_order"),
            await ReadRowsAsync("SELECT conversation_id, section_id, assigned_at FROM conversation_section_assignments ORDER BY conversation_id"));

        async Task<string> ReadRowsAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return JsonSerializer.Serialize(rows);
        }
    }

    private static ConversationSectionOrderEntry SystemGroup(BuiltInConversationSection system) => ConversationSectionOrderEntry.ForSystem(system);
    private static ConversationSectionOrderEntry Custom(ConversationSection section) => ConversationSectionOrderEntry.ForSection(section.SectionId);
    private static IReadOnlyList<ConversationSectionOrderEntry> BuiltIns() => Enum.GetValues<BuiltInConversationSection>().Select(SystemGroup).ToArray();

    private static ConversationSection NewSection(AgentId agent, string name) => new()
    {
        SectionId = SectionId.Create(),
        AgentId = agent,
        Name = name
    };
}
