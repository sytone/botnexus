using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp;

internal static class McpServerWarmupCache
{
    private static readonly ConcurrentDictionary<string, WarmupEntry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static WarmupEntry EnsureStarted(
        string agentId,
        McpExtensionConfig config,
        ILogger logger)
    {
        var key = BuildKey(agentId, config);
        return Entries.GetOrAdd(key, _ =>
        {
            var manager = new McpServerManager(logger);
            var cts = new CancellationTokenSource();
            var entry = new WarmupEntry(manager, cts);
            entry.Start(config);
            return entry;
        });
    }

    public static async ValueTask DisposeAllAsync()
    {
        var entries = Entries.ToArray();
        Entries.Clear();

        foreach (var entry in entries)
            await entry.Value.DisposeAsync().ConfigureAwait(false);
    }

    internal static int Count => Entries.Count;

    private static string BuildKey(string agentId, McpExtensionConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonContext.Default.McpExtensionConfig);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return $"{agentId}:{hash}";
    }

    internal sealed class WarmupEntry : IAsyncDisposable
    {
        private readonly McpServerManager _manager;
        private readonly CancellationTokenSource _cts;
        private Task? _startTask;
        private IReadOnlyList<IAgentTool> _tools = [];

        public WarmupEntry(McpServerManager manager, CancellationTokenSource cts)
        {
            _manager = manager;
            _cts = cts;
        }

        public void Start(McpExtensionConfig config)
        {
            _startTask = Task.Run(async () =>
            {
                try
                {
                    _tools = await _manager.StartServersAsync(config, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    _tools = [];
                }
            }, _cts.Token);
        }

        public bool TryGetReadyTools(out IReadOnlyList<IAgentTool> tools)
        {
            if (_startTask is { IsCompletedSuccessfully: true })
            {
                tools = _tools;
                return true;
            }

            tools = [];
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_startTask is not null)
            {
                try
                {
                    await _startTask.ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup; startup failures are already logged by the manager.
                }
            }

            await _manager.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
