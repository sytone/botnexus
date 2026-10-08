using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Search;

internal static class SearchableConversations
{
    internal static async Task<IReadOnlyList<Conversation>> ListAsync(
        IConversationStore store, SearchScope scope, CancellationToken cancellationToken,
        IAgentRegistry registry)
    {
        var conversations = new List<Conversation>();
        if (scope.IsAll)
            conversations.AddRange(await store.ListAsync(null, cancellationToken).ConfigureAwait(false));
        else
        {
            if (scope.Agents.Count == 0)
                return [];
            // Store partitions are case-sensitive; resolve authorized IDs to registered spelling
            // without enumerating unauthorized conversation partitions.
            foreach (var agent in registry.GetAll().Where(descriptor => scope.Allows(descriptor.AgentId))
                .Select(descriptor => descriptor.AgentId).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                conversations.AddRange(await store.ListAsync(agent, cancellationToken).ConfigureAwait(false));
            }
        }

        return conversations.Where(conversation => scope.Allows(conversation.AgentId)
                && conversation.Visibility != ConversationVisibility.InternalHidden)
            .DistinctBy(conversation => conversation.ConversationId).ToArray();
    }
}
