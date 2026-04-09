using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Providers.Core.Models;

namespace BotNexus.Extensions.Mcp;

/// <summary>
/// Wraps an MCP tool definition as a native BotNexus <see cref="IAgentTool"/>.
/// </summary>
public sealed class McpBridgedTool : IAgentTool
{
    private readonly McpClient _client;
    private readonly McpToolDefinition _definition;
    private readonly bool _usePrefix;

    public McpBridgedTool(McpClient client, McpToolDefinition definition, bool usePrefix = true)
    {
        _client = client;
        _definition = definition;
        _usePrefix = usePrefix;
    }

    /// <inheritdoc />
    public string Name => _usePrefix
        ? $"{_client.ServerId}_{_definition.Name}"
        : _definition.Name;

    /// <inheritdoc />
    public string Label => _definition.Name;

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        _definition.Description ?? string.Empty,
        _definition.InputSchema);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var argsElement = JsonSerializer.SerializeToElement(arguments);

        McpToolCallResult result;
        try
        {
            result = await _client.CallToolAsync(_definition.Name, argsElement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            return new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"MCP error ({ex.Code}): {ex.Message}")]);
        }

        var contentBlocks = new List<AgentToolContent>();
        foreach (var content in result.Content)
        {
            if (content.Type is "text" && content.Text is not null)
            {
                contentBlocks.Add(new AgentToolContent(AgentToolContentType.Text, content.Text));
            }
            else if (content.Type is "image" && content.Text is not null)
            {
                contentBlocks.Add(new AgentToolContent(AgentToolContentType.Image, content.Text));
            }
        }

        if (contentBlocks.Count == 0)
        {
            contentBlocks.Add(new AgentToolContent(AgentToolContentType.Text, "[no content]"));
        }

        return new AgentToolResult(contentBlocks);
    }
}
