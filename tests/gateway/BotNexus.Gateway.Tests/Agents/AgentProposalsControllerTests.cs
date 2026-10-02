using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Services;
using BotNexus.Gateway.Contracts.Agents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentProposalsControllerTests
{
    [Fact]
    public async Task List_UnrestrictedOperatorCaller_DoesNotRequireAdminRole()
    {
        var store = new Mock<IAgentProposalStore>();
        store.Setup(s => s.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var controller = CreateController(store.Object, callerId: "gateway-dev");

        var result = await controller.List(null, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        store.VerifyAll();
    }

    [Fact]
    public async Task Get_MissingIdentity_IsForbidden()
    {
        var store = new Mock<IAgentProposalStore>();
        var controller = CreateController(store.Object, callerId: null);

        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Review_AgentScopedCaller_Is403WithZeroStoreWriterOrRegistryMutation()
    {
        var proposalId = Guid.NewGuid();
        var store = new Mock<IAgentProposalStore>(MockBehavior.Strict);
        var writer = new Mock<IAgentConfigurationWriter>(MockBehavior.Strict);
        var registry = new Mock<IAgentRegistry>(MockBehavior.Strict);
        var controller = CreateController(
            store.Object,
            callerId: "agent-farnsworth",
            allowedAgents: ["farnsworth"],
            registry.Object,
            writer.Object);

        var result = await controller.Review(
            proposalId,
            new AgentProposalReviewRequest(AgentProposalStatus.Approved, "agent cannot approve"),
            CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        store.VerifyNoOtherCalls();
        writer.VerifyNoOtherCalls();
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reconcile_InvalidNumericDecision_IsBadRequestAndStoreIsNotCalled()
    {
        var store = new Mock<IAgentProposalStore>(MockBehavior.Strict);
        var controller = CreateController(store.Object, callerId: "gateway-dev");

        var result = await controller.Reconcile(
            Guid.NewGuid(),
            new AgentProposalReconciliationRequest((AgentProposalReconciliationDecision)999, "evidence"),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Review_DerivesReviewerFromOperatorProvenanceNotRequestBody()
    {
        var proposalId = Guid.NewGuid();
        var proposal = Pending(proposalId);
        var store = new Mock<IAgentProposalStore>();
        store.Setup(s => s.ReviewAsync(
                proposalId,
                AgentProposalStatus.Rejected,
                CitizenId.Of(UserId.From("gateway-dev")),
                "policy mismatch",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentProposalReviewResult(
                AgentProposalReviewOutcome.Applied,
                proposal with { Status = AgentProposalStatus.Rejected }));
        var controller = CreateController(store.Object, callerId: "gateway-dev");

        var result = await controller.Review(
            proposalId,
            new AgentProposalReviewRequest(AgentProposalStatus.Rejected, "policy mismatch"),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        store.VerifyAll();
        typeof(AgentProposalReviewRequest).GetProperties()
            .Select(property => property.Name)
            .ShouldNotContain("Reviewer");
    }

    private static AgentProposalsController CreateController(
        IAgentProposalStore store,
        string? callerId,
        IReadOnlyList<string>? allowedAgents = null,
        IAgentRegistry? registryOverride = null,
        IAgentConfigurationWriter? writerOverride = null)
    {
        var registry = registryOverride ?? Mock.Of<IAgentRegistry>();
        var lifecycle = new AgentLifecycleService(
            registry,
            writerOverride ?? Mock.Of<IAgentConfigurationWriter>(),
            [],
            null,
            null,
            null,
            NullLogger<AgentLifecycleService>.Instance);
        var review = new AgentProposalReviewService(
            store,
            lifecycle,
            TimeProvider.System,
            NullLogger<AgentProposalReviewService>.Instance);
        var controller = new AgentProposalsController(store, review)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (callerId is not null)
        {
            controller.HttpContext.Items["BotNexus.Gateway.CallerIdentity"] = new GatewayCallerIdentity
            {
                CallerId = callerId,
                AllowedAgents = allowedAgents ?? [],
                IsAdmin = false,
            };
        }
        return controller;
    }

    private static AgentProposal Pending(Guid proposalId)
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("candidate"),
            DisplayName = "Candidate",
            ModelId = "model-a",
            ApiProvider = "provider-a",
        };
        return new AgentProposal(
            proposalId,
            AgentProposalKind.Create,
            descriptor.AgentId,
            descriptor,
            "Needed",
            CitizenId.Of(AgentId.From("farnsworth")),
            DateTimeOffset.UtcNow,
            AgentProposalStatus.Pending,
            null,
            null,
            null,
            []);
    }
}
