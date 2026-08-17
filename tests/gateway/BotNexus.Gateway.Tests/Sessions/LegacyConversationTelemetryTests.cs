using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2311 audit gate: <see cref="LegacyConversationResolver"/> exists solely to service a
/// completed one-time migration (#615, closed 2026-05-29). Before it can be deleted we
/// must be able to prove from a running environment whether anything still reaches it.
/// Today every session store constructs the resolver with <c>logger: null</c>, so live
/// activations are completely invisible. These tests pin the counter-based telemetry that
/// closes that gap: every resolve/create/bind is attributed to the call path that caused
/// it, so an operator can read a single snapshot and decide whether the shim is dead.
/// </summary>
/// <remarks>
/// #3227: these tests previously snapshotted the <b>process-wide</b> statics before and
/// after their own activity and asserted an exact delta. That assertion silently also
/// asserted "no other code in this process incremented the same counter in between",
/// which xUnit's parallel collections make false at random - it produced inherited red
/// gates on branches containing no .NET at all. Each test now asserts against a
/// <see cref="LegacyConversationTelemetryScope"/>, an accumulator flowed on
/// <see cref="System.Threading.AsyncLocal{T}"/>, so concurrent increments from any other
/// flow are structurally incapable of being counted. No assertion was relaxed: every
/// exact-delta expectation is preserved verbatim, it is simply measured against a seam the
/// test controls.
/// </remarks>
public sealed class LegacyConversationTelemetryTests
{
    private static LegacyConversationResolver NewResolver(out InMemoryConversationStore store)
    {
        store = new InMemoryConversationStore();
        return new LegacyConversationResolver(store);
    }

