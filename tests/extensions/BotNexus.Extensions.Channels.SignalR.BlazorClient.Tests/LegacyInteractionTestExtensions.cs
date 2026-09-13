using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Keeps pre-#3326 test call sites readable while routing every assertion through the unified
/// intent-bearing interaction seam. Production code has no mechanism-specific client methods.
/// </summary>
internal static class LegacyInteractionTestExtensions
{
    public static Task SendMessageAsync(
        this IAgentInteractionService interaction,
        string agentId,
        string conversationId,
        string content)
        => interaction.DeliverMessageAsync(agentId, conversationId, content);

    public static Task SendMessageAsync(
        this IAgentInteractionService interaction,
        string agentId,
        string conversationId,
        string content,
        IReadOnlyList<DraftAttachment> attachments)
        => interaction.DeliverMessageAsync(agentId, conversationId, content, InboundDeliveryMode.Auto, attachments);

    public static Task SteerAsync(
        this IAgentInteractionService interaction,
        string agentId,
        string conversationId,
        string content)
        => interaction.DeliverMessageAsync(agentId, conversationId, content, InboundDeliveryMode.Steer);

    public static Task SteerAsync(
        this IAgentInteractionService interaction,
        string agentId,
        string conversationId,
        string content,
        IReadOnlyList<DraftAttachment> attachments)
        => interaction.DeliverMessageAsync(agentId, conversationId, content, InboundDeliveryMode.Steer, attachments);

    public static Task InterruptAndSteerAsync(
        this IAgentInteractionService interaction,
        string agentId,
        string conversationId,
        string content)
        => interaction.DeliverMessageAsync(agentId, conversationId, content, InboundDeliveryMode.Interrupt);

    public static Task InterruptAndSteerAsync(
        this IAgentInteractionService interaction,
        string agentId,
        string conversationId,
        string content,
        IReadOnlyList<DraftAttachment> attachments)
        => interaction.DeliverMessageAsync(agentId, conversationId, content, InboundDeliveryMode.Interrupt, attachments);
}
