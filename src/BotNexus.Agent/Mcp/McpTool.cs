using System.Text.Json.Nodes;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Mcp;

/// <summary>
/// Wraps a single tool exposed by an MCP server as a BotNexus <see cref="BotNexus.Core.Abstractions.ITool"/>.
/// Each instance represents one remote tool and delegates execution to <see cref="McpClient"/>.
/// </summary>
public sealed class McpTool : Tools.ToolBase
{
    private readonly IMcpClient _client;
    private readonly string _remoteName;

    /// <summary>The tool name exposed to the LLM (may include server prefix).</summary>
    public override ToolDefinition Definition { get; }

    /// <summary>
    /// Initialises an MCP tool wrapper.
    /// </summary>
    /// <param name="client">The MCP client managing the connection to the remote server.</param>
    /// <param name="remoteName">The bare tool name on the MCP server.</param>
    /// <param name="toolDefinition">The <see cref="ToolDefinition"/> built from the MCP schema.</param>
    /// <param name="logger">Optional logger.</param>
    public McpTool(IMcpClient client, string remoteName, ToolDefinition toolDefinition, ILogger? logger = null)
        : base(logger)
    {
        _client = client;
        _remoteName = remoteName;
        Definition = toolDefinition;
    }

    /// <inheritdoc/>
    protected override async Task<string> ExecuteCoreAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        // Convert the arguments dictionary to a JSON object for the MCP wire format
        var jsonArgs = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            jsonArgs[key] = value switch
            {
                null => null,
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                float f => JsonValue.Create(f),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(value.ToString())
            };
        }

        return await _client.CallToolAsync(_remoteName, jsonArgs, cancellationToken).ConfigureAwait(false);
    }
}
