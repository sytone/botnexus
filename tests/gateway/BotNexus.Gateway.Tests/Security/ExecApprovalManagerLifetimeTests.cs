using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Lifetime tests for <see cref="ExecApprovalManager"/> (issue #2746): pending approvals expire
/// after a bounded TTL and the pending registry has a hard cap, so an approval that is never
/// answered cannot retain its decoded command payload for the lifetime of the process.
/// </summary>
public sealed class ExecApprovalManagerLifetimeTests
{
    private const string SessionId = "session-1";

    /// <summary>
    /// AC3 - the discrimination, not just the rejection: a token issued before the TTL elapsed is
    /// refused by <see cref="ExecApprovalManager.TryRedeem"/>, while a token issued after the clock
    /// moved (still inside the TTL at redemption time) is accepted in the very same test.
    /// </summary>
    [Fact]
    public void TryRedeem_RejectsExpiredToken_ButAcceptsStillFreshTokenIssuedInSameTest()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var sut = new ExecApprovalManager(
            timeProvider: clock,
            pendingTtl: TimeSpan.FromMinutes(5));

        var stale = sut.Issue(SessionId, "echo stale");

        // Issue the second token before either has expired, so the opportunistic prune on Issue
        // cannot be what removes the stale entry - the expiry check in TryRedeem must be.
        clock.Advance(TimeSpan.FromMinutes(4));
        var fresh = sut.Issue(SessionId, "echo fresh");

        // Now advance so the first token is past its TTL while the second is still inside its own.
        clock.Advance(TimeSpan.FromMinutes(2));

        sut.TryRedeem(stale.TokenId, SessionId, stale.CanonicalCommand)
            .ShouldBeFalse("an approval older than the TTL must never be redeemable");
        sut.TryRedeem(fresh.TokenId, SessionId, fresh.CanonicalCommand)
            .ShouldBeTrue("a token still inside the TTL must remain redeemable");
    }

    /// <summary>
    /// AC1 - a token redeemed exactly at the TTL boundary (not yet older than the TTL) is still
    /// valid; only entries strictly older than the TTL expire.
    /// </summary>
    [Fact]
    public void TryRedeem_TokenAtExactTtlBoundary_IsStillRedeemable()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var sut = new ExecApprovalManager(
            timeProvider: clock,
            pendingTtl: TimeSpan.FromMinutes(5));

        var request = sut.Issue(SessionId, "echo boundary");
        clock.Advance(TimeSpan.FromMinutes(5));

        sut.TryRedeem(request.TokenId, SessionId, request.CanonicalCommand).ShouldBeTrue();
    }

    /// <summary>
    /// AC4 - the registry never grows past its configured cap across more Issue calls than the cap,
    /// and the overflowing calls fail rather than inserting.
    /// </summary>
    [Fact]
    public void Issue_BeyondCap_DoesNotGrowRegistryPastMaximum()
    {
        const int Cap = 8;
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var sut = new ExecApprovalManager(
            timeProvider: clock,
            pendingTtl: TimeSpan.FromHours(1),
            maxPending: Cap);

        var issued = 0;
        var refused = 0;
        for (var i = 0; i < Cap * 4; i++)
        {
            try
            {
                sut.Issue(SessionId, $"echo command-{i}");
                issued++;
            }
            catch (ExecApprovalCapacityExceededException)
            {
                refused++;
            }
        }

        issued.ShouldBe(Cap);
        refused.ShouldBe(Cap * 3);
        sut.PendingCount.ShouldBe(Cap);
    }

    /// <summary>
    /// AC2 - the capacity refusal is observable through the existing trusted security-event sink
    /// as a Deny decision, not a silent failure.
    /// </summary>
    [Fact]
    public void Issue_BeyondCap_EmitsDenyEventToSecurityEventSink()
    {
        const int Cap = 2;
        var sink = new CountingSink();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var sut = new ExecApprovalManager(
            sink,
            timeProvider: clock,
            pendingTtl: TimeSpan.FromHours(1),
            maxPending: Cap);

        sut.Issue(SessionId, "echo one");
        sut.Issue(SessionId, "echo two");
        Should.Throw<ExecApprovalCapacityExceededException>(
            () => sut.Issue(SessionId, "echo three"));

        var denies = sink.Events.Where(e => e.Policy == SecurityPolicyDecision.Deny).ToList();
        denies.Count.ShouldBe(1);
        denies[0].Action.ShouldBe("tool.execution.approval.refused");
        denies[0].Target.ShouldNotBeNull();
        denies[0].Target!.Reference.ShouldBe("exec");
    }

    /// <summary>
    /// Pruning is opportunistic on Issue (no timer): once the expired entries age out, capacity
    /// is reclaimed and issuance resumes without any background sweep.
    /// </summary>
    [Fact]
    public void Issue_PrunesExpiredEntries_ReclaimingCapacityWithoutTimer()
    {
        const int Cap = 3;
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var sut = new ExecApprovalManager(
            timeProvider: clock,
            pendingTtl: TimeSpan.FromMinutes(5),
            maxPending: Cap);

        for (var i = 0; i < Cap; i++)
            sut.Issue(SessionId, $"echo abandoned-{i}");

        sut.PendingCount.ShouldBe(Cap);
        Should.Throw<ExecApprovalCapacityExceededException>(() => sut.Issue(SessionId, "echo blocked"));

        clock.Advance(TimeSpan.FromMinutes(6));

        var request = sut.Issue(SessionId, "echo after-expiry");
        sut.PendingCount.ShouldBe(1);
        sut.TryRedeem(request.TokenId, SessionId, request.CanonicalCommand).ShouldBeTrue();
    }

    /// <summary>
    /// A minimal mutable <see cref="TimeProvider"/> test double so expiry can be driven
    /// deterministically without wall-clock waits.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    /// <summary>Collects every event recorded at the trusted sink.</summary>
    private sealed class CountingSink : ISecurityEventSink
    {
        private readonly List<SecurityEvent> _events = [];

        public IReadOnlyList<SecurityEvent> Events => _events;

        public void Record(SecurityEvent securityEvent) => _events.Add(securityEvent);

        public IReadOnlyList<SecurityEvent> Snapshot() => _events.AsReadOnly();

        public int Count => _events.Count;

        public void Clear() => _events.Clear();
    }
}
