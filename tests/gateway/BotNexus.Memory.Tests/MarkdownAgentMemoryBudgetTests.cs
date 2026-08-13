using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Pins issue #2871 at the seam rather than at the helper: <see cref="MarkdownAgentMemory"/> must
/// actually READ <see cref="AgentMemoryPromptRequest.MaxTokenBudget"/>.
/// </summary>
/// <remarks>
/// <see cref="MemoryPromptBudgetTests"/> proves the trimming policy is correct in isolation. That
/// is not sufficient: the original defect was not a wrong policy, it was a policy that no caller
/// invoked. A green helper suite would have stayed green throughout the entire lifetime of the bug.
/// These tests therefore go through the real provider against real files, so unwiring the call
/// reddens them by name.
/// </remarks>
public sealed class MarkdownAgentMemoryBudgetTests
{
    /// <summary>AC1 + AC2: an over-budget memory tree is trimmed and disclosed by the provider.</summary>
    [Fact]
    public async Task GetPromptContext_OverBudget_TrimsAndDiscloses_Ac1()
    {
        await using var ctx = await BudgetTestContext.CreateAsync();
        await ctx.WriteTodayNoteAsync(new string('a', 20_000));

        var context = await ctx.Memory.GetPromptContextAsync(
            new AgentMemoryPromptRequest(BudgetTestContext.AgentId, MaxTokenBudget: 50));

        var rendered = string.Concat(context.DailyNotes.Select(note => note.Content));
        rendered.Contains(MemoryPromptBudget.DisclosureMarker, StringComparison.Ordinal)
            .ShouldBeTrue("the provider must disclose that it trimmed");
        rendered.Length.ShouldBeLessThanOrEqualTo(50 * MemoryPromptBudget.CharsPerToken);
        context.ApproximateTokenCount.ShouldBeLessThanOrEqualTo(50);
    }

    /// <summary>AC1 + AC3: an under-budget tree passes through the provider untouched.</summary>
    [Fact]
    public async Task GetPromptContext_UnderBudget_IsUnchanged_Ac3()
    {
        await using var ctx = await BudgetTestContext.CreateAsync();
        await ctx.WriteTodayNoteAsync("a small daily note");

        var context = await ctx.Memory.GetPromptContextAsync(
            new AgentMemoryPromptRequest(BudgetTestContext.AgentId, MaxTokenBudget: 4000));

        context.DailyNotes.Single().Content.ShouldBe("a small daily note");
        context.DailyNotes.Single().Content.ShouldNotContain(MemoryPromptBudget.DisclosureMarker);
    }

    /// <summary>
    /// The default budget is enforced too. The production caller relied on the record default for
    /// the whole life of the defect, so a fix that only works when the budget is passed explicitly
    /// would leave the reported symptom intact.
    /// </summary>
    [Fact]
    public async Task GetPromptContext_DefaultBudget_IsEnforced_Ac1()
    {
        await using var ctx = await BudgetTestContext.CreateAsync();
        await ctx.WriteTodayNoteAsync(new string('b', 200_000));

        var context = await ctx.Memory.GetPromptContextAsync(
            new AgentMemoryPromptRequest(BudgetTestContext.AgentId));

        var rendered = string.Concat(context.DailyNotes.Select(note => note.Content));
        rendered.Length.ShouldBeLessThanOrEqualTo(4000 * MemoryPromptBudget.CharsPerToken);
        rendered.ShouldContain(MemoryPromptBudget.DisclosureMarker);
    }

    private sealed class BudgetTestContext : IAsyncDisposable
    {
        internal const string AgentId = "budget-test-agent";

        private readonly string _root;
        private readonly string _workspacePath;
        private readonly IMemoryStore _store;

        private BudgetTestContext(string root, string workspacePath, IMemoryStore store, MarkdownAgentMemory memory)
        {
            _root = root;
            _workspacePath = workspacePath;
            _store = store;
            Memory = memory;
        }

        public MarkdownAgentMemory Memory { get; }

        public Task WriteTodayNoteAsync(string content)
        {
            // DateTime.Now (not UtcNow): the provider selects today's and yesterday's notes by
            // LOCAL date, so a UTC-named file would silently miss the window near midnight.
            var path = Path.Combine(_workspacePath, "memory", $"{DateTime.Now:yyyy-MM-dd}.md");
            return File.WriteAllTextAsync(path, content);
        }

        public static async Task<BudgetTestContext> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "botnexus-budget-tests", Guid.NewGuid().ToString("N"));
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.Combine(workspacePath, "memory"));

            var fileSystem = new FileSystem();
            var store = new SqliteMemoryStore(Path.Combine(root, "memory.db"), fileSystem);
            await store.InitializeAsync();

            var memory = new MarkdownAgentMemory(
                AgentId,
                new FixedWorkspaceManager(workspacePath),
                store,
                fileSystem);

            return new BudgetTestContext(root, workspacePath, store, memory);
        }

        public async ValueTask DisposeAsync()
        {
            await _store.DisposeAsync();
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

    private sealed class FixedWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public string GetWorkspacePath(string agentName) => workspacePath;

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: "", Identity: "", User: "", Memory: ""));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
