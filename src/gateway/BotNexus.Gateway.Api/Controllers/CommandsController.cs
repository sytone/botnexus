using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandsController"/> class.
    /// </summary>
    public CommandsController(CommandRegistry commandRegistry, IAgentSupervisor supervisor, BotNexusHome home)
    {
        _commandRegistry = commandRegistry;
        _supervisor = supervisor;
        _home = home;
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
        if (string.IsNullOrWhiteSpace(request.Input))
            return BadRequest(new { error = "input is required." });

        var commandName = TryExtractCommandName(request.Input);
        if (commandName is null)
            return BadRequest(new { error = "input must include a slash command name." });

        var commands = _commandRegistry.GetAll();
        if (!commands.Any(command => string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase)))
            return NotFound(new { error = $"Unknown command: {commandName}" });

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
