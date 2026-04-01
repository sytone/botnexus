using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

/// <summary>Default routing implementation for dispatching messages to agent runners.</summary>
public sealed class AgentRouter : IAgentRouter
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> BroadcastTokens = new(NameComparer) { "all", "*" };
    private readonly IReadOnlyList<IAgentRunner> _allRunners;
    private readonly Dictionary<string, IAgentRunner> _runnerByName;
    private readonly ILogger<AgentRouter> _logger;
    private readonly string? _defaultAgentName;
    private readonly bool _broadcastWhenUnspecified;

    public AgentRouter(
        IEnumerable<IAgentRunner> runners,
        IOptions<BotNexusConfig> config,
        ILogger<AgentRouter> logger)
    {
        _logger = logger;
        _allRunners = [.. runners];
        _runnerByName = new Dictionary<string, IAgentRunner>(NameComparer);

        foreach (var runner in _allRunners)
        {
            if (_runnerByName.ContainsKey(runner.AgentName))
            {
                _logger.LogWarning("Duplicate agent runner registration for {AgentName}; ignoring duplicate", runner.AgentName);
                continue;
            }

            _runnerByName[runner.AgentName] = runner;
        }

        var cfg = config.Value;
        _broadcastWhenUnspecified = cfg.Gateway.BroadcastWhenAgentUnspecified;
        _defaultAgentName = string.IsNullOrWhiteSpace(cfg.Gateway.DefaultAgent)
            ? (cfg.Agents.Named.Keys.FirstOrDefault() ?? "default")
            : cfg.Gateway.DefaultAgent;
    }

    public IReadOnlyList<IAgentRunner> ResolveTargets(InboundMessage message)
    {
        if (_allRunners.Count == 0)
            return [];

        var requestedAgents = GetRequestedAgents(message);
        if (requestedAgents.Count > 0)
        {
            if (requestedAgents.Any(agent => BroadcastTokens.Contains(agent)))
                return _allRunners;

            var selected = new List<IAgentRunner>();
            foreach (var requestedAgent in requestedAgents)
            {
                if (_runnerByName.TryGetValue(requestedAgent, out var runner))
                {
                    selected.Add(runner);
                }
                else
                {
                    _logger.LogWarning("No registered agent runner found for agent {AgentName}", requestedAgent);
                }
            }

            if (selected.Count > 0)
                return selected;
        }

        if (_broadcastWhenUnspecified)
            return _allRunners;

        if (!string.IsNullOrWhiteSpace(_defaultAgentName) &&
            _runnerByName.TryGetValue(_defaultAgentName, out var defaultRunner))
        {
            return [defaultRunner];
        }

        return _allRunners;
    }

    private static IReadOnlyList<string> GetRequestedAgents(InboundMessage message)
    {
        if (message.Metadata.Count == 0)
            return [];

        foreach (var (key, value) in message.Metadata)
        {
            if (!NameComparer.Equals(key, "agent") &&
                !NameComparer.Equals(key, "agent_name") &&
                !NameComparer.Equals(key, "agentName"))
            {
                continue;
            }

            return ParseAgentNames(value);
        }

        return [];
    }

    private static IReadOnlyList<string> ParseAgentNames(object? value)
    {
        if (value is null)
            return [];

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => ParseAgentNames(element.GetString()),
                JsonValueKind.Array => element.EnumerateArray()
                    .SelectMany(item => ParseAgentNames(item))
                    .Distinct(NameComparer)
                    .ToList(),
                _ => []
            };
        }

        if (value is string text)
        {
            return text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(agent => !string.IsNullOrWhiteSpace(agent))
                .Distinct(NameComparer)
                .ToList();
        }

        if (value is IEnumerable<string> stringList)
        {
            return stringList
                .SelectMany(ParseAgentNames)
                .Distinct(NameComparer)
                .ToList();
        }

        if (value is IEnumerable<object?> objectList)
        {
            return objectList
                .SelectMany(ParseAgentNames)
                .Distinct(NameComparer)
                .ToList();
        }

        return ParseAgentNames(Convert.ToString(value));
    }
}
