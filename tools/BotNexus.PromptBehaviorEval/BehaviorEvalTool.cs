using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.PromptBehaviorEval;

internal sealed class BehaviorEvalTool : IAgentTool
{
    private readonly Action<BehaviorObservation> _observe;
    private readonly Func<IReadOnlyDictionary<string, object?>, ToolExecution> _execute;

    public BehaviorEvalTool(
        string name,
        string description,
        JsonElement parameters,
        Action<BehaviorObservation> observe,
        Func<IReadOnlyDictionary<string, object?>, ToolExecution> execute)
    {
        Name = name;
        Label = name;
        Definition = new Tool(name, description, parameters);
        _observe = observe;
        _execute = execute;
    }

    public string Name { get; }
    public string Label { get; }
    public Tool Definition { get; }
    public string ContentSource => ToolContentSource.Local;

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) => Task.FromResult(arguments);

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var execution = _execute(arguments);
        _observe(BehaviorObservation.Tool(Name, execution.Detail, execution.Accepted));
        return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, execution.Result)]));
    }
}

/// <summary>Outcome returned by one deterministic fixture operation.</summary>
public sealed record ToolExecution(string Result, string? Detail = null, bool Accepted = true);
