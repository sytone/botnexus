using System.Text.Json;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Tests.TestUtils;

internal sealed class GetCurrentTimeTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {}
        }
        """).RootElement.Clone();

    public string Name => "get_current_time";

    public string Label => "Get Current Time";

    public Tool Definition => new("get_current_time", "Get current UTC time", Schema);

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(arguments);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, now)]));
    }
}
