using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

public sealed class WebhooksControllerTests
{
    private readonly IWebhookRegistrationStore _registrations = Substitute.For<IWebhookRegistrationStore>();
    private readonly IWebhookRunStore _runs = Substitute.For<IWebhookRunStore>();
    private readonly WebhooksController _sut;

    public WebhooksControllerTests()
    {
        _sut = new WebhooksController(
            _registrations,
            _runs,
            NullLogger<WebhooksController>.Instance);

        // Minimal HTTP context so Request.Scheme/Host resolves
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        _sut.ControllerContext.HttpContext.Request.Scheme = "https";
        _sut.ControllerContext.HttpContext.Request.Host = new HostString("gateway.test");
    }

    private static WebhookRegistration MakeRegistration(string? id = null, string? agentId = null) =>
        new()
        {
            Id = WebhookId.From(id ?? "wh_abc123def456789"),
            Label = "Test Webhook",
            AgentId = AgentId.From(agentId ?? "farnsworth"),
            Secret = "whsec_testsecret",
            DefaultResponseMode = WebhookResponseMode.Async,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_Returns201WithSecret()
    {
        var reg = MakeRegistration();
        _registrations.CreateAsync(Arg.Any<WebhookRegistration>(), Arg.Any<CancellationToken>())
            .Returns(reg);

        var result = await _sut.Create(
            new CreateWebhookRegistrationRequest("farnsworth", "Test Webhook"),
            CancellationToken.None);

        var created = result.Result.ShouldBeOfType<CreatedAtActionResult>();
        var response = created.Value.ShouldBeOfType<WebhookRegistrationResponse>();
        response.AgentId.ShouldBe("farnsworth");
        response.Secret.ShouldNotBeNullOrWhiteSpace("secret must be returned on create");
        response.Url.ShouldContain("wh_abc123def456789");
    }

    [Fact]
    public async Task Create_MissingAgentId_Returns400()
    {
        var result = await _sut.Create(
            new CreateWebhookRegistrationRequest("", "label"),
            CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_MissingLabel_Returns400()
    {
        var result = await _sut.Create(
            new CreateWebhookRegistrationRequest("farnsworth", ""),
            CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // ── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ExistingRegistration_Returns200WithoutSecret()
    {
        var reg = MakeRegistration();
        _registrations.GetAsync(reg.Id, Arg.Any<CancellationToken>()).Returns(reg);

        var result = await _sut.Get(reg.Id.Value, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<WebhookRegistrationResponse>();
        response.Secret.ShouldBeNull("secret must not be exposed after initial create");
        response.WebhookId.ShouldBe(reg.Id.Value);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        // No setup — substitute returns null by default for reference return types.
        // The store returns null for any WebhookId not explicitly configured.
        var result = await _sut.Get("wh_doesnotexist0000", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_NoFilter_ReturnsAllRegistrations()
    {
        var regs = new[] { MakeRegistration("wh_aaa"), MakeRegistration("wh_bbb") };
        _registrations.ListAsync(Arg.Is<AgentId?>(_ => true), Arg.Any<CancellationToken>()).Returns(regs);

        var result = await _sut.List(null, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeAssignableTo<IEnumerable<WebhookRegistrationResponse>>()!.ToList();
        list.Count.ShouldBe(2);
        list.ShouldAllBe(r => r.Secret == null);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingRegistration_ReturnsUpdated()
    {
        var reg = MakeRegistration();
        var updated = reg with { Label = "Updated Label" };
        _registrations.GetAsync(reg.Id, Arg.Any<CancellationToken>()).Returns(reg);
        _registrations.UpdateAsync(Arg.Any<WebhookRegistration>(), Arg.Any<CancellationToken>())
            .Returns(updated);

        var result = await _sut.Update(
            reg.Id.Value,
            new UpdateWebhookRegistrationRequest(Label: "Updated Label"),
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<WebhookRegistrationResponse>();
        response.Label.ShouldBe("Updated Label");
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        // No setup — substitute returns null by default.
        var result = await _sut.Update(
            "wh_missing0000001",
            new UpdateWebhookRegistrationRequest(Label: "x"),
            CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingRegistration_Returns204()
    {
        var reg = MakeRegistration();
        _registrations.GetAsync(reg.Id, Arg.Any<CancellationToken>()).Returns(reg);

        var result = await _sut.Delete(reg.Id.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await _registrations.Received(1).DeleteAsync(reg.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        // No setup — substitute returns null by default.
        var result = await _sut.Delete("wh_missing0000001", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── Run polling ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRun_ExistingRun_Returns200()
    {
        var run = new WebhookRun
        {
            Id = WebhookRunId.Create(),
            WebhookId = WebhookId.From("wh_abc123def456789"),
            Status = WebhookRunStatus.Completed,
            AcceptedAt = DateTimeOffset.UtcNow,
            AgentResponse = "Done."
        };
        _runs.GetAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);

        var result = await _sut.GetRun(run.Id.Value, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<WebhookRunResponse>();
        response.Status.ShouldBe("Completed");
        response.AgentResponse.ShouldBe("Done.");
    }

    [Fact]
    public async Task GetRun_NotFound_Returns404()
    {
        // No setup — substitute returns null by default for WebhookRun.
        var result = await _sut.GetRun("whr_doesnotexist000", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── URL generation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GeneratesCorrectInboundUrl()
    {
        var reg = MakeRegistration("wh_testid12345678", "nova");
        _registrations.CreateAsync(Arg.Any<WebhookRegistration>(), Arg.Any<CancellationToken>())
            .Returns(reg);

        var result = await _sut.Create(
            new CreateWebhookRegistrationRequest("nova", "Nova Alerts"),
            CancellationToken.None);

        var created = result.Result.ShouldBeOfType<CreatedAtActionResult>();
        var response = created.Value.ShouldBeOfType<WebhookRegistrationResponse>();
        response.Url.ShouldBe("https://gateway.test/api/webhooks/nova/wh_testid12345678");
    }
}
