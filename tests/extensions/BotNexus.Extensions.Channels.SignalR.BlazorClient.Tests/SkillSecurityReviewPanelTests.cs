using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SkillSecurityReviewPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _client = Substitute.For<IGatewayRestClient>();

    public SkillSecurityReviewPanelTests()
    {
        _ctx.Services.AddSingleton(_client);
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>())
            .Returns(new SkillSecurityFindingsDto([], false));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Empty_state_and_refresh_are_rendered()
    {
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No unresolved critical findings"));
        cut.Find("[data-testid='security-refresh']").Click();
        _client.Received(2).GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Pending_request_renders_loading_state()
    {
        var pending = new TaskCompletionSource<SkillSecurityFindingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        cut.Markup.ShouldContain("Scanning shared skills");
    }

    [Fact]
    public void Attacker_controlled_fields_are_rendered_as_text()
    {
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>()).Returns(Response(
            CompleteFinding() with { Skill = "<img src=x onerror=alert(1)>", RuleId = "<script>alert(1)</script>" }));
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("&lt;img src=x onerror=alert(1)&gt;"));
        cut.Markup.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        cut.Markup.ShouldNotContain("<script>alert(1)</script>");
    }

    [Fact]
    public void Complete_finding_requires_reason_and_confirmation_before_submit()
    {
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>()).Returns(Response(CompleteFinding()));
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("dangerous-exec"));
        cut.Find("[data-testid='security-acknowledge']").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("[data-testid='security-reason']").Input("Reviewed in incident 42");
        cut.Find("[data-testid='security-confirm']").Change(true);
        cut.Find("[data-testid='security-acknowledge']").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void Incomplete_finding_cannot_be_acknowledged()
    {
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>()).Returns(Response(
            CompleteFinding() with { IsComplete = false, FileSha256 = null, RevisionId = null, MissingReason = "file-hash-unavailable" }));
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Evidence incomplete"));
        cut.FindAll("[data-testid='security-acknowledge']").ShouldBeEmpty();
    }

    [Fact]
    public void Unauthorized_and_api_failures_are_distinct()
    {
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<SkillSecurityFindingsDto?>>(_ => throw new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden));
        var unauthorized = _ctx.Render<SkillsSecurityReviewPanel>();
        unauthorized.WaitForAssertion(() => unauthorized.Markup.ShouldContain("Administrator access is required"));

        _client.ClearReceivedCalls();
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<SkillSecurityFindingsDto?>>(_ => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));
        var failed = _ctx.Render<SkillsSecurityReviewPanel>();
        failed.WaitForAssertion(() => failed.Markup.ShouldContain("Unable to scan shared skills"));
    }

    [Fact]
    public void Disposal_cancels_in_flight_scan()
    {
        CancellationToken observed = default;
        var pending = new TaskCompletionSource<SkillSecurityFindingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            return pending.Task;
        });
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        cut.WaitForAssertion(() => observed.CanBeCanceled.ShouldBeTrue());
        cut.Instance.Dispose();
        cut.WaitForAssertion(() => observed.IsCancellationRequested.ShouldBeTrue());
    }

    [Fact]
    public void Successful_acknowledgement_rescans_and_clears_finding()
    {
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>())
            .Returns(Response(CompleteFinding()), new SkillSecurityFindingsDto([], false));
        _client.AcknowledgeSkillSecurityFindingAsync(Arg.Any<SkillSecurityAcknowledgementDto>(), Arg.Any<CancellationToken>())
            .Returns(new SkillSecurityAcknowledgementResult(HttpStatusCode.Created, null));
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        Submit(cut);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No unresolved critical findings"));
        _client.Received(1).AcknowledgeSkillSecurityFindingAsync(
            Arg.Is<SkillSecurityAcknowledgementDto>(r => r.Confirmed && r.Reason == "Reviewed in incident 42" && r.FindingId == CompleteFinding().ScannerFindingId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Stale_conflict_keeps_finding_and_requests_rescan()
    {
        _client.GetSkillSecurityFindingsAsync(Arg.Any<CancellationToken>()).Returns(Response(CompleteFinding()));
        _client.AcknowledgeSkillSecurityFindingAsync(Arg.Any<SkillSecurityAcknowledgementDto>(), Arg.Any<CancellationToken>())
            .Returns(new SkillSecurityAcknowledgementResult(HttpStatusCode.Conflict, "stale"));
        var cut = _ctx.Render<SkillsSecurityReviewPanel>();
        Submit(cut);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Finding changed. Refresh and review the current evidence"));
        cut.Markup.ShouldContain("dangerous-exec");
    }

    private static void Submit(IRenderedComponent<SkillsSecurityReviewPanel> cut)
    {
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("dangerous-exec"));
        cut.Find("[data-testid='security-reason']").Input("Reviewed in incident 42");
        cut.Find("[data-testid='security-confirm']").Change(true);
        cut.Find("[data-testid='security-acknowledge']").Click();
    }

    private static SkillSecurityFindingsDto Response(SkillSecurityFindingDto finding) => new([finding], false);

    private static SkillSecurityFindingDto CompleteFinding() => new(
        "shelling-skill", "Global", "dangerous-exec", "Critical", "scripts/run.mjs", 2,
        "skill-security-scanner/1", new string('a', 64), new string('b', 64), new string('c', 64), true, null);
}
