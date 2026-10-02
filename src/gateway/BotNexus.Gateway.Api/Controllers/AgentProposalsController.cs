using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Services;
using BotNexus.Gateway.Contracts.Agents;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Operator-facing boundary for listing and reviewing governed agent proposals.
/// Gateway authentication applies the deployment's normal optional access policy before this
/// controller. Each action additionally requires unrestricted operator request provenance, which
/// excludes agent-scoped and satellite credentials without inventing human roles or RBAC.
/// </summary>
[ApiController]
[Route("api/agent-proposals")]
public sealed class AgentProposalsController(
    IAgentProposalStore proposals,
    AgentProposalReviewService reviewService) : ControllerBase
{
    private const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    /// <summary>Lists proposals, optionally filtered by review status.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentProposal>>> List(
        [FromQuery] AgentProposalStatus? status,
        CancellationToken cancellationToken)
    {
        if (OperatorIdentity is null)
            return Forbidden();
        return Ok(await proposals.ListAsync(status, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gets one proposal including review and application evidence.</summary>
    [HttpGet("{proposalId:guid}")]
    public async Task<ActionResult<AgentProposal>> Get(Guid proposalId, CancellationToken cancellationToken)
    {
        if (OperatorIdentity is null)
            return Forbidden();
        var proposal = await proposals.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        return proposal is null ? NotFound() : Ok(proposal);
    }

    /// <summary>
    /// Records an operator decision. Reviewer identity is derived from the server-stamped
    /// <see cref="GatewayCallerIdentity"/> and is intentionally absent from the request contract.
    /// </summary>
    [HttpPost("{proposalId:guid}/review")]
    public async Task<IActionResult> Review(
        Guid proposalId,
        [FromBody] AgentProposalReviewRequest request,
        CancellationToken cancellationToken)
    {
        var identity = OperatorIdentity;
        if (identity is null)
            return Forbidden();
        if (request.Decision is AgentProposalStatus.Pending)
            return BadRequest(new { error = "Decision must be Approved or Rejected." });

        CitizenId reviewer;
        try
        {
            reviewer = CitizenId.Of(UserId.From(identity.CallerId));
        }
        catch (Vogen.ValueObjectValidationException)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Operator request provenance cannot be represented as a reviewer identity." });
        }

        var result = await reviewService.ReviewAsync(
            proposalId,
            request.Decision,
            reviewer,
            request.Reason,
            cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            AgentProposalLifecycleOutcome.NotFound => NotFound(),
            AgentProposalLifecycleOutcome.AlreadyReviewed => Conflict(result),
            AgentProposalLifecycleOutcome.ApplicationFailed => StatusCode(StatusCodes.Status500InternalServerError, result),
            _ => Ok(result),
        };
    }

    /// <summary>
    /// Records an operator's evidence-based conclusion for a proposal stranded in Applying.
    /// This endpoint never invokes lifecycle application; a NotApplied conclusion only makes a
    /// later explicit approval retry eligible.
    /// </summary>
    [HttpPost("{proposalId:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(
        Guid proposalId,
        [FromBody] AgentProposalReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        var identity = OperatorIdentity;
        if (identity is null)
            return Forbidden();
        if (!Enum.IsDefined(request.Decision))
            return BadRequest(new { error = "Decision must be Applied or NotApplied." });
        if (string.IsNullOrWhiteSpace(request.Evidence))
            return BadRequest(new { error = "Reconciliation evidence is required." });

        CitizenId reconciler;
        try { reconciler = CitizenId.Of(UserId.From(identity.CallerId)); }
        catch (Vogen.ValueObjectValidationException)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Operator request provenance cannot be represented as a reconciler identity." });
        }

        var result = await proposals.ReconcileApplicationAsync(
            proposalId, request.Decision, reconciler, request.Evidence,
            DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            AgentProposalReconciliationOutcome.NotFound => NotFound(),
            AgentProposalReconciliationOutcome.NotApplying => Conflict(result),
            _ => Ok(result),
        };
    }

    private GatewayCallerIdentity? OperatorIdentity
        => HttpContext.Items.TryGetValue(CallerIdentityItemKey, out var value)
            && value is GatewayCallerIdentity { AllowedAgents.Count: 0 } identity
            && !identity.CallerId.StartsWith("satellite:", StringComparison.OrdinalIgnoreCase)
                ? identity
                : null;

    private ObjectResult Forbidden()
        => StatusCode(StatusCodes.Status403Forbidden, new { error = "Agent-scoped callers cannot review agent proposals." });
}

/// <summary>Decision payload for a proposal review; reviewer identity comes from request provenance.</summary>
/// <param name="Decision">Approved or Rejected.</param>
/// <param name="Reason">Optional operator rationale retained in audit history.</param>
public sealed record AgentProposalReviewRequest(AgentProposalStatus Decision, string? Reason);

/// <summary>Evidence-based resolution of an ambiguous application attempt.</summary>
/// <param name="Decision">Whether external verification proves the lifecycle effect applied.</param>
/// <param name="Evidence">Required operator evidence retained in the proposal ledger.</param>
public sealed record AgentProposalReconciliationRequest(
    AgentProposalReconciliationDecision Decision,
    string Evidence);
