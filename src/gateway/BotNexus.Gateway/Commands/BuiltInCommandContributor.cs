using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Commands;

/// <summary>
/// Provides built-in slash commands through the shared command contributor extension contract.
/// </summary>
internal sealed class BuiltInCommandContributor(
    IAgentRegistry agentRegistry,
    IAgentSupervisor agentSupervisor,
    ISessionStore sessionStore,
    IOptionsMonitor<CompactionOptions> compactionOptions,
    IServiceProvider serviceProvider,
    ISessionCompactionCoordinator? compactionCoordinator = null) : ICommandContributor
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
        },
        new CommandDescriptor
        {
            Name = "/context",
            Description = "Show a breakdown of context window usage for the current session.",
            Category = "Session"
        },
        new CommandDescriptor
        {
            Name = "/compact",
            Description = "Compact the current session context: summarise older messages to free context window.",
            Category = "Session"
        },
        new CommandDescriptor
        {
            Name = "/model",
            Description = "Show, set (/model <model-id>), or clear (/model clear) the per-conversation model override.",
            Category = "Session"
        },
        new CommandDescriptor
        {
            Name = "/reasoning",
            Description = "Show, set (/reasoning <minimal|low|medium|high|xhigh|max>), or clear (/reasoning clear) the per-conversation thinking override.",
            Category = "Session"
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
            "/context" => Task.FromResult(ExecuteContext(context)),
            "/compact" => ExecuteCompactAsync(context, cancellationToken),
            "/model" => ExecuteModelOverrideAsync(context, cancellationToken),
            "/reasoning" => ExecuteReasoningOverrideAsync(context, cancellationToken),
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

    private CommandResult ExecuteContext(CommandExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.SessionId))
        {
            return new CommandResult
            {
                Title = "Context Usage",
                Body = "No active session. Start a conversation first.",
                IsError = true
            };
        }

        if (agentSupervisor is not IAgentHandleInspector inspector)
        {
            return new CommandResult
            {
                Title = "Context Usage",
                Body = "Agent supervisor does not support context diagnostics.",
                IsError = true
            };
        }

        var agentId = AgentId.From(context.AgentId);
        var sessionId = SessionId.From(context.SessionId);
        var handle = inspector.GetHandle(agentId, sessionId);
        if (handle is null)
        {
            return new CommandResult
            {
                Title = "Context Usage",
                Body = "No active handle for this session. The session may not have started yet.",
                IsError = true
            };
        }

        var diag = (handle as IAgentHandleInspector)?.GetContextDiagnostics();
        if (diag is null)
        {
            return new CommandResult
            {
                Title = "Context Usage",
                Body = "Context diagnostics are not available for this handle type.",
                IsError = true
            };
        }

        var contextWindowTokens = compactionOptions.CurrentValue.ContextWindowTokens;
        var totalUsed = diag.TotalEstimatedTokens;
        var remaining = contextWindowTokens - totalUsed;
        var usedPct = contextWindowTokens > 0
            ? Math.Round((double)totalUsed / contextWindowTokens * 100, 1)
            : 0.0;

        static string Pct(int tokens, int window)
            => window > 0 ? $"{Math.Round((double)tokens / window * 100, 1),5:F1}%" : "  n/a%";

        static string Fmt(int n) => n.ToString("N0");

        var separator = new string('-', 60);
        var sb = new StringBuilder();
        sb.AppendLine($"Context Window Usage  ({usedPct}% of {Fmt(contextWindowTokens)} token window)");
        sb.AppendLine();
        sb.AppendLine($"{"Section",-28} {"~Tokens",9}  {"Chars",9}  {"  %",6}");
        sb.AppendLine(separator);
        sb.AppendLine($"{"System instructions",-28} {Fmt(diag.SystemPromptTokens),9}  {Fmt(diag.SystemPromptChars),9}  {Pct(diag.SystemPromptTokens, contextWindowTokens),6}");
        sb.AppendLine($"{"Tool definitions",-28} {Fmt(diag.ToolDefinitionTokens),9}  {Fmt(diag.ToolDefinitionChars),9}  {Pct(diag.ToolDefinitionTokens, contextWindowTokens),6}");
        sb.AppendLine($"{"User/Assistant messages",-28} {Fmt(diag.UserAssistantTokens),9}  {Fmt(diag.UserAssistantChars),9}  {Pct(diag.UserAssistantTokens, contextWindowTokens),6}");
        sb.AppendLine($"{"Tool results",-28} {Fmt(diag.ToolResultTokens),9}  {Fmt(diag.ToolResultChars),9}  {Pct(diag.ToolResultTokens, contextWindowTokens),6}");
        sb.AppendLine(separator);
        sb.AppendLine($"{"Total used",-28} {Fmt(totalUsed),9}  {"",9}  {Pct(totalUsed, contextWindowTokens),6}");
        sb.AppendLine($"{"Remaining",-28} {Fmt(remaining),9}");
        sb.AppendLine();
        sb.AppendLine($"History: {diag.HistoryEntryCount} message(s)  |  Tools: {diag.ToolCount}");
        sb.AppendLine("Note: token counts are estimates (chars / 4). Actual provider usage may differ.");

        return new CommandResult
        {
            Title = "Context Window Usage",
            Body = sb.ToString().TrimEnd(),
            IsError = false
        };
    }

    private static CommandResult ClientSideOnlyCommandResult() => new()
    {
        Title = "Client-side Command",
        Body = "This command executes client-side only.",
        IsError = true
    };

    private async Task<CommandResult> ExecuteCompactAsync(
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.SessionId))
        {
            return new CommandResult
            {
                Title = "Compact Failed",
                Body = "Cannot compact without an active session. Start a conversation first.",
                IsError = true
            };
        }

        if (compactionCoordinator is null)
        {
            return new CommandResult
            {
                Title = "Compact Failed",
                Body = "Session compaction coordinator is not available in this gateway instance.",
                IsError = true
            };
        }

        var sessionId = SessionId.From(context.SessionId);
        var session = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new CommandResult
            {
                Title = "Compact Failed",
                Body = "Session not found.",
                IsError = true
            };
        }

        var agentId = AgentId.From(context.AgentId);
        var outcome = await compactionCoordinator.CompactAsync(agentId, session, cancellationToken, force: true).ConfigureAwait(false);
        var notificationText = compactionCoordinator.BuildNotificationText(outcome);

        return new CommandResult
        {
            Title = "Session Compacted",
            Body = notificationText,
            IsError = !outcome.Applied && outcome.FailureReason is not null
        };
    }

    // #1706: /model and /reasoning drive the per-conversation override that ModelOverrideResolver
    // consumes as top precedence. With no argument they report the current override; with an
    // argument they set it; with clear/off/default/agent they clear back to the agent default. The
    // commands resolve the conversation bound to the active session and no-op safely when there is
    // no conversation context or conversation store.
    private async Task<CommandResult> ExecuteModelOverrideAsync(CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var (conversation, error) = await ResolveActiveConversationAsync(context, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return new CommandResult { Title = "Model Override", Body = error!, IsError = true };

        var arg = context.Arguments.Count > 0 ? string.Join(' ', context.Arguments).Trim() : null;
        var store = serviceProvider.GetService<IConversationStore>()!;

        if (string.IsNullOrWhiteSpace(arg))
        {
            var current = conversation.ModelOverride ?? "(none - using agent default)";
            return new CommandResult { Title = "Model Override", Body = $"Current model override: {current}", IsError = false };
        }

        if (IsClearToken(arg))
        {
            conversation.ModelOverride = null;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new CommandResult { Title = "Model Override", Body = "Cleared model override; reverting to the agent default.", IsError = false };
        }

        conversation.ModelOverride = arg;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
        return new CommandResult { Title = "Model Override", Body = $"Set model override to '{arg}' for this conversation.", IsError = false };
    }

    private async Task<CommandResult> ExecuteReasoningOverrideAsync(CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var (conversation, error) = await ResolveActiveConversationAsync(context, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return new CommandResult { Title = "Reasoning Override", Body = error!, IsError = true };

        var arg = context.Arguments.Count > 0 ? context.Arguments[0].Trim() : null;
        var store = serviceProvider.GetService<IConversationStore>()!;

        if (string.IsNullOrWhiteSpace(arg))
        {
            var current = conversation.ThinkingOverride ?? "(none - using agent default)";
            return new CommandResult { Title = "Reasoning Override", Body = $"Current thinking override: {current}", IsError = false };
        }

        if (IsClearToken(arg))
        {
            conversation.ThinkingOverride = null;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new CommandResult { Title = "Reasoning Override", Body = "Cleared thinking override; reverting to the agent default.", IsError = false };
        }

        var token = arg.ToLowerInvariant();
        if (token is not ("minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            return new CommandResult { Title = "Reasoning Override", Body = $"Unknown thinking level '{arg}'. Use minimal, low, medium, high, xhigh, or max.", IsError = true };

        conversation.ThinkingOverride = token;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
        return new CommandResult { Title = "Reasoning Override", Body = $"Set thinking override to '{token}' for this conversation.", IsError = false };
    }

    private static bool IsClearToken(string arg)
        => arg.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("off", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("default", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("agent", StringComparison.OrdinalIgnoreCase);

    private async Task<(Conversation? Conversation, string? Error)> ResolveActiveConversationAsync(
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.SessionId))
            return (null, "No active session. Start a conversation first.");

        var store = serviceProvider.GetService<IConversationStore>();
        if (store is null)
            return (null, "Conversation store is not available in this gateway instance.");

        var sessionId = SessionId.From(context.SessionId);
        var session = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is not null && session.ConversationId.IsInitialized())
        {
            var bySession = await store.GetAsync(session.ConversationId, cancellationToken).ConfigureAwait(false);
            if (bySession is not null)
                return (bySession, null);
        }

        var agentId = AgentId.From(context.AgentId);
        var conversations = await store.ListAsync(agentId, cancellationToken).ConfigureAwait(false);
        var bound = conversations.FirstOrDefault(c => c.ActiveSessionId == sessionId);
        if (bound is null)
            return (null, "No conversation is bound to this session yet.");
        return (bound, null);
    }
}
