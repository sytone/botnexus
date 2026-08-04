using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Pins issue #2780: markdown notes written through <see cref="MarkdownAgentMemory"/> must be
/// mirrored into the searchable memory store with <c>SourceType = "note"</c>, so
/// <c>memory_search</c> can return deliberately-curated daily notes and not only conversation
/// turns.
/// </summary>
/// <remarks>
/// <para>
/// The four query strings below are the field-test corpus recorded on #2780 and are reproduced
/// <b>verbatim</b>. Cases 1-3 were failing queries whose ground truth lived in a daily note;
/// case 4 is the control that already resolved to a conversation turn and must not regress.
/// </para>
/// <para>
/// The reporter's constraint is honoured deliberately: every document is seeded into a
/// <b>test-owned</b> store under a temp directory. Reading a live agent store would make the
/// corpus degrade every time these fixtures are discussed, because discussing them indexes
/// turns containing the same strings. A regression corpus that changes when you talk about it
/// is not a corpus.
/// </para>
/// </remarks>
public sealed class MarkdownNoteIndexingTests
{
    private const string Query1 = "watermark stale keel archive pulled_utc";
    private const string Query2 = "mail script queryParameters raw string gotcha";
    private const string Query3 = "Minu Iyer promotion rule measurement set";
    private const string Query4Control = "Jon knee injury MRI brace";

    private const string Note0803 = """
        ## Keel watermark audit

        KEEL'S RECORD WAS 5h20m STALE: stated 2026-08-03T15:05:00Z, actual newest pulled_utc was
        2026-08-03T20:25:27Z. The archive watermark is not advanced by the poller.

        ## People enrichment

        PROMOTION - Minu Iyer, hypothesis -> RULE R2 after the measurement set cleared threshold.
        """;

    private const string Note0802 = """
        ## Mail skill gotcha

        queryParameters is a RAW QUERY STRING that MUST START WITH ? - the script silently returns
        an unfiltered page when it does not.
        """;

    [Fact]
    public async Task SaveToFile_IndexesTheNote_AndItIsRetrievableBySearch_Ac1()
    {
        await using var ctx = await NoteTestContext.CreateAsync();

        await ctx.Memory.SaveToFileAsync(Note0803, "memory/2026-08-03.md");

        var hits = await ctx.Store.SearchAsync(Query1, 10);

        hits.ShouldNotBeEmpty();
        var top = hits[0];
        top.SourceType.ShouldBe("note");
        top.Content.ShouldContain("pulled_utc");
        top.MetadataJson.ShouldNotBeNull();
        top.MetadataJson!.ShouldContain("2026-08-03.md");
    }

    [Fact]
    public async Task SaveWithoutFilePath_IndexesTheDailyNote_AndItIsRetrievable_Ac2()
    {
        await using var ctx = await NoteTestContext.CreateAsync();

        await ctx.Memory.SaveAsync(new AgentMemorySaveRequest(NoteTestContext.AgentId, Note0802, "note"));

        var hits = await ctx.Store.SearchAsync(Query2, 10);

        hits.ShouldNotBeEmpty();
        hits[0].SourceType.ShouldBe("note");
        hits[0].Content.ShouldContain("MUST START WITH ?");
    }

    [Fact]
    public async Task MultiSectionNote_ReturnsTheMatchingSectionOnly_Ac3()
    {
        await using var ctx = await NoteTestContext.CreateAsync();

        await ctx.Memory.SaveToFileAsync(Note0803, "memory/2026-08-03.md");

        var hits = await ctx.Store.SearchAsync(Query3, 10);

        hits.ShouldNotBeEmpty();
        var top = hits[0];
        top.Content.ShouldContain("Minu Iyer");
        // The unrelated sibling section of the SAME file must not be dragged along: chunking by
        // heading is what makes BM25 and the vector arm score a coherent unit.
        top.Content.ShouldNotContain("pulled_utc");
        top.MetadataJson!.ShouldContain("People enrichment");
    }

    [Fact]
    public async Task SourceTypeFilter_SelectsNotesAndExcludesConversationTurns_Ac4()
    {
        await using var ctx = await NoteTestContext.CreateAsync();

        await ctx.Memory.SaveToFileAsync(Note0803, "memory/2026-08-03.md");
        await ctx.SeedControlConversationAsync();

        var notesOnly = await ctx.Store.SearchAsync(
            "keel OR knee", 20, new MemorySearchFilter { SourceType = "note" });
        var conversationsOnly = await ctx.Store.SearchAsync(
            "keel OR knee", 20, new MemorySearchFilter { SourceType = "conversation" });

        notesOnly.ShouldNotBeEmpty();
        notesOnly.ShouldAllBe(e => e.SourceType == "note");
        conversationsOnly.ShouldNotBeEmpty();
        conversationsOnly.ShouldAllBe(e => e.SourceType == "conversation");
        conversationsOnly.ShouldAllBe(e => e.Id != notesOnly[0].Id);
    }

