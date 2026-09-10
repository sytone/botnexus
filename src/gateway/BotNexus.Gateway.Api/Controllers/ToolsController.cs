using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Tools;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for managing user-defined portal tools. Tools are persisted server-side so
/// they roam with the user across browsers and devices (#2232).
/// </summary>
[ApiController]
[Route("api/tools")]
public sealed class ToolsController(
    IToolStore store,
    ILogger<ToolsController> logger,
    IHttpClientFactory? httpClientFactory = null) : ControllerBase
{
    private readonly IToolStore _store = store;
    private readonly ILogger<ToolsController> _logger = logger;
    private readonly IHttpClientFactory? _httpClientFactory = httpClientFactory;


    /// <summary>
    /// Reports whether a tool's site will allow itself to be embedded in a frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This has to be answered on the SERVER. A browser gives the hosting page nothing usable to
    /// detect a refusal with: a blocked frame raises the same load event a successful one does,
    /// and reading into it throws SecurityError in both cases. A client-side probe was tried and
    /// could not tell a blocked frame from a working cross-origin one - so the portal showed a
    /// blank white panel and claimed success.
    /// </para>
    /// <para>
    /// The gateway can simply ask the site. X-Frame-Options DENY/SAMEORIGIN and a CSP
    /// frame-ancestors directive that does not admit us both mean the browser will refuse, and
    /// both are plain response headers. Redirects are followed, because the headers that matter
    /// are the ones on the page that would actually render - SABnzbd answers 303 and only the
    /// login page it lands on carries the header.
    /// </para>
    /// <para>
    /// Not an open fetch proxy: the URL comes from a stored tool by id, never from the caller.
    /// </para>
    /// </remarks>
    /// <param name="id">Tool identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [HttpGet("{id}/embeddable")]
    public async Task<ActionResult<ToolEmbeddableResponse>> Embeddable(
        string id,
        CancellationToken cancellationToken)
    {
        var tool = await _store.GetAsync(ToolId.From(id), cancellationToken);

        if (tool is null)
            return NotFound();

        if (_httpClientFactory is null || !Uri.TryCreate(tool.Url, UriKind.Absolute, out var url))
        {
            // Unknown rather than a guess: the frame is still attempted and the timeout watchdog
            // remains the backstop.
            return Ok(new ToolEmbeddableResponse { Embeddable = true, Reason = null, Checked = false });
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var reason = DescribeFramingRefusal(response);

            return Ok(new ToolEmbeddableResponse
            {
                Embeddable = reason is null,
                Reason = reason,
                Checked = true,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Unreachable from here does not mean unreachable from the browser - they may be on
            // different networks. Say nothing rather than something wrong.
            _logger.LogDebug(ex, "Could not check framing headers for tool {ToolId}.", id);
            return Ok(new ToolEmbeddableResponse { Embeddable = true, Reason = null, Checked = false });
        }
    }

    /// <summary>Names the header that will make a browser refuse to frame this, or null.</summary>
    private static string? DescribeFramingRefusal(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Frame-Options", out var xfo))
        {
            var value = string.Join(", ", xfo).Trim();

            // ALLOW-FROM is obsolete and ignored by current browsers; anything else here refuses.
            if (!string.IsNullOrWhiteSpace(value))
                return $"X-Frame-Options: {value}";
        }

        if (response.Headers.TryGetValues("Content-Security-Policy", out var csp))
        {
            foreach (var directive in string.Join(";", csp).Split(';'))
            {
                var trimmed = directive.Trim();

                if (!trimmed.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 'none' and 'self' both exclude this origin. A host list might admit us, but the
                // portal cannot know its own public origin reliably, so it is treated as a refusal
                // and the user gets a working "open in new tab" rather than a blank panel.
                return $"Content-Security-Policy: {trimmed}";
            }
        }

        return null;
    }

    /// <summary>Lists all tools ordered by <see cref="ToolDefinition.Order"/> ascending.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The full ordered list of tools.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ToolDefinition>>> List(CancellationToken cancellationToken)
        => Ok(await _store.ListAsync(cancellationToken));

    /// <summary>Gets a single tool by identifier.</summary>
    /// <param name="id">The tool identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tool, or 404 when it does not exist.</returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<ToolDefinition>> Get(string id, CancellationToken cancellationToken)
    {
        var tool = await _store.GetAsync(ToolId.From(id), cancellationToken);
        return tool is null ? NotFound(new { error = $"Tool '{id}' not found." }) : Ok(tool);
    }

    /// <summary>Creates a new tool.</summary>
    /// <param name="request">The tool to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created tool.</returns>
    [HttpPost]
    public async Task<ActionResult<ToolDefinition>> Create([FromBody] ToolDefinition request, CancellationToken cancellationToken)
    {
        var created = await _store.CreateAsync(request, cancellationToken);
        _logger.LogInformation("Tool created via API: {ToolId} ({Name})", created.Id.Value, created.Name);
        return CreatedAtAction(nameof(Get), new { id = created.Id.Value }, created);
    }

    /// <summary>Updates an existing tool.</summary>
    /// <param name="id">The tool identifier.</param>
    /// <param name="request">The updated tool payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated tool, or 404 when it does not exist.</returns>
    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    public async Task<ActionResult<ToolDefinition>> Update(string id, [FromBody] ToolDefinition request, CancellationToken cancellationToken)
    {
        var typedId = ToolId.From(id);
        var existing = await _store.GetAsync(typedId, cancellationToken);
        if (existing is null)
            return NotFound(new { error = $"Tool '{id}' not found." });

        var updated = request with
        {
            Id = typedId,
            CreatedAt = existing.CreatedAt
        };

        var saved = await _store.UpdateAsync(updated, cancellationToken);
        _logger.LogInformation("Tool updated via API: {ToolId} ({Name})", saved.Id.Value, saved.Name);
        return Ok(saved);
    }

    /// <summary>Deletes a tool.</summary>
    /// <param name="id">The tool identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>204 No Content, or 404 when the tool does not exist.</returns>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var typedId = ToolId.From(id);
        var existing = await _store.GetAsync(typedId, cancellationToken);
        if (existing is null)
            return NotFound(new { error = $"Tool '{id}' not found." });

        await _store.DeleteAsync(typedId, cancellationToken);
        _logger.LogInformation("Tool deleted via API: {ToolId}", id);
        return NoContent();
    }
}

/// <summary>Whether a tool's site permits being framed, and what said otherwise.</summary>
public sealed class ToolEmbeddableResponse
{
    /// <summary>False when a response header will make the browser refuse to render the frame.</summary>
    public required bool Embeddable { get; init; }

    /// <summary>The header responsible, for showing the user why. Null when embeddable.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// False when the check could not be performed at all - the site was unreachable from the
    /// gateway, or the URL was unusable. Distinguishes "allowed" from "unknown".
    /// </summary>
    public required bool Checked { get; init; }
}
