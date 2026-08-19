using System.Text;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Covers the truncate-with-continuation behaviour of the central tool-output budget (issue #2760):
/// an oversized result must remain USABLE, carrying a handle that reaches the omitted bytes rather
/// than a dead-end prefix.
/// </summary>
public class ToolOutputContinuationTests
{
    /// <summary>
    /// AC1: an oversized result is not a refusal and not a bare prefix - it carries a continuation
    /// handle naming the offset to resume from.
    /// </summary>
    /// <remarks>
    /// AC5 mutation target. Restoring the unconditional hard refusal - dropping the handle from the
    /// marker, or returning an error result with no payload - reddens THIS test by name, because it
    /// asserts on the handle token and the resume offset, not merely on "some text came back".
    /// </remarks>
    [Fact]
    public void Apply_ExceedsBudget_ReturnsTruncatedPayloadWithContinuationHandle()
    {
        var store = new ToolOutputContinuationStore();
        var payload = new string('x', 4096);
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, payload)]);

        var bounded = ToolOutputBudget.Apply(result, 512, store);
        var text = JoinText(bounded);

        // Data, not a refusal: the retained prefix is real content.
        text.ShouldContain(new string('x', 512));

        // A handle, not a dead end.
        var handle = ExtractHandle(text);
        handle.ShouldNotBeNullOrWhiteSpace();
        text.ShouldContain(ToolOutputBudget.ContinuationToolName);
        text.ShouldContain("offset=512");

        // The handle actually resolves to the omitted remainder.
        var slice = store.Read(handle, 512, 1024);
        slice.Status.ShouldBe(ToolOutputContinuationStatus.Ok);
        slice.Text.ShouldBe(new string('x', 1024));
        slice.TotalBytes.ShouldBe(4096);
    }

    /// <summary>
    /// AC2: a 185 KB payload - the largest overshoot in the #2760 forensics window - is retrievable
    /// COMPLETELY by following the handle across successive calls.
    /// </summary>
    /// <remarks>
    /// The fixture size is the point: at 3.6x the historical cap there is no single-shot
    /// parameterisation an agent could have guessed, which is precisely why the issue rejects
    /// narrowing guidance as a sufficient remedy. The assertion is byte-for-byte equality with the
    /// original, so a paging protocol that drops or duplicates a chunk seam fails.
    /// </remarks>
    [Fact]
    public void Apply_LargeFixture_IsFullyRecoverableByFollowingTheHandle()
    {
        var store = new ToolOutputContinuationStore();
        var payload = BuildDistinctPayload(185 * 1024);
        Encoding.UTF8.GetByteCount(payload).ShouldBe(185 * 1024);

        var bounded = ToolOutputBudget.Apply(
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, payload)]),
            51_200,
            store);

        var markerText = JoinText(bounded);
        var handle = ExtractHandle(markerText);

        var reassembled = new StringBuilder(markerText[..51_200]);
        var offset = 51_200L;
        var calls = 0;

        while (true)
        {
            calls++;
            calls.ShouldBeLessThan(100, "paging must terminate, not spin");

            var slice = store.Read(handle, offset, 16 * 1024);
            slice.Status.ShouldBe(ToolOutputContinuationStatus.Ok);
            reassembled.Append(slice.Text);
            offset = slice.NextOffset;

            if (slice.IsComplete)
            {
                break;
            }
        }

        calls.ShouldBeGreaterThan(1, "a 185 KB payload must take more than one continuation call");
        reassembled.ToString().ShouldBe(payload);
    }

    /// <summary>
    /// AC4: a <c>nextLink</c> in the oversized payload is surfaced in the returned result even when
    /// the body carrying it was cut away.
    /// </summary>
    [Fact]
    public void Apply_PayloadContainsNextLink_LinkIsSurfacedDespiteTruncation()
    {
        const string link = "https://graph.microsoft.com/v1.0/me/messages?$skip=50";
        var payload = $$"""{"@odata.nextLink":"{{link}}","value":[{{new string('z', 8192)}}]}""";
        var bounded = ToolOutputBudget.Apply(
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, payload)]),
            128,
            new ToolOutputContinuationStore());

        var text = JoinText(bounded);
        text.ShouldContain(link);
        text.ShouldContain(ToolOutputBudget.NextLinkNotice(link));
    }

    /// <summary>
    /// AC4 sad path: no <c>nextLink</c> in the payload means no fabricated one in the marker. An
    /// invented continuation URL would be worse than none - the caller would follow it and fail.
    /// </summary>
    [Fact]
    public void Apply_PayloadWithoutNextLink_EmitsNoNextLinkNotice()
    {
        var bounded = ToolOutputBudget.Apply(
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, new string('q', 4096))]),
            128,
            new ToolOutputContinuationStore());

        JoinText(bounded).ShouldNotContain("[nextLink:");
    }

    /// <summary>
    /// AC6: a result UNDER the cap is returned byte-identically and the very same instance - no
    /// handle, no marker, no store entry. The continuation machinery must be invisible on the path
    /// it does not apply to.
    /// </summary>
    [Fact]
    public void Apply_ResultWithinBudget_IsUnchangedAndStoresNothing()
    {
        var store = new ToolOutputContinuationStore();
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "small payload")]);

        var bounded = ToolOutputBudget.Apply(result, ToolOutputBudget.DefaultMaxBytes, store);

        bounded.ShouldBeSameAs(result);
        JoinText(bounded).ShouldBe("small payload");
        store.Read("toc_anything", 0, 16).Status.ShouldBe(ToolOutputContinuationStatus.UnknownHandle);
    }

    /// <summary>
    /// The handle is reachable end-to-end through <see cref="ToolExecutor"/>, not merely from a
    /// direct <c>Apply</c> call - the executor is the only seam a real agent ever crosses.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ToolExceedsBudget_ResultCarriesContinuationHandle()
    {
        const int budget = 1024;
        var tool = new FixedOutputTool("bulk", new string('w', budget * 4));
        var context = new AgentContext(null, [], [tool]);
        var assistant = new AssistantAgentMessage(
            Content: string.Empty,
            ToolCalls: [new ToolCallContent("t1", "bulk", new Dictionary<string, object?>(StringComparer.Ordinal))]);
        var config = TestHelpers.CreateTestConfig() with { MaxToolOutputBytes = budget };

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        var message = results.Single();
        message.IsError.ShouldBeFalse();

        var text = JoinText(message.Result);
        text.ShouldContain(ToolOutputBudget.ContinuationToolName);

        var handle = ExtractHandle(text);
        ToolOutputContinuationStore.Shared.Read(handle, 0, 8).Status.ShouldBe(ToolOutputContinuationStatus.Ok);
    }

    /// <summary>
    /// An unknown handle reports itself as unknown rather than as an empty success. "Evicted" and
    /// "here is nothing" must not share a symbol, or the caller cannot tell a retry from a bug.
    /// </summary>
    [Fact]
    public void Read_UnknownHandle_ReportsUnknownHandle()
        => new ToolOutputContinuationStore().Read("toc_missing", 0, 64).Status
            .ShouldBe(ToolOutputContinuationStatus.UnknownHandle);

    /// <summary>An offset past the end is out of range, distinct from an unknown handle.</summary>
    [Fact]
    public void Read_OffsetPastEnd_ReportsOffsetOutOfRange()
    {
        var store = new ToolOutputContinuationStore();
        var handle = store.Store("abcdef");

        var slice = store.Read(handle, 99, 16);

        slice.Status.ShouldBe(ToolOutputContinuationStatus.OffsetOutOfRange);
        slice.TotalBytes.ShouldBe(6);
    }

    /// <summary>
    /// Chunk seams land on rune boundaries, so multi-byte content survives paging intact. A naive
    /// byte cut would corrupt one character at EVERY seam, not just the final one.
    /// </summary>
    [Fact]
    public void Read_MultiByteContent_PagesWithoutCorruptingSeams()
    {
        var store = new ToolOutputContinuationStore();
        var payload = string.Concat(Enumerable.Repeat("\u4f60\u597d\U0001F52C", 512));
        var handle = store.Store(payload);

        var reassembled = new StringBuilder();
        var offset = 0L;
        while (true)
        {
            // 7 is deliberately coprime with the 3- and 4-byte rune widths, so a boundary-unaware
            // implementation is guaranteed to split a rune.
            var slice = store.Read(handle, offset, 7);
            slice.Status.ShouldBe(ToolOutputContinuationStatus.Ok);
            slice.Text.ShouldNotContain("\uFFFD");
            reassembled.Append(slice.Text);

            slice.NextOffset.ShouldBeGreaterThan(offset, "paging must advance, never stall");
            offset = slice.NextOffset;

            if (slice.IsComplete)
            {
                break;
            }
        }

        reassembled.ToString().ShouldBe(payload);
    }

    /// <summary>
    /// The store is bounded and evicts oldest-first: a recovery buffer for oversized payloads that
    /// grew without limit would leak memory in proportion to exactly the traffic it exists to serve.
    /// </summary>
    [Fact]
    public void Store_ExceedsEntryCap_EvictsOldestFirst()
    {
        var store = new ToolOutputContinuationStore(maxEntries: 2);

        var first = store.Store("one");
        var second = store.Store("two");
        var third = store.Store("three");

        store.Read(first, 0, 8).Status.ShouldBe(ToolOutputContinuationStatus.UnknownHandle);
        store.Read(second, 0, 8).Status.ShouldBe(ToolOutputContinuationStatus.Ok);
        store.Read(third, 0, 8).Status.ShouldBe(ToolOutputContinuationStatus.Ok);
    }

    /// <summary>
    /// A null store degrades to truncation-with-guidance rather than throwing. A missing recovery
    /// aid must never be worse than the pre-#2760 behaviour it replaces.
    /// </summary>
    [Fact]
    public void Apply_NullStore_TruncatesWithoutHandleAndDoesNotThrow()
    {
        var bounded = ToolOutputBudget.Apply(
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, new string('n', 4096))]),
            256,
            continuationStore: null);

        var text = JoinText(bounded);
        text.ShouldContain(ToolOutputBudget.NarrowingGuidance);
        text.ShouldNotContain(ToolOutputBudget.ContinuationToolName);
    }

    private static string BuildDistinctPayload(int byteLength)
    {
        // ASCII so byte length equals char length, and non-uniform so a mis-ordered or duplicated
        // chunk cannot accidentally compare equal to the original.
        var builder = new StringBuilder(byteLength);
        for (var i = 0; i < byteLength; i++)
        {
            builder.Append((char)('a' + (i % 26)));
        }

        return builder.ToString();
    }

    private static string ExtractHandle(string text)
    {
        const string marker = "handle=\"";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the truncation marker must name a continuation handle");
        start += marker.Length;
        var end = text.IndexOf('"', start);
        end.ShouldBeGreaterThan(start);
        return text[start..end];
    }

    private static string JoinText(AgentToolResult result)
        => string.Concat(result.Content.Where(block => block.Type == AgentToolContentType.Text).Select(block => block.Value));

    /// <summary>A tool with no cap of its own — the stand-in for every MCP or third-party tool.</summary>
    private sealed class FixedOutputTool(string name, string output) : IAgentTool
    {
        public string Name => name;

        public string Label => name;

        public Tool Definition => new(
            name,
            "returns a fixed payload",
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, output)]));
    }
}