    [Fact]
    public async Task ControlQuery_StillResolvesToTheConversationTurn_Ac4Control()
    {
        await using var ctx = await NoteTestContext.CreateAsync();

        await ctx.Memory.SaveToFileAsync(Note0803, "memory/2026-08-03.md");
        await ctx.Memory.SaveToFileAsync(Note0802, "memory/2026-08-02.md");
        await ctx.SeedControlConversationAsync();

        var hits = await ctx.Store.SearchAsync(Query4Control, 10);

        hits.ShouldNotBeEmpty();
        hits[0].SourceType.ShouldBe("conversation");
        hits[0].Content.ShouldContain("hurt my knee");
    }

    [Fact]
    public async Task RepeatedAppends_UpdateTheSectionRow_RatherThanDuplicatingIt()
    {
        await using var ctx = await NoteTestContext.CreateAsync();

        for (var i = 0; i < 5; i++)
            await ctx.Memory.SaveToFileAsync("## Keel watermark audit\n\nline " + i, "memory/2026-08-03.md");

        var hits = await ctx.Store.SearchAsync("Keel watermark audit", 50,
            new MemorySearchFilter { SourceType = "note" });

        // Five appends to one heading must leave ONE row for that section, not five.
        hits.Count(h => h.MetadataJson!.Contains("Keel watermark audit")).ShouldBe(1);
        // ...and it must carry the latest append, i.e. the re-index replaced rather than skipped.
        hits.Single(h => h.MetadataJson!.Contains("Keel watermark audit")).Content.ShouldContain("line 4");
    }

    [Fact]
    public async Task IndexingFailure_DoesNotLoseTheMarkdownWrite()
    {
        await using var ctx = await NoteTestContext.CreateAsync(new ThrowingMemoryStore());

        // The file is the source of truth: an indexing fault must be swallowed, never surfaced
        // to a caller whose note was written successfully.
        await Should.NotThrowAsync(() => ctx.Memory.SaveToFileAsync(Note0803, "memory/2026-08-03.md"));

        var written = await File.ReadAllTextAsync(
            Path.Combine(ctx.WorkspacePath, "memory", "2026-08-03.md"));
        written.ShouldContain("pulled_utc");
    }

    private sealed class NoteTestContext : IAsyncDisposable
    {
        internal const string AgentId = "note-test-agent";

        private readonly string _root;

        private NoteTestContext(string root, string workspacePath, IMemoryStore store, MarkdownAgentMemory memory)
        {
            _root = root;
            WorkspacePath = workspacePath;
            Store = store;
            Memory = memory;
        }

        public string WorkspacePath { get; }
        public IMemoryStore Store { get; }
        public MarkdownAgentMemory Memory { get; }

        public static async Task<NoteTestContext> CreateAsync(IMemoryStore? store = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "botnexus-note-tests", Guid.NewGuid().ToString("N"));
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.Combine(workspacePath, "memory"));

            var fileSystem = new FileSystem();
            var effectiveStore = store ?? new SqliteMemoryStore(Path.Combine(root, "memory.db"), fileSystem);
            await effectiveStore.InitializeAsync();

            var memory = new MarkdownAgentMemory(
                AgentId,
                new AppendingWorkspaceManager(workspacePath),
                effectiveStore,
                fileSystem);

            return new NoteTestContext(root, workspacePath, effectiveStore, memory);
        }

        public Task SeedControlConversationAsync()
            => Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                "35535c036dfb4ee492d81b8a16a79e6d",
                AgentId,
                "User: I had a fall off the barge and hurt my knee, the MRI is booked and I am in a brace.\n" +
                "Assistant: Noted - knee injury, MRI scheduled, brace in the meantime.",
                sourceType: "conversation",
                sessionId: "session-control",
                turnIndex: 0));

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
            {
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    /// <summary>
    /// Mirrors <c>FileAgentWorkspaceManager.SaveMemoryAsync</c>'s append-to-disk behaviour so the
    /// test exercises the real seam: index AFTER the file write, reading back what was written.
    /// </summary>
    private sealed class AppendingWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public string GetWorkspacePath(string agentName) => workspacePath;

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: "", Identity: "", User: "", Memory: ""));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
            => SaveMemoryAsync(agentName, null, content, null, ct);

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
            => SaveMemoryAsync(agentName, filePath, content, null, ct);

        public async Task SaveMemoryAsync(
            string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default)
        {
            var memoryRoot = Path.Combine(workspacePath, string.IsNullOrWhiteSpace(memoryPathOverride) ? "memory" : memoryPathOverride);
            var relative = string.IsNullOrWhiteSpace(filePath)
                ? $"{DateTime.UtcNow:yyyy-MM-dd}.md"
                : filePath.Replace('\\', '/').StartsWith("memory/", StringComparison.OrdinalIgnoreCase)
                    ? filePath.Replace('\\', '/')["memory/".Length..]
                    : filePath;

            var target = Path.Combine(memoryRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.AppendAllTextAsync(target, content + Environment.NewLine, ct);
        }
    }

    /// <summary>A store whose writes always fail, proving the note write survives an index fault.</summary>
    private sealed class ThrowingMemoryStore : IMemoryStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<Models.MemoryEntry> InsertAsync(Models.MemoryEntry entry, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");
        public Task<Models.MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
            => Task.FromResult<Models.MemoryEntry?>(null);
        public Task<IReadOnlyList<Models.MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Models.MemoryEntry>>([]);
        public Task<IReadOnlyList<Models.MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Models.MemoryEntry>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new MemoryStoreStats(0, 0, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
