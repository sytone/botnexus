using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// The single provider/model preflight consumed by <c>create_agent</c> and <c>update_agent</c>
/// (#2649).
/// <para>
/// <see cref="AgentDescriptor.ApiProvider"/> is resolved at spawn time as a <b>model-registry
/// provider instance key</b> (<c>InProcessIsolationStrategy</c> calls
/// <c>ModelRegistry.GetModel(descriptor.ApiProvider, modelId)</c>). Validating the same field
/// against the API-<i>contract</i> registry instead - which enumerates values like
/// <c>github-copilot-messages</c> - rejected the only values that work and accepted values that
/// persisted a permanently unspawnable agent. This type validates against the registry the runtime
/// actually reads, and both tools call it, so the two cannot drift apart again.
/// </para>
/// </summary>
public static class AgentModelPreflight
{
    /// <summary>
    /// Maximum length of an operator-facing rejection produced here. The remedy lists come from a
    /// live registry that can hold hundreds of models; an unbounded list is not a usable message.
    /// </summary>
    public const int MaxMessageLength = 512;

    /// <summary>
    /// Returns a rejection message when <paramref name="descriptor"/>'s
    /// <see cref="AgentDescriptor.ApiProvider"/> / <see cref="AgentDescriptor.ModelId"/> pair is one
    /// the runtime positively cannot resolve, or <see langword="null"/> when the caller may proceed.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> or empty <paramref name="modelRegistry"/> yields
    /// <see langword="null"/> (proceed): a host that has registered no models cannot distinguish a
    /// typo from a provider it simply has not loaded yet, and refusing every agent in that state
    /// would be worse than the bug being fixed.
    /// </remarks>
    /// <param name="descriptor">The candidate descriptor, before persistence.</param>
    /// <param name="modelRegistry">The runtime model registry, or <see langword="null"/>.</param>
    public static string? ValidateResolvable(AgentDescriptor descriptor, ModelRegistry? modelRegistry)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ValidateResolvable(descriptor.ApiProvider, descriptor.ModelId, modelRegistry);
    }

    /// <summary>
    /// Field-level overload for callers that hold the raw arguments rather than a built descriptor.
    /// </summary>
    /// <param name="apiProvider">The provider instance key as supplied by the caller.</param>
    /// <param name="modelId">The model id as supplied by the caller.</param>
    /// <param name="modelRegistry">The runtime model registry, or <see langword="null"/>.</param>
    public static string? ValidateResolvable(string? apiProvider, string? modelId, ModelRegistry? modelRegistry)
    {
        var result = ModelPreflight.Resolve(modelRegistry, apiProvider, modelId);

        return result.Kind switch
        {
            ModelPreflightKind.UnknownProvider => ModelPreflight.FormatList(
                $"Unknown API provider '{apiProvider}'. Available providers: ",
                result.AvailableProviders,
                ".",
                MaxMessageLength),

            ModelPreflightKind.UnknownModel => ModelPreflight.FormatList(
                $"Model '{modelId}' is not registered for provider '{apiProvider}'. Available models: ",
                result.AvailableModels,
                ".",
                MaxMessageLength),

            _ => null
        };
    }
}
