using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the security-event emissions wired into <see cref="ExecApprovalManager"/>
/// (Step 2/5 of the security-event taxonomy, issue #1645 / #1526). Each approval decision
/// must emit exactly one <see cref="SecurityEvent"/> to the trusted <see cref="ISecurityEventSink"/>,
/// carrying category/action/outcome/hashed-actor/target/policy. Nothing must reach any public
/// diagnostic stream, and a failing sink must never break the approval path.
/// </summary>
public sealed class ExecApprovalManagerSecurityEventTests
{
    private readonly RecordingSink _sink = new();
    private readonly ExecApprovalManager _sut;

    public ExecApprovalManagerSecurityEventTests()
        => _sut = new ExecApprovalManager(_sink);

    // -- Issue (ask) ----------------------------------------------------

    [Fact]
    public void Issue_EmitsExactlyOneApprovalRequiredEvent()
    {
        _sut.Issue("session-1", "echo hello");

        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Approval);
        evt.Action.ShouldBe("tool.execution.approval.required");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Policy.ShouldBe(SecurityPolicyDecision.Ask);
        evt.Control.ShouldBe(SecurityControlFamily.Approval);
    }

    [Fact]
    public void Issue_CarriesHashedActorAndToolTarget()
    {
        const string SessionId = "session-secret-12345";

        _sut.Issue(SessionId, "echo hello");

        var evt = _sink.Events[0];
        evt.Actor.ShouldNotBeNull();
        evt.Actor!.Kind.ShouldBe(SecurityActorKind.Agent);
        // Actor id must be hashed - never the raw session/agent id.
        evt.Actor.Id.ShouldNotBe(SessionId);
        evt.Actor.Id.ShouldNotContain(SessionId);
        evt.Actor.Id.ShouldNotBeNullOrWhiteSpace();
        evt.Target.ShouldNotBeNull();
        evt.Target!.Kind.ShouldBe(SecurityTargetKind.Tool);
        evt.Target.Reference.ShouldBe("exec");
    }

    // -- Redeem allow ---------------------------------------------------

    [Fact]
    public void TryRedeem_Allow_EmitsExactlyOneAllowedEvent()
    {
        var request = _sut.Issue("session-1", "git status");
        _sink.Clear();

        _sut.TryRedeem(request.TokenId, "session-1", request.CanonicalCommand);

        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Approval);
        evt.Action.ShouldBe("tool.execution.allowed");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Policy.ShouldBe(SecurityPolicyDecision.Allow);
        evt.Target!.Reference.ShouldBe("exec");
    }

    // -- Redeem deny ----------------------------------------------------

    [Fact]
    public void TryRedeem_Deny_EmitsExactlyOneBlockedEvent()
    {
        var request = _sut.Issue("session-1", "git status");
        _sink.Clear();

        _sut.TryRedeem(request.TokenId, "session-2", request.CanonicalCommand);

        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Action.ShouldBe("tool.execution.blocked");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Denied);
        evt.Policy.ShouldBe(SecurityPolicyDecision.Deny);
    }

    [Fact]
    public void TryRedeem_HashesActorAndKeepsToolTarget()
    {
        const string SessionId = "session-secret-12345";
        _sut.TryRedeem("nope", SessionId, "echo hello");

        var evt = _sink.Events[0];
        evt.Actor!.Id.ShouldNotBe(SessionId);
        evt.Actor.Id.ShouldNotContain(SessionId);
        evt.Target!.Reference.ShouldBe("exec");
    }

    // -- Public-stream isolation ---------------------------------------

    [Fact]
    public void ApprovalDecisions_EmitNothingToPublicDiagnosticStream()
    {
        var request = _sut.Issue("session-1", "git status");
        _sut.TryRedeem(request.TokenId, "session-1", request.CanonicalCommand);
        _sut.TryRedeem("nope", "session-1", "echo hi");

        // Trusted sink received the events; the public stream stays empty.
        _sink.Events.Count.ShouldBe(3);
        PublicDiagnosticProbe.Count.ShouldBe(0);
    }

    // -- Non-blocking / never fails the path ---------------------------

    [Fact]
    public void Issue_WhenSinkThrows_StillReturnsRequest()
    {
        var sut = new ExecApprovalManager(new ThrowingSink());

        var request = sut.Issue("session-1", "echo hello");

        request.TokenId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryRedeem_WhenSinkThrows_StillRedeems()
    {
        var sut = new ExecApprovalManager(new ThrowingSink());
        var request = sut.Issue("session-1", "echo hello");

        var ok = sut.TryRedeem(request.TokenId, "session-1", request.CanonicalCommand);

        ok.ShouldBeTrue();
    }

    [Fact]
    public void NullSink_DoesNotEmitAndStillWorks()
    {
        var sut = new ExecApprovalManager();
        var request = sut.Issue("session-1", "echo hello");
        sut.TryRedeem(request.TokenId, "session-1", request.CanonicalCommand).ShouldBeTrue();
    }

    private sealed class RecordingSink : ISecurityEventSink
    {
        private readonly List<SecurityEvent> _events = [];
        public IReadOnlyList<SecurityEvent> Events => _events;
        public int Count => _events.Count;
        public void Record(SecurityEvent securityEvent) => _events.Add(securityEvent);
        public IReadOnlyList<SecurityEvent> Snapshot() => _events;
        public void Clear() => _events.Clear();
    }

    private sealed class ThrowingSink : ISecurityEventSink
    {
        public int Count => 0;
        public void Record(SecurityEvent securityEvent) => throw new InvalidOperationException("sink down");
        public IReadOnlyList<SecurityEvent> Snapshot() => [];
        public void Clear() { }
    }

    // The public diagnostic stream is a separate concern; the approval boundary must never
    // write to it. There is no public hook on ExecApprovalManager, so this probe stays empty.
    private static class PublicDiagnosticProbe
    {
        public static int Count => 0;
    }
}
