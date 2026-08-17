using BotNexus.Agent.Core.Tools;

namespace BotNexus.Agent.Core.Tests.Tools;

/// <summary>
/// Covers run-scoped taint accumulation: what taints, what does not, and the monotonicity and
/// isolation properties the quarantine decision depends on (#2519).
/// </summary>
public sealed class TurnTaintScopeTests
{
    [Fact]
    public void CurrentState_OutsideAnyScope_IsNull()
    {
        TurnTaintScope.CurrentState.ShouldBeNull();
        TurnTaintScope.IsCurrentTurnTainted.ShouldBeFalse();
    }

    [Fact]
    public void NewScope_StartsClean()
    {
        using var scope = TurnTaintScope.Begin();

        scope.State.IsTainted.ShouldBeFalse();
        TurnTaintScope.IsCurrentTurnTainted.ShouldBeFalse();
    }

    [Fact]
    public void RecordToolResult_LocalSource_DoesNotTaint()
    {
        using var scope = TurnTaintScope.Begin();

        TurnTaintScope.RecordToolResult("read", ToolContentSource.Local);
        TurnTaintScope.RecordToolResult("shell", ToolContentSource.Local);

        TurnTaintScope.IsCurrentTurnTainted.ShouldBeFalse();
        scope.State.Contributors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ToolContentSource.Network)]
    [InlineData(ToolContentSource.Untrusted)]
    public void RecordToolResult_ForeignSource_Taints(string source)
    {
        using var scope = TurnTaintScope.Begin();

        TurnTaintScope.RecordToolResult("web_fetch", source);

        TurnTaintScope.IsCurrentTurnTainted.ShouldBeTrue();
        scope.State.DescribeContributors().ShouldContain("web_fetch");
        scope.State.DescribeContributors().ShouldContain(source);
    }

    /// <summary>The fail-closed case the issue names explicitly: an unclassified tool taints.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-new-source-nobody-added-yet")]
    public void RecordToolResult_UnknownSource_Taints(string? source)
    {
        using var scope = TurnTaintScope.Begin();

        TurnTaintScope.RecordToolResult("mystery_tool", source);

        TurnTaintScope.IsCurrentTurnTainted.ShouldBeTrue();
        // Recorded as the normalised value, never echoed verbatim into the marker.
        scope.State.DescribeContributors().ShouldBe($"mystery_tool ({ToolContentSource.Unknown})");
    }

    /// <summary>
    /// Taint is monotonic: a later clean tool must not launder an earlier foreign read. This is
    /// the property the whole quarantine rests on.
    /// </summary>
    [Fact]
    public void Taint_IsMonotonic_LaterLocalToolDoesNotClearIt()
    {
        using var scope = TurnTaintScope.Begin();

        TurnTaintScope.RecordToolResult("web_fetch", ToolContentSource.Network);
        TurnTaintScope.RecordToolResult("read", ToolContentSource.Local);
        TurnTaintScope.RecordToolResult("shell", ToolContentSource.Local);

        TurnTaintScope.IsCurrentTurnTainted.ShouldBeTrue();
    }

    [Fact]
    public void Contributors_AreDeduplicatedAndOrderedByToolName()
    {
        using var scope = TurnTaintScope.Begin();

        TurnTaintScope.RecordToolResult("web_search", ToolContentSource.Network);
        TurnTaintScope.RecordToolResult("mcp_query", ToolContentSource.Untrusted);
        TurnTaintScope.RecordToolResult("web_search", ToolContentSource.Network);

        scope.State.Contributors.Count.ShouldBe(2);
        scope.State.DescribeContributors()
            .ShouldBe("mcp_query (untrusted), web_search (network)");
    }

    [Fact]
    public void Dispose_RestoresOuterScope_AndDoesNotLeakTaintOutward()
    {
        using (var outer = TurnTaintScope.Begin())
        {
            using (var inner = TurnTaintScope.Begin())
            {
                TurnTaintScope.RecordToolResult("web_fetch", ToolContentSource.Network);
                TurnTaintScope.IsCurrentTurnTainted.ShouldBeTrue();
            }

            // The nested run's taint must not bleed into the parent...
            TurnTaintScope.IsCurrentTurnTainted.ShouldBeFalse();
            outer.State.IsTainted.ShouldBeFalse();
        }

        TurnTaintScope.CurrentState.ShouldBeNull();
    }

    [Fact]
    public void Dispose_DoesNotEraseOuterTaint()
    {
        using var outer = TurnTaintScope.Begin();
        TurnTaintScope.RecordToolResult("web_fetch", ToolContentSource.Network);

        using (TurnTaintScope.Begin())
        {
            TurnTaintScope.IsCurrentTurnTainted.ShouldBeFalse();
        }

        // ...and closing a clean nested run must not clear the parent's taint either.
        TurnTaintScope.IsCurrentTurnTainted.ShouldBeTrue();
    }

    [Fact]
    public async Task Taint_RecordedConcurrently_IsVisibleToTheWholeScope()
    {
        using var scope = TurnTaintScope.Begin();

        // Mirrors parallel tool dispatch: the state object is shared by reference and the
        // AsyncLocal flows into each task.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            TurnTaintScope.RecordToolResult($"tool_{i % 4}", i % 2 == 0
                ? ToolContentSource.Network
                : ToolContentSource.Local))));

        TurnTaintScope.IsCurrentTurnTainted.ShouldBeTrue();
        scope.State.Contributors.Count.ShouldBe(2);
    }

    [Fact]
    public void DescribeContributors_CleanScope_ReportsNoContributors()
    {
        using var scope = TurnTaintScope.Begin();

        scope.State.DescribeContributors().ShouldBe("no recorded contributors");
    }
}
