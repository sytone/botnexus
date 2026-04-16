using BotNexus.AgentCore.Tools;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Describes a command for client command palettes and API responses.
/// </summary>
public sealed record CommandDescriptor
{
    /// <summary>
    /// Gets the slash command name, such as <c>/skills</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the short command description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the optional command category.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets a value indicating whether the command should only execute on the client.
    /// </summary>
    public bool ClientSideOnly { get; init; }

    /// <summary>
    /// Gets the optional sub-command descriptors.
    /// </summary>
    public IReadOnlyList<SubCommandDescriptor>? SubCommands { get; init; }
}

/// <summary>
/// Describes a command sub-command.
/// </summary>
public sealed record SubCommandDescriptor
{
    /// <summary>
    /// Gets the sub-command name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the sub-command description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the optional sub-command argument descriptors.
    /// </summary>
    public IReadOnlyList<CommandArgumentDescriptor>? Arguments { get; init; }
}

/// <summary>
/// Describes a command argument.
/// </summary>
public sealed record CommandArgumentDescriptor
{
    /// <summary>
    /// Gets the argument name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the argument description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets a value indicating whether the argument is required.
    /// </summary>
    public bool Required { get; init; }
}

/// <summary>
/// Provides context for command execution.
/// </summary>
public sealed record CommandExecutionContext
{
    /// <summary>
    /// Gets the raw command input.
    /// </summary>
    public required string RawInput { get; init; }

    /// <summary>
    /// Gets the parsed sub-command name, if present.
    /// </summary>
    public string? SubCommand { get; init; }

    /// <summary>
    /// Gets the parsed command arguments after the sub-command.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets the active agent id, if available.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Gets the active session id, if available.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the BotNexus home directory.
    /// </summary>
    public required string HomeDirectory { get; init; }

    /// <summary>
    /// Gets a delegate that resolves a tool from the active session by tool name.
    /// </summary>
    public Func<string, IAgentTool?>? ResolveSessionTool { get; init; }
}

/// <summary>
/// Represents the result of command execution.
/// </summary>
public sealed record CommandResult
{
    /// <summary>
    /// Gets the result title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the result body text.
    /// </summary>
    public required string Body { get; init; }

    /// <summary>
    /// Gets a value indicating whether the result is an error.
    /// </summary>
    public bool IsError { get; init; }
}
