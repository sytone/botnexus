using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for conversation canvas rendering and canvas-state persistence. Extracted from
/// <see cref="ConversationsController"/> (#1688) so the canvas concern can be exercised against a
/// single-dependency surface: it touches only the conversation store and the optional canvas
/// notifiers, which are orthogonal to conversation lifecycle, bindings, archival, and audit.
/// All route templates are preserved verbatim from the original controller so client behaviour is
/// unchanged.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ConversationCanvasController : ControllerBase
{
    private readonly IConversationStore _conversations;
    private readonly IReadOnlyList<IAgentCanvasNotifier> _canvasNotifiers;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationCanvasController"/> class.
    /// </summary>
    /// <param name="conversations">The conversation store - the controller's only required dependency.</param>
    /// <param name="canvasNotifiers">Optional notifiers that broadcast canvas state changes to connected
    /// clients. Defaults to none so the canvas-state CRUD endpoints can be unit-tested against an in-memory
    /// store without transport scaffolding.</param>
    public ConversationCanvasController(
        IConversationStore conversations,
        IEnumerable<IAgentCanvasNotifier>? canvasNotifiers = null)
    {
        _conversations = conversations;
        _canvasNotifiers = canvasNotifiers?.ToArray() ?? [];
    }

    /// <summary>Gets the current canvas HTML for a conversation.</summary>
    [HttpGet("~/api/agents/{agentId}/conversations/{conversationId}/canvas")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetCanvas(string agentId, string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return NotFound();
        if (string.IsNullOrEmpty(conversation.CanvasHtml))
            return NoContent();
        return Content(conversation.CanvasHtml, "text/html");
    }

    /// <summary>Saves canvas HTML for a conversation.</summary>
    [HttpPut("~/api/agents/{agentId}/conversations/{conversationId}/canvas")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PutCanvas(string agentId, string conversationId, [FromBody] string html, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return NotFound();
        conversation.CanvasHtml = string.IsNullOrEmpty(html) ? null : html;
        await _conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // -----------------------------------------------------------------------
    // Canvas State CRUD (Issue #1066)
    // -----------------------------------------------------------------------

    /// <summary>Returns the full canvas state dictionary for a conversation (empty object if none).</summary>
    [HttpGet("{conversationId}/canvas-state")]
    [ProducesResponseType(typeof(Dictionary<string, JsonElement>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetCanvasState(string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();
        var state = await _conversations.GetCanvasStateAsync(ConversationId.From(conversationId), cancellationToken);
        return Ok(state ?? new Dictionary<string, JsonElement>());
    }

    /// <summary>Returns a single canvas state key value, or 404 if not found.</summary>
    [HttpGet("{conversationId}/canvas-state/{key}")]
    [ProducesResponseType(typeof(JsonElement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetCanvasStateKey(string conversationId, string key, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();
        var state = await _conversations.GetCanvasStateAsync(ConversationId.From(conversationId), cancellationToken);
        if (state is null || !state.TryGetValue(key, out var value))
            return NotFound();
        return Ok(value);
    }

    /// <summary>Upserts a canvas state key. Body is the raw JSON value.</summary>
    [HttpPost("{conversationId}/canvas-state/{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetCanvasStateKey(
        string conversationId,
        string key,
        [FromBody] JsonElement value,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();
        await _conversations.SetCanvasStateKeyAsync(ConversationId.From(conversationId), key, value, cancellationToken);
        foreach (var notifier in _canvasNotifiers)
            await notifier.NotifyCanvasStateChangedAsync(conversationId, key, value, cancellationToken);
        return Ok();
    }

    /// <summary>Removes a canvas state key. Idempotent - returns 204 even if key didn't exist.</summary>
    [HttpDelete("{conversationId}/canvas-state/{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteCanvasStateKey(
        string conversationId,
        string key,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(ConversationId.From(conversationId), cancellationToken);
        if (conversation is null)
            return NotFound();
        await _conversations.DeleteCanvasStateKeyAsync(ConversationId.From(conversationId), key, cancellationToken);
        foreach (var notifier in _canvasNotifiers)
            await notifier.NotifyCanvasStateChangedAsync(conversationId, key, null, cancellationToken);
        return NoContent();
    }
}
