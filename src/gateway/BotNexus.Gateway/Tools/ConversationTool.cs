using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

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
    IReadOnlyList<string>? allowedAgents = null) : IAgentTool
{
    public string Name => "conversation";
    public string Label => "Conversation Context";

    public Tool Definition => new(
        Name,
        "Get, list, create, and annotate persistent conversation context.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["get", "set_title", "set_purpose", "list", "new"],
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
            !action.Equals("new", StringComparison.OrdinalIgnoreCase))
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
            _ => throw new InvalidOperationException($"Unsupported conversation action '{action}'.")
        };
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
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = targetAgentId,
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
            Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
            Status = ConversationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        var created = await conversationStore.CreateAsync(conversation, ct).ConfigureAwait(false);
        return TextResult(JsonSerializer.Serialize(ToToolResponse(created), JsonOptions));
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
