using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Converts AgentContext to provider Context for LLM invocation.
/// </summary>
/// <remarks>
/// Calls ConvertToLlmDelegate to transform AgentMessage[] to provider Message[].
/// Maps IAgentTool[] to provider Tool[] schemas.
/// </remarks>
internal static class ContextConverter
{
    /// <summary>
    /// Convert agent context to provider context.
    /// </summary>
    /// <param name="agentContext">The agent context snapshot.</param>
    /// <param name="convertToLlm">The message conversion delegate.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A provider Context ready for LLM invocation.</returns>
    public static async Task<Context> ToProviderContext(
        AgentContext agentContext,
        ConvertToLlmDelegate convertToLlm,
        CancellationToken ct)
    {
        var providerMessages = await convertToLlm(agentContext.Messages, ct).ConfigureAwait(false);
        var tools = agentContext.Tools.Count == 0
            ? null
            : agentContext.Tools.Select(ToProviderTool).ToList();

        return new Context(agentContext.SystemPrompt, providerMessages, tools);
    }

    /// <summary>
    /// Executes to provider tool.
    /// </summary>
    /// <param name="agentTool">The agent tool.</param>
    /// <returns>The to provider tool result.</returns>
    public static Tool ToProviderTool(IAgentTool agentTool)
    {
        return new Tool(
            Name: agentTool.Definition.Name,
            Description: agentTool.Definition.Description,
            Parameters: agentTool.Definition.Parameters);
    }
}
