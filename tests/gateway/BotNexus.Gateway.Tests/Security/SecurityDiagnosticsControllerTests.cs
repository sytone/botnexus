using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the trusted-only security-event read path (Step 5/5 of the security-event
/// taxonomy, issue #1648 / #1526). The read path exposes buffered <see cref="SecurityEvent"/>
/// records to administrators only, via <see cref="SecurityDiagnosticsController"/>, and is
/// deliberately a SEPARATE surface from the public activity/diagnostic stream:
/// <list type="bullet">
///   <item>An admin caller can read the buffered security events.</item>
///   <item>A non-admin caller is denied with 403 Forbidden.</item>
///   <item>A request with no resolved caller identity is denied (fail-closed).</item>
///   <item>No security event is ever published onto the public <see cref="IActivityBroadcaster"/> stream.</item>
/// </list>
/// </summary>
public sealed class SecurityDiagnosticsControllerTests
{
    private const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    // -- Happy path: admin can read --------------------------------------------------

    [Fact]
    public void GetSecurityEvents_WhenCallerIsAdmin_ReturnsBufferedEvents()
    {
        var sink = new RingBufferSecurityEventSink();
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Approval,
            "exec.command.denied",
            SecurityEventOutcome.Denied,
            SecurityEventSeverity.Medium,
            Policy: SecurityPolicyDecision.Deny,
            Control: SecurityControlFamily.Approval));
        var controller = new SecurityDiagnosticsController(sink)
        {
            ControllerContext = CreateControllerContext(isAdmin: true)
        };

        var result = controller.GetSecurityEvents();

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<SecurityEventsResponse>();
        response.Total.ShouldBe(1);
        response.Events.Count.ShouldBe(1);
        response.Events[0].Action.ShouldBe("exec.command.denied");
        response.Events[0].Category.ShouldBe(nameof(SecurityEventCategory.Approval));
        response.Events[0].Outcome.ShouldBe(nameof(SecurityEventOutcome.Denied));
    }

    [Fact]
    public void GetSecurityEvents_WhenAdmin_ReturnsEventsMostRecentFirst()
    {
        var sink = new RingBufferSecurityEventSink();
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Auth, "gateway.auth.failed", SecurityEventOutcome.Failure, SecurityEventSeverity.High));
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Tool, "tool.execution.blocked", SecurityEventOutcome.Denied, SecurityEventSeverity.Medium));
        var controller = new SecurityDiagnosticsController(sink)
        {
            ControllerContext = CreateControllerContext(isAdmin: true)
        };

        var result = controller.GetSecurityEvents();

        var response = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<SecurityEventsResponse>();
        response.Total.ShouldBe(2);
        // Snapshot is most-recent first: the second recorded event leads.
        response.Events[0].Action.ShouldBe("tool.execution.blocked");
        response.Events[1].Action.ShouldBe("gateway.auth.failed");
    }

    // -- Sad path: authorization gate ------------------------------------------------

    [Fact]
    public void GetSecurityEvents_WhenCallerNotAdmin_ReturnsForbidden()
    {
        var sink = new RingBufferSecurityEventSink();
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Auth, "gateway.auth.failed", SecurityEventOutcome.Failure, SecurityEventSeverity.High));
        var controller = new SecurityDiagnosticsController(sink)
        {
            ControllerContext = CreateControllerContext(isAdmin: false)
        };

        var result = controller.GetSecurityEvents();

        result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void GetSecurityEvents_WhenNoCallerIdentity_ReturnsForbidden()
    {
        var sink = new RingBufferSecurityEventSink();
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Auth, "gateway.auth.failed", SecurityEventOutcome.Failure, SecurityEventSeverity.High));
        var httpContext = new DefaultHttpContext(); // no caller identity in Items -> fail closed
        var controller = new SecurityDiagnosticsController(sink)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var result = controller.GetSecurityEvents();

        result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void GetSecurityEvents_WhenCallerNotAdmin_DoesNotLeakEventPayload()
    {
        var sink = new RingBufferSecurityEventSink();
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Secret, "secret.reference.accessed", SecurityEventOutcome.Success, SecurityEventSeverity.Low));
        var controller = new SecurityDiagnosticsController(sink)
        {
            ControllerContext = CreateControllerContext(isAdmin: false)
        };

        var result = controller.GetSecurityEvents();

        // A denied caller must never receive the buffered events in the body.
        var denied = result.ShouldBeOfType<ObjectResult>();
        denied.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (denied.Value is SecurityEventsResponse).ShouldBeFalse();
    }

    // -- Graceful when sink not enabled ----------------------------------------------

    [Fact]
    public void GetSecurityEvents_WhenSinkNull_ReturnsNotFound()
    {
        var controller = new SecurityDiagnosticsController(securityEvents: null)
        {
            ControllerContext = CreateControllerContext(isAdmin: true)
        };

        var result = controller.GetSecurityEvents();

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // -- Never on the public activity/SignalR stream ---------------------------------

    [Fact]
    public void SecurityEventReadPath_DoesNotPublishToActivityBroadcaster()
    {
        var broadcaster = new RecordingActivityBroadcaster();
        var sink = new RingBufferSecurityEventSink();
        sink.Record(new SecurityEvent(
            SecurityEventCategory.Approval, "exec.command.denied", SecurityEventOutcome.Denied, SecurityEventSeverity.Medium));
        var controller = new SecurityDiagnosticsController(sink)
        {
            ControllerContext = CreateControllerContext(isAdmin: true)
        };

        _ = controller.GetSecurityEvents();

        // The trusted read path must NEVER fan a security event out onto the public stream.
        broadcaster.PublishCount.ShouldBe(0);
        // And the controller does not depend on the broadcaster at all.
        typeof(SecurityDiagnosticsController)
            .GetConstructors()
            .ShouldAllBe(c => c.GetParameters().All(p => p.ParameterType != typeof(IActivityBroadcaster)));
    }

    private static ControllerContext CreateControllerContext(bool isAdmin)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CallerIdentityItemKey] = new GatewayCallerIdentity
        {
            CallerId = "caller-1",
            IsAdmin = isAdmin
        };
        return new ControllerContext { HttpContext = httpContext };
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            PublishCount++;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
