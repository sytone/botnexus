using System.Text;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Text;
using Shouldly;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// #3628 leg 2: the central byte budget must never hand the model an untrusted-content envelope
/// that opens and never closes.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately written against the MODEL-VISIBLE PROJECTION - the concatenation of every
/// text block the budget returns - rather than against any single block. The defect was structural:
/// each layer was locally correct and the invariant lived between them, so an assertion scoped to
/// one block would have passed while the model still saw a broken envelope.
/// </para>
/// <para>
/// Non-vacuity: every test here fails on <c>main</c> before the fix. The pre-#3628 budget cut a
/// rune-safe prefix and appended a marker, with no knowledge of the fence at all.
/// </para>
/// </remarks>
public class ToolOutputBudgetFenceTests
{
    /// <summary>
    /// The structural invariant under test: an opening fence implies a matching closing fence.
    /// </summary>
    private static void ShouldTerminateEveryEnvelope(AgentToolResult result)
    {
        var projection = Projection(result);
        UntrustedContentFence.TryFindUnterminatedFence(projection, out var orphanId)
            .ShouldBeFalse(
                $"the model-visible projection opens envelope '{orphanId}' and never closes it:\n"
                + projection);
    }

    private static string Projection(AgentToolResult result)
        => string.Join(
            '\n',
            result.Content
                .Where(c => c.Type == AgentToolContentType.Text)
                .Select(c => c.Value));

    /// <summary>
    /// Builds a wrapped envelope of the shape <c>BrowserSnapshotEnvelope.Wrap</c> emits. Constructed
    /// from <see cref="UntrustedContentFence"/> rather than by referencing the browser extension:
    /// the budget is a generic seam and this test must not manufacture the layering inversion the
    /// production code deliberately avoids.
    /// </summary>
    private static (string Text, string Id) WrappedEnvelope(int bodyChars)
    {
        var id = UntrustedContentFence.NewId();
        var text = new StringBuilder()
            .Append(UntrustedContentFence.Render(closing: false, id)).Append('\n')
            .Append("source: https://evil.example/page\n")
            .Append(new string('x', bodyChars)).Append('\n')
            .Append(UntrustedContentFence.Render(closing: true, id))
            .ToString();
        return (text, id);
    }

    [Fact]
    public void Apply_CutInsideTheEnvelopeBody_StillTerminatesTheEnvelope()
    {
        var (text, id) = WrappedEnvelope(bodyChars: 8_000);
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);

        // A budget well below the envelope length forces the prefix cut into the body, discarding
        // the closing fence with the tail - the #3628 leg 2 scenario.
        var bounded = ToolOutputBudget.Apply(result, maxBytes: 2_000, continuationStore: null);

        var projection = Projection(bounded);
        projection.Length.ShouldBeLessThan(text.Length, "the input must actually have been cut");
        projection.ShouldContain(UntrustedContentFence.Render(closing: false, id));
        projection.Contains(UntrustedContentFence.Render(closing: true, id), StringComparison.Ordinal)
            .ShouldBeTrue("the closing fence must be re-emitted with the SAME id, or it closes nothing");
        ShouldTerminateEveryEnvelope(bounded);
    }

    [Fact]
    public void Apply_CutInsideTheEnvelopeBody_KeepsTheTruncationMarkerInsideTheFence()
    {
        var (text, _) = WrappedEnvelope(bodyChars: 8_000);
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);

        var bounded = ToolOutputBudget.Apply(result, maxBytes: 2_000, continuationStore: null);

        var projection = Projection(bounded);
        var markerAt = projection.IndexOf("tool output truncated", StringComparison.Ordinal);
        markerAt.ShouldBeGreaterThan(-1);

        var fences = UntrustedContentFence.MarkerPattern.Matches(projection);
        fences.Count.ShouldBe(2);
        // The marker is narration ABOUT the untrusted payload; if it landed after the closing fence
        // it would read as trusted text the page could have positioned.
        markerAt.ShouldBeGreaterThan(fences[0].Index);
        markerAt.ShouldBeLessThan(fences[1].Index);
    }

    [Theory]
    // Sweep budgets across the region where the cut lands in the body, on the fence, and inside it.
    [InlineData(120)]
    [InlineData(200)]
    [InlineData(260)]
    [InlineData(300)]
    [InlineData(340)]
    [InlineData(380)]
    public void Apply_AtEveryCutPoint_TheProjectionNeverOpensAnEnvelopeItDoesNotClose(int maxBytes)
    {
        // A single hand-picked budget proves one boundary; the hazard is that SOME cut point leaves
        // a partial fence. Sweeping is what makes this non-vacuous.
        var (text, _) = WrappedEnvelope(bodyChars: 200);
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);

        var bounded = ToolOutputBudget.Apply(result, maxBytes, continuationStore: null);

        ShouldTerminateEveryEnvelope(bounded);
        Projection(bounded).ShouldNotMatch(@"-{2}\s*(BEGIN|END)\s+UNTRUSTED\s+WEB\s+CO(?!NTENT)");
    }

    [Fact]
    public void Apply_CutLandingMidFence_ClipsThePartialFenceRatherThanLeavingAFragment()
    {
        var id = UntrustedContentFence.NewId();
        var closing = UntrustedContentFence.Render(closing: true, id);
        // Budget chosen so the prefix ends part-way through the closing fence line.
        var text = UntrustedContentFence.Render(closing: false, id) + "\nbody\n" + closing;
        var cutAt = text.Length - (closing.Length / 2);

        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);
        var bounded = ToolOutputBudget.Apply(result, maxBytes: cutAt, continuationStore: null);

        var projection = Projection(bounded);
        // No half-written fence survives: the fragment is clipped, and a whole one is re-emitted.
        UntrustedContentFence.PartialFenceStart(projection).ShouldBe(-1);
        UntrustedContentFence.MarkerPattern.Matches(projection).Count.ShouldBe(2);
        ShouldTerminateEveryEnvelope(bounded);
    }

    [Fact]
    public void Apply_ResultWithNoEnvelope_IsNotGivenOne()
    {
        // The repair must be a repair, not an unconditional append: a plain oversize payload has no
        // envelope and must not acquire one.
        var result = new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, new string('y', 4_000))]);

        var bounded = ToolOutputBudget.Apply(result, maxBytes: 1_000, continuationStore: null);

        UntrustedContentFence.MarkerPattern.Matches(Projection(bounded)).Count.ShouldBe(0);
    }

    [Fact]
    public void Apply_WithinBudget_LeavesTheEnvelopeByteIdentical()
    {
        var (text, _) = WrappedEnvelope(bodyChars: 50);
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);

        var bounded = ToolOutputBudget.Apply(result, maxBytes: 1_000_000, continuationStore: null);

        // No cut, no repair, no churn - the same instance flows through.
        bounded.ShouldBeSameAs(result);
        Projection(bounded).ShouldBe(text);
    }
}
