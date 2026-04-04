using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Tests.Extensions.Registrar;

public sealed class RegistrarEchoTool(string value) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "registrar_echo",
        "Echoes registrar-provided values for testing",
        new Dictionary<string, ToolParameterSchema>());

    public Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        => Task.FromResult($"registrar:{value}");
}
