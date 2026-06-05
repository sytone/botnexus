using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Agent tool for inspecting, annotating, and creating gateway conversations.
/// Access is scoped per-agent based on <see cref="ConversationAccessLevel"/>.
/// </summary>
public sealed class ConversationTool(
    IConversationStore conversationStore,
    AgentId agentId,
    ConversationId? currentConversationId = null,
    ConversationAccessLevel accessLevel = ConversationAccessLevel.Own,
    IReadOnlyList<string>? allowedAgents = null,
    ISessionStore? sessionStore = null,
    IInboundMessageOrchestrator? messageOrchestrator = null,
    IConversationChangeNotifier? changeNotifier = null) : IAgentTool
{
    public string Name => "conversation";
    public string Label => "Conversation Context";

    public Tool Definition => new(
        Name,
        "Get, list, create, annotate, and archive persistent conversation context.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["get", "set_title", "set_purpose", "set", "list", "new", "message", "archive"],
                  "description": "Action to perform."
                },
                "conversationId": {
                  "type": "string",
                  "description": "Conversation ID for get/set actions. Defaults to the current conversation."
                },
                "agentId": {
                  "type": "string",
                  "description": "Target agent ID for cross-agent get/list/new actions. Defaults to your own agent."
                },
                "title": {
                  "type": "string",
                  "description": "New display title for set_title or new."
                },
                "displayName": {
                  "type": "string",
                  "description": "Alias for title/display name for set_title or new."
                },
                "purpose": {
                  "type": "string",
                  "description": "Conversation purpose for set_purpose or new."
                },
                "instructions": {
                  "type": ["string", "null"],
                  "description": "Conversation-scoped instructions injected into the system prompt. Pass null to clear."
                },
                "message": {
                  "type": "string",
                  "description": "Optional initial user message to seed the new conversation."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = ReadString(arguments, "action") ?? throw new ArgumentException("Missing required argument: action.");
        if (!action.Equals("get", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("set_title", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("set_purpose", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("list", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("new", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("set", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("message", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("archive", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported conversation action '{action}'.");

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = (ReadString(arguments, "action") ?? "").ToLowerInvariant();
        return action switch
        {
            "get" => await GetAsync(arguments, cancellationToken).ConfigureAwait(false),
            "set_title" => await SetTitleAsync(arguments, cancellationToken).ConfigureAwait(false),
            "set_purpose" => await SetPurposeAsync(arguments, cancellationToken).ConfigureAwait(false),
            "list" => await ListAsync(arguments, cancellationToken).ConfigureAwait(false),
            "new" => await NewAsync(arguments, cancellationToken).ConfigureAwait(false),
            "set" => await SetAsync(arguments, cancellationToken).ConfigureAwait(false),
            "message" => await SendMessageAsync(arguments, cancellationToken).ConfigureAwait(false),
            "archive" => await ArchiveAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported conversation action '{action}'.")
        };
    }


    private async Task<AgentToolResult> SendMessageAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var message = ReadString(arguments, "message")
            ?? throw new ArgumentException("Missing required argument: message.");
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("message must not be empty.");

        if (messageOrchestrator is null)
            throw new InvalidOperationException("Message orchestrator is required to send a message to a conversation.");

        if (sessionStore is null)
            throw new InvalidOperationException("Session store is required to send a message to a conversation.");

        var conversation = await ResolveConversationAsync(arguments, ct).ConfigureAwait(false);
        EnsureCanAccess(conversation.AgentId);

        // Reuse active session if present, otherwise create a fresh one.
        GatewaySession session;
        if (conversation.ActiveSessionId is { } existingSessionId)
        {
            var existing = await sessionStore.GetAsync(existingSessionId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                session = existing;
            }
            else
            {
                session = await sessionStore.GetOrCreateAsync(SessionId.Create(), conversation.AgentId, ct).ConfigureAwait(false);
                session.ConversationId = conversation.ConversationId;
                conversation.ActiveSessionId = session.SessionId;
                conversation.UpdatedAt = DateTimeOffset.UtcNow;
                await conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
            }
        }
        else
        {
            session = await sessionStore.GetOrCreateAsync(SessionId.Create(), conversation.AgentId, ct).ConfigureAwait(false);
            session.ConversationId = conversation.ConversationId;
            conversation.ActiveSessionId = session.SessionId;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        messageOrchestrator.Post(
            new InboundMessage
            {
                ChannelType = ChannelKey.From("internal"),
                SenderId = agentId.Value,
                Sender = CitizenId.Of(agentId),
                ChannelAddress = ChannelAddress.From(conversation.AgentId.Value),
                Content = message.Trim(),
                RoutingHints = new InboundMessageRoutingHints(
                    RequestedAgentId: conversation.AgentId,
                    RequestedSessionId: session.SessionId,
                    RequestedConversationId: conversation.ConversationId),
                Metadata = new Dictionary<string, object?>
                {
                    ["messageType"] = "message",
                    ["source"] = "conversation-tool-message"
                }
            });

        return TextResult(JsonSerializer.Serialize(new
        {
            conversationId = conversation.ConversationId.Value,
            sessionId = session.SessionId.Value
        }, JsonOptions));
    }

    private async Task<AgentToolResult> GetAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var conversation = await ResolveConversationAsync(arguments, ct).ConfigureAwait(false);
        EnsureCanAccess(conversation.AgentId);
        return TextResult(JsonSerializer.Serialize(ToToolResponse(conversation), JsonOptions));
    }

    private async Task<AgentToolResult> SetTitleAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var title = ReadString(arguments, "title") ?? ReadString(arguments, "displayName")
            ?? throw new ArgumentException("Missing required argument: title or displayName.");
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title must not be empty.");

        var conversation = await ResolveConversationAsync(arguments, ct).ConfigureAwait(false);
        EnsureCanAccess(conversation.AgentId);
        conversation.Title = title.Trim();
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        await NotifyBestEffortAsync("updated", conversation, ct).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(ToToolResponse(conversation), JsonOptions));
    }

    private async Task<AgentToolResult> SetPurposeAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var purpose = ReadString(arguments, "purpose")
            ?? throw new ArgumentException("Missing required argument: purpose.");

        var conversation = await ResolveConversationAsync(arguments, ct).ConfigureAwait(false);
        EnsureCanAccess(conversation.AgentId);
        conversation.Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        await NotifyBestEffortAsync("updated", conversation, ct).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(ToToolResponse(conversation), JsonOptions));
    }

    private async Task<AgentToolResult> ListAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var targetAgentId = ReadString(arguments, "agentId") is { } requestedAgentId
            ? AgentId.From(requestedAgentId)
            : agentId;
        EnsureCanAccess(targetAgentId);

        var conversations = await conversationStore.ListAsync(targetAgentId, ct).ConfigureAwait(false);
        var result = conversations.Select(ToToolResponse);
        return TextResult(JsonSerializer.Serialize(result, JsonOptions));
    }

    private async Task<AgentToolResult> NewAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var targetAgentId = ReadString(arguments, "agentId") is { } requestedAgentId
            ? AgentId.From(requestedAgentId)
            : agentId;
        EnsureCanAccess(targetAgentId);

        var title = ReadString(arguments, "displayName") ?? ReadString(arguments, "title");
        var purpose = ReadString(arguments, "purpose");
        var message = ReadString(arguments, "message");
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = targetAgentId,
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
            Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
            Status = ConversationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            // The conversation_new tool is invoked by an agent (the caller of this tool), so the
            // initiating citizen is always the calling agent.
            Initiator = CitizenId.Of(agentId)
        };

        var created = await conversationStore.CreateAsync(conversation, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(message))
        {
            if (sessionStore is null)
                throw new InvalidOperationException("Session store is required to seed an initial message.");

            var session = await sessionStore
                .GetOrCreateAsync(SessionId.Create(), targetAgentId, ct)
                .ConfigureAwait(false);
            session.ConversationId = created.ConversationId;
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = message.Trim()
            });
            await sessionStore.SaveAsync(session, ct).ConfigureAwait(false);

            created.ActiveSessionId = session.SessionId;
            created.UpdatedAt = DateTimeOffset.UtcNow;
            await conversationStore.SaveAsync(created, ct).ConfigureAwait(false);

            // Trigger agent turn: dispatch a synthetic inbound message so the agent
            // processes the seeded user message rather than it sitting silently in history.
            // Post() is fire-and-forget so the calling agent is not blocked waiting for
            // the spawned agent turn to complete (fixes #728).
            if (messageOrchestrator is not null)
            {
                messageOrchestrator.Post(
                    new InboundMessage
                    {
                        ChannelType = ChannelKey.From("internal"),
                        SenderId = agentId.Value,
                        Sender = CitizenId.Of(agentId),
                        ChannelAddress = ChannelAddress.From(targetAgentId.Value),
                        Content = message.Trim(),
                        RoutingHints = new InboundMessageRoutingHints(
                            RequestedAgentId: targetAgentId,
                            RequestedSessionId: session.SessionId,
                            RequestedConversationId: created.ConversationId),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["messageType"] = "message",
                            ["source"] = "conversation-tool-new"
                        }
                    });
            }
        }

        return TextResult(JsonSerializer.Serialize(ToToolResponse(created), JsonOptions));
    }

    private async Task<AgentToolResult> SetAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var conversation = await ResolveConversationAsync(arguments, ct).ConfigureAwait(false);
        EnsureCanAccess(conversation.AgentId);
        var instructions = ReadString(arguments, "instructions");
        if (arguments.ContainsKey("instructions"))
        {
            conversation.Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        await NotifyBestEffortAsync("updated", conversation, ct).ConfigureAwait(false);
        return TextResult("Conversation updated.");
    }

    private async Task<AgentToolResult> ArchiveAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var conversation = await ResolveConversationAsync(arguments, ct).ConfigureAwait(false);
        EnsureCanAccess(conversation.AgentId);
        await conversationStore.ArchiveAsync(conversation.ConversationId, ct).ConfigureAwait(false);
        await NotifyBestEffortAsync("archived", conversation, ct).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(new
        {
            conversationId = conversation.ConversationId.Value,
            agentId = conversation.AgentId.Value,
            status = "archived"
        }, JsonOptions));
    }

    private async Task<Conversation> ResolveConversationAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        ConversationId conversationId;
        if (ReadString(arguments, "conversationId") is { } requestedConversationId)
            conversationId = ConversationId.From(requestedConversationId);
        else if (currentConversationId is { } resolvedCurrentConversationId)
            conversationId = resolvedCurrentConversationId;
        else
            throw new ArgumentException("Missing required argument: conversationId.");

        var conversation = await conversationStore.GetAsync(conversationId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Conversation '{conversationId}' not found.");

        if (ReadString(arguments, "agentId") is { } requestedAgentId &&
            !string.Equals(conversation.AgentId.Value, requestedAgentId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Conversation '{conversationId}' not found for agent '{requestedAgentId}'.");

        return conversation;
    }

    private void EnsureCanAccess(AgentId targetAgentId)
    {
        if (accessLevel == ConversationAccessLevel.All)
            return;

        if (string.Equals(targetAgentId.Value, agentId.Value, StringComparison.OrdinalIgnoreCase))
            return;

        if (accessLevel == ConversationAccessLevel.Allowlist &&
            allowedAgents is not null &&
            allowedAgents.Any(a => string.Equals(a, targetAgentId.Value, StringComparison.OrdinalIgnoreCase)))
            return;

        throw new UnauthorizedAccessException($"Agent '{agentId}' does not have access to conversations for agent '{targetAgentId}'.");
    }

    private static object ToToolResponse(Conversation conversation) => new
    {
        Id = conversation.ConversationId.Value,
        ConversationId = conversation.ConversationId.Value,
        AgentId = conversation.AgentId.Value,
        DisplayName = conversation.Title,
        Title = conversation.Title,
        conversation.Purpose,
        conversation.Instructions,
        Status = conversation.Status.ToString(),
        conversation.IsDefault,
        ActiveSessionId = conversation.ActiveSessionId?.Value,
        conversation.CreatedAt,
        conversation.UpdatedAt
    };

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private async Task NotifyBestEffortAsync(string changeType, Conversation conversation, CancellationToken ct)
    {
        if (changeNotifier is null)
            return;
        try
        {
            await changeNotifier.NotifyConversationChangedAsync(changeType, conversation.AgentId.Value, conversation.ConversationId.Value, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Notification is best-effort: a SignalR hub failure must not fail the agent tool call.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>Conversation access level for the conversation tool.</summary>
public enum ConversationAccessLevel
{
    /// <summary>Agent can only access its own conversations.</summary>
    Own,
    /// <summary>Agent can access its own conversations plus those of explicitly allowed agents.</summary>
    Allowlist,
    /// <summary>Agent can access all conversations.</summary>
    All
}
