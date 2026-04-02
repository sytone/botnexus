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
    private readonly object _sync = new();
    private readonly List<IAgentRunner> _allRunners;
    private readonly Dictionary<string, IAgentRunner> _runnerByName;
    private readonly ILogger<AgentRouter> _logger;
    private readonly IAgentRunnerFactory? _runnerFactory;
    private string? _defaultAgentName;
    private bool _broadcastWhenUnspecified;

    public AgentRouter(
        IEnumerable<IAgentRunner> runners,
        IOptionsMonitor<BotNexusConfig> config,
        ILogger<AgentRouter> logger,
        IAgentRunnerFactory? runnerFactory = null)
        : this(runners, config.CurrentValue, logger, runnerFactory)
    {
    }

    private AgentRouter(
        IEnumerable<IAgentRunner> runners,
        BotNexusConfig cfg,
        ILogger<AgentRouter> logger,
        IAgentRunnerFactory? runnerFactory)
    {
        _logger = logger;
        _runnerFactory = runnerFactory;
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

        _broadcastWhenUnspecified = cfg.Gateway.BroadcastWhenAgentUnspecified;
        _defaultAgentName = string.IsNullOrWhiteSpace(cfg.Gateway.DefaultAgent)
            ? (cfg.Agents.Named.Keys.FirstOrDefault() ?? "default")
            : cfg.Gateway.DefaultAgent;
    }

    public void ReloadFromConfig(BotNexusConfig previous, BotNexusConfig current, IReadOnlyCollection<string> affectedAgents)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (_runnerFactory is null)
        {
            _logger.LogWarning("Agent config changed but router cannot rebuild runners without a runner factory.");
            return;
        }

        var allAgentNames = new HashSet<string>(current.Agents.Named.Keys, NameComparer);
        var removedAgents = previous.Agents.Named.Keys
            .Where(agent => !allAgentNames.Contains(agent))
            .ToList();

        lock (_sync)
        {
            foreach (var removedAgent in removedAgents)
            {
                _runnerByName.Remove(removedAgent);
                _allRunners.RemoveAll(r => NameComparer.Equals(r.AgentName, removedAgent));
            }

            foreach (var agentName in affectedAgents.Where(allAgentNames.Contains))
            {
                var runner = _runnerFactory.Create(agentName);
                _runnerByName[agentName] = runner;
                _allRunners.RemoveAll(r => NameComparer.Equals(r.AgentName, agentName));
                _allRunners.Add(runner);
            }

            _broadcastWhenUnspecified = current.Gateway.BroadcastWhenAgentUnspecified;
            _defaultAgentName = string.IsNullOrWhiteSpace(current.Gateway.DefaultAgent)
                ? (current.Agents.Named.Keys.FirstOrDefault() ?? "default")
                : current.Gateway.DefaultAgent;
        }
    }

    public IReadOnlyList<IAgentRunner> ResolveTargets(InboundMessage message)
    {
        lock (_sync)
        {
            if (_allRunners.Count == 0)
                return [];

            var requestedAgents = GetRequestedAgents(message);
            if (requestedAgents.Count > 0)
            {
                if (requestedAgents.Any(agent => BroadcastTokens.Contains(agent)))
                    return [.. _allRunners];

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
                return [.. _allRunners];

            if (!string.IsNullOrWhiteSpace(_defaultAgentName) &&
                _runnerByName.TryGetValue(_defaultAgentName, out var defaultRunner))
            {
                return [defaultRunner];
            }

            return [.. _allRunners];
        }
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
