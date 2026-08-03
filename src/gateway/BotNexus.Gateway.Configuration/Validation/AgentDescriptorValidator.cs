using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

public static class AgentDescriptorValidator
{
    public static IReadOnlyList<string> Validate(
        AgentDescriptor descriptor,
        IEnumerable<string>? availableIsolationStrategies = null,
        ModelRegistry? modelRegistry = null)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            errors.Add("DisplayName is required.");

        if (string.IsNullOrWhiteSpace(descriptor.ModelId))
            errors.Add("ModelId is required.");

        if (string.IsNullOrWhiteSpace(descriptor.ApiProvider))
            errors.Add("ApiProvider is required.");

        if (descriptor.MaxConcurrentSessions < 0)
            errors.Add("MaxConcurrentSessions must be >= 0.");

        if (string.IsNullOrWhiteSpace(descriptor.IsolationStrategy))
        {
            errors.Add("IsolationStrategy is required.");
        }
        else if (availableIsolationStrategies is not null)
        {
            var strategyNames = availableIsolationStrategies.ToArray();
            if (strategyNames.Length > 0 && !strategyNames.Contains(descriptor.IsolationStrategy, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"IsolationStrategy '{descriptor.IsolationStrategy}' is not registered. " +
                    $"Available: {string.Join(", ", strategyNames.Order(StringComparer.OrdinalIgnoreCase))}.");
            }
        }

        errors.AddRange(ValidateModelCapabilities(descriptor, modelRegistry));

        return errors;
    }

    /// <summary>
    /// Validates the agent-level thinking and context-window defaults (PBI4 / #1705) against
    /// the capability set the selected model advertises. This is the enforcement half of the
    /// agent layer of the three-layer <c>ModelOverrideResolver</c> stack: a value the model
    /// cannot honour (a thinking level on a non-reasoning model, a context window the model
    /// does not expose) is rejected here rather than silently ignored downstream.
    /// </summary>
    /// <remarks>
    /// When <paramref name="modelRegistry"/> is <see langword="null"/> (unit paths that do not
    /// wire a registry) or the model is not registered for the descriptor's provider, capability
    /// checks are skipped - an unregistered model is a separate error surfaced at spawn time.
    /// </remarks>
    public static IReadOnlyList<string> ValidateModelCapabilities(
        AgentDescriptor descriptor,
        ModelRegistry? modelRegistry)
    {
        List<string> errors = [];

        var hasThinking = !string.IsNullOrWhiteSpace(descriptor.Thinking);
        var hasContext = descriptor.ContextWindow is not null;
        if (!hasThinking && !hasContext)
            return errors;

        // Parse the thinking string up front so a malformed value is reported even without a registry.
        ThinkingLevel? parsedThinking = null;
        if (hasThinking)
        {
            if (TryParseThinking(descriptor.Thinking!, out var level))
                parsedThinking = level;
            else
                errors.Add(
                    $"Thinking '{descriptor.Thinking}' is not a recognised thinking level. " +
                    "Valid values: minimal, low, medium, high, xhigh, max.");
        }

        if (modelRegistry is null)
            return errors;

        var model = modelRegistry.GetModel(descriptor.ApiProvider, descriptor.ModelId);
        if (model is null)
            return errors; // Unregistered model handled elsewhere; nothing to validate against here.

        if (parsedThinking is { } thinking)
        {
            var supported = ModelRegistry.GetSupportedThinkingLevels(model);
            if (!supported.Contains(thinking))
            {
                errors.Add(supported.Count == 0
                    ? $"Model '{descriptor.ModelId}' does not support thinking; remove the thinking level."
                    : $"Thinking level '{descriptor.Thinking}' is not supported by model '{descriptor.ModelId}'. " +
                      $"Supported: {string.Join(", ", supported.Select(ThinkingToWire))}.");
            }
        }

        if (descriptor.ContextWindow is { } contextWindow)
        {
            var supported = ModelRegistry.GetSupportedContextSizes(model);
            if (!supported.Contains(contextWindow))
            {
                errors.Add(
                    $"Context window '{contextWindow}' is not supported by model '{descriptor.ModelId}'. " +
                    $"Supported: {string.Join(", ", supported)}.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Parses a wire-form thinking string (as stored on <see cref="AgentDescriptor.Thinking"/>)
    /// into the enum, accepting the JSON member names (<c>xhigh</c>) as well as the C# enum
    /// names (<c>ExtraHigh</c>) for tolerance.
    /// </summary>
    public static bool TryParseThinking(string value, out ThinkingLevel level)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "minimal": level = ThinkingLevel.Minimal; return true;
            case "low": level = ThinkingLevel.Low; return true;
            case "medium": level = ThinkingLevel.Medium; return true;
            case "high": level = ThinkingLevel.High; return true;
            case "xhigh":
            case "extrahigh": level = ThinkingLevel.ExtraHigh; return true;
            case "max": level = ThinkingLevel.Max; return true;
            default:
                return Enum.TryParse(value, ignoreCase: true, out level)
                    && Enum.IsDefined(level);
        }
    }

    private static string ThinkingToWire(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => level.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Configuration-source variant of <see cref="Validate(AgentDescriptor, IEnumerable{string}?)"/>
    /// that additionally rejects any descriptor declaring <see cref="AgentKind.SubAgent"/>.
    /// Sub-agents are runtime-only — they are created exclusively by
    /// <c>DefaultSubAgentManager.SpawnAsync</c> on the spawn path and never persisted as
    /// configuration. A config file or REST payload that asserts <c>Kind = SubAgent</c> is
    /// either a misconfiguration (a named agent the operator wanted to register would be
    /// silently denied <c>spawn_subagent</c>) or a privilege-confusion attempt; either way
    /// the safer response is to reject at load time.
    /// </summary>
    public static IReadOnlyList<string> ValidateForConfig(
        AgentDescriptor descriptor,
        IEnumerable<string>? availableIsolationStrategies = null,
        ModelRegistry? modelRegistry = null)
    {
        var errors = new List<string>(Validate(descriptor, availableIsolationStrategies, modelRegistry));

        if (descriptor.Kind == AgentKind.SubAgent)
        {
            errors.Add(
                "Kind = SubAgent is reserved for runtime-spawned sub-agents and may not be " +
                "declared in configuration or REST payloads. Remove the 'kind' property or set " +
                "it to 'Named'.");
        }

        return errors;
    }
}
