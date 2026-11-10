using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>Stable built-in sidebar identities, independent of labels and custom section ids.</summary>
[JsonConverter(typeof(BuiltInConversationSectionJsonConverter))]
public enum BuiltInConversationSection
{
    /// <summary>Conversations retaining their pinned attribute.</summary>
    Pinned,
    /// <summary>Conversations with no more specific presentation home.</summary>
    Conversations,
    /// <summary>Conversations originating from scheduled work.</summary>
    Scheduled,
    /// <summary>Conversations originating from webhooks.</summary>
    Webhooks
}

/// <summary>Uses names on the wire and rejects numeric enum values.</summary>
public sealed class BuiltInConversationSectionJsonConverter() : JsonStringEnumConverter<BuiltInConversationSection>(allowIntegerValues: false)
{
}
