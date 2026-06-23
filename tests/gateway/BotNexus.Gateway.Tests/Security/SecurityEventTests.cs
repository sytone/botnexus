using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the <see cref="SecurityEvent"/> canonical security-observability record
/// and its factory helpers (Step 1/5 of the security-event taxonomy, issue #1532 / #1526).
/// </summary>
public sealed class SecurityEventTests
{
    [Fact]
    public void Constructor_DefaultsTimestampToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;

        var evt = new SecurityEvent(
            SecurityEventCategory.Approval,
            "tool.execution.blocked",
            SecurityEventOutcome.Denied,
            SecurityEventSeverity.High);

        var after = DateTimeOffset.UtcNow;

        evt.TimestampUtc.ShouldBeGreaterThanOrEqualTo(before);
        evt.TimestampUtc.ShouldBeLessThanOrEqualTo(after);
        evt.TimestampUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Constructor_RetainsAllProvidedFields()
    {
        var actor = new SecurityEventActor(SecurityActorKind.Operator, "hash-abc");
        var target = new SecurityEventTarget(SecurityTargetKind.Tool, "exec");
        var ts = new DateTimeOffset(2026, 6, 18, 7, 0, 0, TimeSpan.Zero);

        var evt = new SecurityEvent(
            Category: SecurityEventCategory.Tool,
            Action: "tool.execution.requested",
            Outcome: SecurityEventOutcome.Success,
            Severity: SecurityEventSeverity.Info,
            Actor: actor,
            Target: target,
            Policy: SecurityPolicyDecision.Allow,
            Control: SecurityControlFamily.Approval)
        {
            TimestampUtc = ts
        };

        evt.Category.ShouldBe(SecurityEventCategory.Tool);
        evt.Action.ShouldBe("tool.execution.requested");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Severity.ShouldBe(SecurityEventSeverity.Info);
        evt.Actor.ShouldBe(actor);
        evt.Target.ShouldBe(target);
        evt.Policy.ShouldBe(SecurityPolicyDecision.Allow);
        evt.Control.ShouldBe(SecurityControlFamily.Approval);
        evt.TimestampUtc.ShouldBe(ts);
    }

    [Fact]
    public void OptionalFields_DefaultToNullOrNone()
    {
        var evt = new SecurityEvent(
            SecurityEventCategory.Auth,
            "gateway.auth.failed",
            SecurityEventOutcome.Failure,
            SecurityEventSeverity.Medium);

        evt.Actor.ShouldBeNull();
        evt.Target.ShouldBeNull();
        evt.Policy.ShouldBe(SecurityPolicyDecision.None);
        evt.Control.ShouldBe(SecurityControlFamily.None);
    }

    [Fact]
    public void ApprovalDecision_FactoryProducesDeniedApprovalEvent()
    {
        var actor = new SecurityEventActor(SecurityActorKind.ChannelSender, "hash-sender");

        var evt = SecurityEvent.ApprovalDecision(
            action: "exec.approval.denied",
            decision: SecurityPolicyDecision.Deny,
            actor: actor,
            target: new SecurityEventTarget(SecurityTargetKind.Tool, "exec"));

        evt.Category.ShouldBe(SecurityEventCategory.Approval);
        evt.Control.ShouldBe(SecurityControlFamily.Approval);
        evt.Policy.ShouldBe(SecurityPolicyDecision.Deny);
        evt.Outcome.ShouldBe(SecurityEventOutcome.Denied);
        evt.Severity.ShouldBe(SecurityEventSeverity.Medium);
        evt.Actor.ShouldBe(actor);
    }

    [Fact]
    public void ApprovalDecision_AllowDecisionMapsToSuccessOutcome()
    {
        var evt = SecurityEvent.ApprovalDecision(
            action: "exec.approval.granted",
            decision: SecurityPolicyDecision.Allow,
            actor: null,
            target: null);

        evt.Policy.ShouldBe(SecurityPolicyDecision.Allow);
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
    }

    [Fact]
    public void AuthOutcome_FactoryProducesAuthCategoryEvent()
    {
        var actor = new SecurityEventActor(SecurityActorKind.ChannelSender, "hash-x");

        var fail = SecurityEvent.AuthOutcome(
            action: "gateway.auth.failed",
            success: false,
            actor: actor,
            severity: SecurityEventSeverity.High);

        fail.Category.ShouldBe(SecurityEventCategory.Auth);
        fail.Control.ShouldBe(SecurityControlFamily.Auth);
        fail.Outcome.ShouldBe(SecurityEventOutcome.Failure);
        fail.Severity.ShouldBe(SecurityEventSeverity.High);

        var ok = SecurityEvent.AuthOutcome("gateway.auth.succeeded", success: true, actor: actor);
        ok.Outcome.ShouldBe(SecurityEventOutcome.Success);
        ok.Severity.ShouldBe(SecurityEventSeverity.Info);
    }

    [Fact]
    public void Actor_HashedIdIsRetainedVerbatim()
    {
        // The model does not hash; callers pass already-opaque/hashed ids.
        var actor = new SecurityEventActor(SecurityActorKind.Agent, "sha256:deadbeef");
        actor.Kind.ShouldBe(SecurityActorKind.Agent);
        actor.Id.ShouldBe("sha256:deadbeef");
    }
}
