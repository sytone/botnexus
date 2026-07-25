using BotNexus.Gateway.Nav;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for the portal left-nav ordering model (#2236, slice 5 of #2231). Exposes the
/// effective order of every built-in nav item (defaults layered with user overrides) and lets the
/// user override or reset a single item's order. Overrides persist server-side so they roam with
/// the user across browsers and devices.
/// </summary>
[ApiController]
[Route("api/nav-order")]
public sealed class NavOrderController(INavOrderStore store, ILogger<NavOrderController> logger) : ControllerBase
{
    private readonly INavOrderStore _store = store;
    private readonly ILogger<NavOrderController> _logger = logger;

    /// <summary>Lists every built-in nav item with its effective order, ascending.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The full ordered list of nav items.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NavItemOrder>>> List(CancellationToken cancellationToken)
        => Ok(await _store.ListAsync(cancellationToken));

    /// <summary>Sets an order override for a single nav key.</summary>
    /// <param name="key">The stable nav key (e.g. <c>tools</c>).</param>
    /// <param name="request">The new order payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The full updated ordered list of nav items.</returns>
    [HttpPut("{key}")]
    [HttpPatch("{key}")]
    public async Task<ActionResult<IReadOnlyList<NavItemOrder>>> SetOrder(
        string key,
        [FromBody] NavOrderUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "Nav key is required." });

        await _store.SetOrderAsync(key, request.Order, cancellationToken);
        _logger.LogInformation("Nav order override set via API: {NavKey} = {Order}", key, request.Order);
        return Ok(await _store.ListAsync(cancellationToken));
    }

    /// <summary>Resets a nav key to its built-in default, removing any user override.</summary>
    /// <param name="key">The stable nav key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The full updated ordered list of nav items.</returns>
    [HttpDelete("{key}")]
    public async Task<ActionResult<IReadOnlyList<NavItemOrder>>> Reset(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "Nav key is required." });

        await _store.ResetAsync(key, cancellationToken);
        _logger.LogInformation("Nav order override reset via API: {NavKey}", key);
        return Ok(await _store.ListAsync(cancellationToken));
    }
}

/// <summary>Request body for setting a nav item's order override.</summary>
/// <param name="Order">The new order number; lower renders higher in the sidebar.</param>
public sealed record NavOrderUpdateRequest(int Order);
