using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Http;
using BotNexus.Gateway.Commands;
using BotNexus.Gateway.Configuration;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for command discovery and execution.
/// </summary>
[ApiController]
[Route("api/commands")]
public sealed class CommandsController : ControllerBase
{
    private readonly CommandRegistry _commandRegistry;
    private readonly IAgentSupervisor _supervisor;
    private readonly BotNexusHome _home;
    private readonly ISessionStore _sessions;
    private readonly IConversationStore _conversations;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandsController"/> class.
    /// </summary>
    public CommandsController(CommandRegistry commandRegistry, IAgentSupervisor supervisor, BotNexusHome home,
        ISessionStore sessions, IConversationStore conversations)
    {
        _commandRegistry = commandRegistry;
        _supervisor = supervisor;
        _home = home;
        _sessions = sessions;
        _conversations = conversations;
    }

    /// <summary>
    /// Lists all available slash commands.
    /// </summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<CommandDescriptor>> List() => Ok(_commandRegistry.GetAll());

    /// <summary>
    /// Lists all available slash commands.
    /// </summary>
    [NonAction]
    public ActionResult<IReadOnlyList<CommandDescriptor>> GetCommands() => List();

    /// <summary>
    /// Executes a slash command.
    /// </summary>
    [HttpPost("execute")]
    public async Task<ActionResult<CommandResult>> Execute([FromBody] CommandExecuteRequest request, CancellationToken cancellationToken)
    {
        // Authorize the body value actually passed to commands, independently of any query
        // selector. The middleware supplies an identity even in keyless development mode.
        if (HttpContext?.Items[GatewayAuthHttpContext.CallerIdentityItemKey] is not GatewayCallerIdentity caller ||
            !GatewayAuthMiddleware.IsAgentAuthorized(caller, request.AgentId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller is not authorized for this agent." });

        if (string.IsNullOrWhiteSpace(request.Input))
            return BadRequest(new { error = "input is required." });

        var commandName = TryExtractCommandName(request.Input);
        if (commandName is null)
            return BadRequest(new { error = "input must include a slash command name." });

        var command = _commandRegistry.GetAll().FirstOrDefault(value =>
            string.Equals(value.Name, commandName, StringComparison.OrdinalIgnoreCase));
        if (command is null)
            return NotFound(new { error = $"Unknown command: {commandName}" });

        // Check every selector, not only the first one preferred by authentication middleware.
        // Authorization above still takes precedence for a forbidden body agent.
        foreach (var selector in new[] { "agent", "agentId" })
        {
            if (Request.Query.TryGetValue(selector, out var values) && values.Any(value =>
                !string.Equals(value?.Trim(), request.AgentId?.Trim(), StringComparison.OrdinalIgnoreCase)))
                return BadRequest(new { error = "Query and body agent selectors must agree." });
        }

        if ((request.AgentId is not null && !AgentId.TryFrom(request.AgentId, out _)) ||
            (request.SessionId is not null && !SessionId.TryFrom(request.SessionId, out _)) ||
            ((request.SessionId is not null || command.Category == "Session") && string.IsNullOrWhiteSpace(request.AgentId)))
            return BadRequest(new { error = "A valid agent/session context is required." });

        // A missing handle is not proof that a supplied session is safe to mutate. Validate
        // persisted ownership and its current durable parent before any handle/tool lookup.
        if (request.SessionId is not null &&
            (request.AgentId is null ||
             !await IsCurrentSessionAsync(AgentId.From(request.AgentId), SessionId.From(request.SessionId), cancellationToken)))
            return BadRequest(new { error = "Session does not match the current agent and durable conversation." });

        var context = new CommandExecutionContext
        {
            RawInput = request.Input,
            AgentId = request.AgentId,
            SessionId = request.SessionId,
            HomeDirectory = _home.RootPath,
            ResolveSessionTool = BuildSessionToolResolver(request.AgentId, request.SessionId)
        };

        var result = await _commandRegistry.ExecuteAsync(request.Input, context, cancellationToken);
        return Ok(result);
    }

    private async Task<bool> IsCurrentSessionAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(sessionId, cancellationToken);
        if (session is null || session.SessionId != sessionId || session.AgentId != agentId)
            return false;

        Conversation? conversation;
        if (session.ConversationId.IsInitialized())
        {
            conversation = await _conversations.GetAsync(session.ConversationId, cancellationToken);
            if (conversation is null || conversation.ConversationId != session.ConversationId)
                return false;
        }
        else
        {
            var matches = (await _conversations.ListAsync(agentId, cancellationToken))
                .Where(value => value.ActiveSessionId == sessionId).ToList();
            if (matches.Count != 1)
                return false;
            conversation = matches[0];
        }

        return conversation.ConversationId.IsInitialized() && conversation.AgentId == agentId &&
            conversation.ActiveSessionId == sessionId && conversation.Status != ConversationStatus.Archived;
    }

    private Func<string, IAgentTool?>? BuildSessionToolResolver(string? agentId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (_supervisor is not IAgentHandleInspector inspector)
            return null;

        var parsedAgentId = AgentId.From(agentId);
        var parsedSessionId = SessionId.From(sessionId);
        if (inspector.GetHandle(parsedAgentId, parsedSessionId) is null)
            return null;

        return toolName => inspector.ResolveTool(parsedAgentId, parsedSessionId, toolName);
    }

    private static string? TryExtractCommandName(string input)
    {
        var tokens = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return null;

        return tokens.FirstOrDefault(token => token.StartsWith("/", StringComparison.Ordinal));
    }
}

/// <summary>
/// Command execution request payload.
/// </summary>
public sealed record CommandExecuteRequest
{
    /// <summary>
    /// Gets the raw command input.
    /// </summary>
    public string? Input { get; init; }

    /// <summary>
    /// Gets the optional agent identifier.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Gets the optional session identifier.
    /// </summary>
    public string? SessionId { get; init; }
}
