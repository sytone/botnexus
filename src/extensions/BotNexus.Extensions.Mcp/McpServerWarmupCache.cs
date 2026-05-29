using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Mcp;

internal static class McpServerWarmupCache
{
    private static readonly ConcurrentDictionary<string, WarmupEntry> WarmupEntriesByKey = new(StringComparer.OrdinalIgnoreCase);

    public static WarmupEntry EnsureStarted(
        string agentId,
        McpExtensionConfig config,
        ILogger logger)
    {
        var key = BuildKey(agentId, config);
        return WarmupEntriesByKey.GetOrAdd(key, _ =>
        {
            var manager = new McpServerManager(logger);
            var cts = new CancellationTokenSource();
            var entry = new WarmupEntry(manager, cts, logger);
            entry.Start(config);
            return entry;
        });
    }

    public static async ValueTask DisposeAllAsync()
    {
        var entries = WarmupEntriesByKey.ToArray();
        WarmupEntriesByKey.Clear();

        foreach (var entry in entries)
            await entry.Value.DisposeAsync().ConfigureAwait(false);
    }

    internal static int Count => WarmupEntriesByKey.Count;

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
        private readonly ILogger _logger;
        private Task? _startTask;
        private IReadOnlyList<IAgentTool> _tools = [];

        public WarmupEntry(McpServerManager manager, CancellationTokenSource cts, ILogger logger)
        {
            _manager = manager;
            _cts = cts;
            _logger = logger;
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
                catch (Exception ex)
                {
                    _tools = [];
                    _logger.LogWarning(ex, "MCP server warmup failed unexpectedly.");
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
                    // Best-effort cleanup; startup failures are logged by the warmup task or manager.
                }
            }

            await _manager.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
