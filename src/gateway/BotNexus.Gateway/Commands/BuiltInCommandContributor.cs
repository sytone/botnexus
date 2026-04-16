using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Commands;

/// <summary>
/// Provides built-in slash commands through the shared command contributor extension contract.
/// </summary>
internal sealed class BuiltInCommandContributor(
    IAgentRegistry agentRegistry,
    IAgentSupervisor agentSupervisor,
    ISessionStore sessionStore,
    IServiceProvider serviceProvider) : ICommandContributor
{
    private static readonly IReadOnlyList<CommandDescriptor> BuiltInCommands =
    [
        new CommandDescriptor
        {
            Name = "/help",
            Description = "List all available commands.",
            Category = "System"
        },
        new CommandDescriptor
        {
            Name = "/status",
            Description = "Show gateway health and runtime status.",
            Category = "System"
        },
        new CommandDescriptor
        {
            Name = "/agents",
            Description = "List registered agents and their models.",
            Category = "System"
        },
        new CommandDescriptor
        {
            Name = "/new",
            Description = "Seal the current session and create a new one.",
            Category = "Session"
        },
        new CommandDescriptor
        {
            Name = "/reset",
            Description = "Reset the current chat (client-side only).",
            Category = "Session",
            ClientSideOnly = true
        }
    ];

    public IReadOnlyList<CommandDescriptor> GetCommands() => BuiltInCommands;

    public Task<CommandResult> ExecuteAsync(
        string commandName,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default)
        => NormalizeCommand(commandName) switch
        {
            "/help" => Task.FromResult(ExecuteHelp()),
            "/status" => Task.FromResult(ExecuteStatus()),
            "/agents" => Task.FromResult(ExecuteAgents()),
            "/new" => ExecuteNewSessionAsync(context, cancellationToken),
            "/reset" => Task.FromResult(ClientSideOnlyCommandResult()),
            _ => Task.FromResult(new CommandResult
            {
                Title = "Command Not Found",
                Body = $"Unknown built-in command: {commandName}",
                IsError = true
            })
        };

    private static string NormalizeCommand(string commandName)
        => commandName.Trim().ToLowerInvariant();

    private CommandResult ExecuteHelp()
    {
        var registry = serviceProvider.GetService<CommandRegistry>();
        if (registry is null)
        {
            return new CommandResult
            {
                Title = "Help Unavailable",
                Body = "Command registry is not available.",
                IsError = true
            };
        }

        var builder = new StringBuilder();
        builder.AppendLine("Available Commands");
        builder.AppendLine();

        foreach (var command in registry.GetAll().OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase))
        {
            var suffix = command.ClientSideOnly ? " (client-side only)" : string.Empty;
            builder.AppendLine($"- {command.Name}{suffix}: {command.Description}");
        }

        return new CommandResult
        {
            Title = "Command Help",
            Body = builder.ToString().TrimEnd(),
            IsError = false
        };
    }

    private CommandResult ExecuteStatus()
    {
        var registeredAgents = agentRegistry.GetAll();
        var instances = agentSupervisor.GetAllInstances() ?? [];
        var runningSessions = instances.Count(instance =>
            instance.Status is AgentInstanceStatus.Starting
                or AgentInstanceStatus.Idle
                or AgentInstanceStatus.Running);

        var body = $"""
                    Gateway Status
                    - Registered agents: {registeredAgents.Count}
                    - Running sessions: {runningSessions}
                    - Active instances: {instances.Count}
                    """;

        return new CommandResult
        {
            Title = "Gateway Status",
            Body = body,
            IsError = false
        };
    }

    private CommandResult ExecuteAgents()
    {
        var agents = agentRegistry.GetAll()
            .OrderBy(agent => agent.AgentId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (agents.Count == 0)
        {
            return new CommandResult
            {
                Title = "Registered Agents",
                Body = "No agents are currently registered.",
                IsError = false
            };
        }

        var builder = new StringBuilder("Registered Agents");
        builder.AppendLine();
        builder.AppendLine();

        foreach (var agent in agents)
        {
            builder.AppendLine($"- {agent.AgentId.Value}: provider={agent.ApiProvider}, model={agent.ModelId}");
        }

        return new CommandResult
        {
            Title = "Registered Agents",
            Body = builder.ToString().TrimEnd(),
            IsError = false
        };
    }

    private async Task<CommandResult> ExecuteNewSessionAsync(
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId))
        {
            return new CommandResult
            {
                Title = "Session Creation Failed",
                Body = "Cannot create a new session without an active agent context.",
                IsError = true
            };
        }

        var agentId = AgentId.From(context.AgentId);
        if (!string.IsNullOrWhiteSpace(context.SessionId))
        {
            var currentSessionId = SessionId.From(context.SessionId);
            var currentSession = await sessionStore.GetAsync(currentSessionId, cancellationToken).ConfigureAwait(false);
            if (currentSession is not null)
            {
                currentSession.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed;
                currentSession.UpdatedAt = DateTimeOffset.UtcNow;
                await sessionStore.SaveAsync(currentSession, cancellationToken).ConfigureAwait(false);
            }

            await agentSupervisor.StopAsync(agentId, currentSessionId, cancellationToken).ConfigureAwait(false);
        }

        var newSessionId = SessionId.Create();
        await sessionStore.GetOrCreateAsync(newSessionId, agentId, cancellationToken).ConfigureAwait(false);

        return new CommandResult
        {
            Title = "New Session Created",
            Body = $"Created new session: {newSessionId.Value}",
            IsError = false
        };
    }

    private static CommandResult ClientSideOnlyCommandResult() => new()
    {
        Title = "Client-side Command",
        Body = "This command executes client-side only.",
        IsError = true
    };
}
