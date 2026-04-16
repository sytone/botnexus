using System.Collections.Frozen;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Commands;

/// <summary>
/// Resolves and dispatches slash commands provided by command contributors.
/// </summary>
public sealed class CommandRegistry
{
    private readonly FrozenDictionary<string, CommandRegistration> _registrations;
    private readonly IReadOnlyList<CommandDescriptor> _commands;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandRegistry"/> class.
    /// </summary>
    /// <param name="contributors">Registered command contributors.</param>
    /// <param name="logger">Logger instance.</param>
    public CommandRegistry(
        IEnumerable<ICommandContributor> contributors,
        ILogger<CommandRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(logger);

        var registrations = new Dictionary<string, CommandRegistration>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<CommandDescriptor>();

        foreach (var contributor in contributors)
        {
            foreach (var command in contributor.GetCommands())
            {
                if (registrations.ContainsKey(command.Name))
                {
                    logger.LogWarning(
                        "Duplicate command registration detected for '{CommandName}'. First registration wins.",
                        command.Name);
                    continue;
                }

                registrations[command.Name] = new CommandRegistration(command, contributor);
                commands.Add(command);
            }
        }

        _registrations = registrations.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _commands = commands.AsReadOnly();
    }

    /// <summary>
    /// Gets all registered commands.
    /// </summary>
    /// <returns>Registered command descriptors.</returns>
    public IReadOnlyList<CommandDescriptor> GetAll() => _commands;

    /// <summary>
    /// Executes a command from raw slash input.
    /// </summary>
    /// <param name="rawInput">Raw user input.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command result.</returns>
    public async Task<CommandResult> ExecuteAsync(
        string rawInput,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new CommandResult
            {
                Title = "Invalid Command",
                Body = "Command input cannot be empty.",
                IsError = true
            };
        }

        var parseResult = ParseRawInput(rawInput);
        var commandName = parseResult.CommandName;
        if (commandName is null || !_registrations.TryGetValue(commandName, out var registration))
        {
            return new CommandResult
            {
                Title = "Command Not Found",
                Body = $"Unknown command: {commandName ?? rawInput.Trim()}",
                IsError = true
            };
        }

        var executionContext = context with
        {
            RawInput = rawInput,
            SubCommand = parseResult.SubCommand,
            Arguments = parseResult.Arguments
        };

        try
        {
            return await registration.Contributor
                .ExecuteAsync(commandName, executionContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CommandResult
            {
                Title = "Command Execution Failed",
                Body = $"Command '{commandName}' failed: {ex.Message}",
                IsError = true
            };
        }
    }

    private static CommandParseResult ParseRawInput(string rawInput)
    {
        var tokens = rawInput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return new CommandParseResult(null, null, []);

        var commandIndex = Array.FindIndex(tokens, token => token.StartsWith("/", StringComparison.Ordinal));
        if (commandIndex < 0)
            return new CommandParseResult(null, null, []);

        var commandName = tokens[commandIndex];
        var subCommand = commandIndex + 1 < tokens.Length ? tokens[commandIndex + 1] : null;
        var argumentStart = subCommand is null ? commandIndex + 1 : commandIndex + 2;
        var arguments = argumentStart < tokens.Length
            ? tokens[argumentStart..]
            : [];

        return new CommandParseResult(commandName, subCommand, arguments);
    }

    private sealed record CommandParseResult(string? CommandName, string? SubCommand, IReadOnlyList<string> Arguments);

    private sealed record CommandRegistration(CommandDescriptor Descriptor, ICommandContributor Contributor);
}
