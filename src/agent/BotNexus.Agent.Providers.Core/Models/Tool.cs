using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Models;

/// <summary>
/// Tool definition with JSON Schema parameters.
/// </summary>
public record Tool(
    string Name,
    string Description,
    JsonElement Parameters
);
