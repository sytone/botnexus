using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Tools;

namespace BotNexus.Tools.Tests;

/// <summary>
/// Covers the tool that reads a truncated payload back through its continuation handle (#2760).
/// </summary>
public class ToolOutputContinuationToolTests
{
    /// <summary>
    /// The happy path: a stored payload is paged back completely, and the final call says so rather
    /// than leaving the caller unable to tell "done" from "more to come".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PagesStoredPayloadToCompletion()
    {
        var store = new ToolOutputContinuationStore();
        var payload = string.Concat(Enumerable.Range(0, 4096).Select(i => (char)('a' + (i % 26))));
        var handle = store.Store(payload);
        var tool = new ToolOutputContinuationTool(store);

        var first = await ExecuteAsync(tool, handle, 0, 1024);
        first.ShouldContain(payload[..1024]);
        first.ShouldContain("next offset 1024");
        first.ShouldContain(ToolOutputBudget.ContinuationToolName);

        var last = await ExecuteAsync(tool, handle, 4000, 1024);
        last.ShouldContain(payload[4000..]);
        last.ShouldContain("continuation complete");
    }

    /// <summary>
    /// An unknown or evicted handle produces an explicit, actionable diagnostic - not an empty
    /// success that the model would read as "the data is gone and that is fine".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnknownHandle_ExplainsAndNamesTheRemedy()
    {
        var text = await ExecuteAsync(new ToolOutputContinuationTool(new ToolOutputContinuationStore()), "toc_gone", 0, 64);

        text.ShouldContain("No stored output for handle");
        text.ShouldContain("rerun the original tool");
    }

    /// <summary>An offset beyond the payload is reported as out of range, with the real total.</summary>
    [Fact]
    public async Task ExecuteAsync_OffsetOutOfRange_ReportsTheTotal()
    {
        var store = new ToolOutputContinuationStore();
        var handle = store.Store("abcdef");

        var text = await ExecuteAsync(new ToolOutputContinuationTool(store), handle, 900, 64);

        text.ShouldContain("outside the stored output");
        text.ShouldContain("total 6 bytes");
    }

    /// <summary>
    /// A missing handle is rejected at argument-preparation time rather than silently returning the
    /// unknown-handle diagnostic, so a malformed call is distinguishable from an evicted payload.
    /// </summary>
    [Fact]
    public async Task PrepareArgumentsAsync_MissingHandle_Throws()
    {
        var tool = new ToolOutputContinuationTool(new ToolOutputContinuationStore());

        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?>(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The declared tool name is the one the truncation marker tells the model to call. A drift
    /// between them would leave every marker pointing at a tool that does not exist.
    /// </summary>
    [Fact]
    public void Name_MatchesTheNameAdvertisedInTheTruncationMarker()
        => new ToolOutputContinuationTool().Name.ShouldBe(ToolOutputBudget.ContinuationToolName);

    private static async Task<string> ExecuteAsync(ToolOutputContinuationTool tool, string handle, long offset, int maxBytes)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["handle"] = handle,
            ["offset"] = offset,
            ["max_bytes"] = maxBytes
        };

        var result = await tool.ExecuteAsync("call-1", args);
        return string.Concat(result.Content
            .Where(block => block.Type == AgentToolContentType.Text)
            .Select(block => block.Value));
    }
}