    [Fact]
    public async Task Resolve_RecordsActivation_AttributedToCallPath()
    {
        using var telemetry = LegacyConversationTelemetry.BeginScope();
        var resolver = NewResolver(out _);

        await resolver.ResolveAsync(AgentId.From("tele-a"), LegacyResolveReason.SaveTimeStamp);

        var observed = telemetry.Snapshot();
        observed.TotalResolves.ShouldBe(1);
        observed.SaveTimeStampResolves.ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_WhenConversationDoesNotExist_RecordsCreate()
    {
        using var telemetry = LegacyConversationTelemetry.BeginScope();
        var resolver = NewResolver(out _);

        await resolver.ResolveAsync(AgentId.From("tele-b"), LegacyResolveReason.LoadTimeBackfill);

        var observed = telemetry.Snapshot();
        observed.TotalCreates.ShouldBe(1);
        observed.LoadTimeBackfillResolves.ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_WhenConversationAlreadyExists_DoesNotRecordAnotherCreate()
    {
        var resolver = NewResolver(out _);
        var agentId = AgentId.From("tele-c");
        await resolver.ResolveAsync(agentId, LegacyResolveReason.StartupMigration);

        using var telemetry = LegacyConversationTelemetry.BeginScope();
        await resolver.ResolveAsync(agentId, LegacyResolveReason.StartupMigration);
        var observed = telemetry.Snapshot();

        observed.TotalCreates.ShouldBe(0);
        observed.TotalResolves.ShouldBe(1);
    }

    [Fact]
    public async Task Bind_RecordsActivation()
    {
        var resolver = NewResolver(out _);
        var conversation = await resolver.ResolveAsync(AgentId.From("tele-d"), LegacyResolveReason.StartupMigration);

        using var telemetry = LegacyConversationTelemetry.BeginScope();
        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-d"));

        telemetry.Snapshot().TotalBinds.ShouldBe(1);
    }

    [Fact]
    public async Task Bind_WhenPointerAlreadySet_DoesNotRecordAnotherBind()
    {
        var resolver = NewResolver(out _);
        var conversation = await resolver.ResolveAsync(AgentId.From("tele-e"), LegacyResolveReason.StartupMigration);
        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-e-1"));

        using var telemetry = LegacyConversationTelemetry.BeginScope();
        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-e-2"));

        telemetry.Snapshot().TotalBinds.ShouldBe(0);
    }

    [Fact]
    public async Task Snapshot_ReportsWhetherShimIsStillLive()
    {
        // The whole point of the audit gate: an operator reads HasActivity to decide
        // whether the shim can be deleted. A process that did touch the resolver must
        // report true.
        using var telemetry = LegacyConversationTelemetry.BeginScope();
        var resolver = NewResolver(out _);
        await resolver.ResolveAsync(AgentId.From("tele-f"), LegacyResolveReason.LoadTimeBackfill);

        telemetry.Snapshot().HasActivity.ShouldBeTrue();

        // The process-wide reading remains the production oracle and must agree: a scope
        // is an additional attribution channel, not a replacement that swallows the
        // activation. Asserted as "at least", because by construction we do not control
        // what else in the process has touched the statics - which is precisely why the
        // exact-delta assertions above no longer read them.
        LegacyConversationTelemetry.Snapshot().HasActivity.ShouldBeTrue();
    }

    [Fact]
    public void Snapshot_OnFreshCounters_ReportsNoActivity()
    {
        var empty = default(LegacyConversationTelemetrySnapshot);

        empty.HasActivity.ShouldBeFalse();
        empty.TotalResolves.ShouldBe(0);
    }

    [Fact]
    public async Task Resolve_DefaultReason_IsUnspecified()
    {
        // Callers that have not yet been attributed still get counted, so the total is
        // never under-reported - they just land in the Unspecified bucket.
        using var telemetry = LegacyConversationTelemetry.BeginScope();
        var resolver = NewResolver(out _);

        await resolver.ResolveAsync(AgentId.From("tele-g"));

        telemetry.Snapshot().UnspecifiedResolves.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------------
    // #3227 non-vacuity. These cases prove the seam actually isolates, rather than merely
    // compiling. Without them the fix would be indistinguishable from a cosmetic rewrite.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Scope_IsNotInflatedByConcurrentActivationOnAnotherFlow()
    {
        // AC5, forward direction: artificially raise the shared counter from a concurrent
        // path in the exact window that used to redden Bind_RecordsActivation. Under the
        // old before/after-static shape this interleaving produced a delta of 2; the
        // scope must still read exactly 1.
        var resolver = NewResolver(out _);
        var conversation = await resolver.ResolveAsync(AgentId.From("tele-h"), LegacyResolveReason.StartupMigration);

        using var telemetry = LegacyConversationTelemetry.BeginScope();

        var beforeStatics = LegacyConversationTelemetry.Snapshot();

        // SuppressFlow is what makes this interference faithful. A bare Task.Run would
        // INHERIT the ambient AsyncLocal scope - it is a child of this flow, not a
        // stranger - and would be counted, correctly. A sibling xUnit test is not a
        // child of this flow, so the ExecutionContext must be suppressed to model it.
        await RunOnUnrelatedFlowAsync(() =>
        {
            LegacyConversationTelemetry.RecordBind();
            LegacyConversationTelemetry.RecordResolve(LegacyResolveReason.SaveTimeStamp);
            LegacyConversationTelemetry.RecordCreate();
        });

        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-h"));

        var observed = telemetry.Snapshot();
        observed.TotalBinds.ShouldBe(1);
        observed.TotalResolves.ShouldBe(0);
        observed.TotalCreates.ShouldBe(0);
        observed.SaveTimeStampResolves.ShouldBe(0);

        // And the interference genuinely did reach the statics - otherwise the case above
        // would pass vacuously against a no-op. This is the assertion that would fail if
        // the fix were reverted to reading the statics.
        var afterStatics = LegacyConversationTelemetry.Snapshot();
        (afterStatics.TotalBinds - beforeStatics.TotalBinds).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Scope_DoesNotSuppressTheProcessWideCounters()
    {
        // The production audit question is process-wide; a scope must add a channel, not
        // divert one. If BeginScope ever short-circuited the statics, the #2311 gate
        // would silently start reporting "shim is dead" while it was live.
        var before = LegacyConversationTelemetry.Snapshot();

        using (var telemetry = LegacyConversationTelemetry.BeginScope())
        {
            LegacyConversationTelemetry.RecordBind();
            telemetry.Snapshot().TotalBinds.ShouldBe(1);
        }

        var after = LegacyConversationTelemetry.Snapshot();
        (after.TotalBinds - before.TotalBinds).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Scope_AfterDispose_NoLongerCollects()
    {
        var telemetry = LegacyConversationTelemetry.BeginScope();
        telemetry.Dispose();

        LegacyConversationTelemetry.RecordBind();

        telemetry.Snapshot().TotalBinds.ShouldBe(0);
    }

    [Fact]
    public void Scope_Nested_CreditsInnermostAndEveryAncestor()
    {
        using var outer = LegacyConversationTelemetry.BeginScope();

        using (var inner = LegacyConversationTelemetry.BeginScope())
        {
            LegacyConversationTelemetry.RecordBind();
            inner.Snapshot().TotalBinds.ShouldBe(1);
        }

        // Outer sees the inner scope's activity, and collection resumes for the outer
        // scope once the inner one is disposed.
        outer.Snapshot().TotalBinds.ShouldBe(1);

        LegacyConversationTelemetry.RecordBind();
        outer.Snapshot().TotalBinds.ShouldBe(2);
    }

    [Fact]
    public async Task Scope_CountsActivityRaisedOnFlowsItStarted()
    {
        // The isolation must not overshoot: work the test itself awaits - including work
        // that hops threads - is still the test's own activity and must be counted.
        using var telemetry = LegacyConversationTelemetry.BeginScope();

        await Task.Run(async () =>
        {
            var resolver = NewResolver(out _);
            await resolver.ResolveAsync(AgentId.From("tele-i"), LegacyResolveReason.LoadTimeBackfill);
        });

        var observed = telemetry.Snapshot();
        observed.TotalResolves.ShouldBe(1);
        observed.LoadTimeBackfillResolves.ShouldBe(1);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a flow that did not inherit the caller's
    /// <see cref="System.Threading.ExecutionContext"/>, modelling a concurrently running
    /// sibling test rather than a child task of this one.
    /// </summary>
    private static async Task RunOnUnrelatedFlowAsync(Action work)
    {
        Task task;
        using (System.Threading.ExecutionContext.SuppressFlow())
        {
            task = Task.Run(work);
        }

        await task;
    }
}
