using BotNexus.Agent.Core.Tools;
using BotNexus.Extensions.Mcp.Plugins;

namespace BotNexus.Extensions.Mcp.Tests.Plugins;

/// <summary>
/// Records what was asked of the real server manager without spawning a process, so the
/// registration policy under test is the scoping / trust / ownership logic and nothing else.
/// </summary>
internal sealed class RecordingMcpServerHost : IMcpServerHost
{
    private readonly List<string> _running = [];

    public List<string> Started { get; } = [];

    public List<string> Stopped { get; } = [];

    public IReadOnlyList<string> Running
    {
        get
        {
            lock (_running) { return [.. _running]; }
        }
    }

    public Task<IReadOnlyList<IAgentTool>> StartServerAsync(
        string serverId,
        McpServerConfig serverConfig,
        bool useToolPrefix,
        CancellationToken cancellationToken = default)
    {
        lock (_running)
        {
            Started.Add(serverId);
            _running.Add(serverId);
        }

        return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
    }

    public Task<IReadOnlyList<string>> StopServersAsync(
        Func<string, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        lock (_running)
        {
            var matched = _running.Where(predicate).ToList();
            foreach (var id in matched)
            {
                _running.Remove(id);
                Stopped.Add(id);
            }

            return Task.FromResult<IReadOnlyList<string>>(matched);
        }
    }
}

/// <summary>Trust evaluator that answers from a fixed verdict, for the Enforce/Warn matrix.</summary>
internal sealed class StubPluginTrustEvaluator(PluginTrustMode mode, bool trusted, string reason = "content hash mismatch")
    : IPluginTrustEvaluator
{
    public List<string> Evaluated { get; } = [];

    public PluginTrustMode Mode => mode;

    public PluginTrustDecision Evaluate(string pluginName, string pluginDirectory)
    {
        Evaluated.Add(pluginName);
        return trusted ? PluginTrustDecision.Trust : PluginTrustDecision.Deny(reason);
    }
}
