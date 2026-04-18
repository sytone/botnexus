using System.Data;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.AgentCore.Tests.TestUtils;

internal sealed class CalculateTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "expression": { "type": "string" }
          },
          "required": ["expression"]
        }
        """).RootElement.Clone();

    public string Name => "calculate";

    public string Label => "Calculate";

    public Tool Definition => new("calculate", "Evaluate arithmetic", Schema);

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
        var expression = arguments.TryGetValue("expression", out var value)
            ? value?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new InvalidOperationException("Missing expression argument.");
        }

        var computed = new DataTable().Compute(expression, null)?.ToString() ?? string.Empty;
        return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, computed)]));
    }
}
