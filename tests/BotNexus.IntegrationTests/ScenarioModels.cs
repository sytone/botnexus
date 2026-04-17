using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.IntegrationTests;

public class ScenarioDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 30;
    public List<AgentDefinition> Agents { get; set; } = [];
    public List<ScenarioStep> Steps { get; set; } = [];
}

public class AgentDefinition
{
    public string Id { get; set; } = "";
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";
    public string Model { get; set; } = "gpt-4.1";
    public string Provider { get; set; } = "github-copilot";
    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }
}

public class ScenarioStep
{
    public string Action { get; set; } = "";
    public string? Agent { get; set; }
    public string? Content { get; set; }
    public string? Label { get; set; }
    public string? Type { get; set; }
    [JsonPropertyName("from_step")]
    public string? FromStep { get; set; }
    public string? Step { get; set; }
    public string? Condition { get; set; }
    public List<string>? Steps { get; set; }
    public List<EventWait>? Events { get; set; }
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 15;
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
    [JsonPropertyName("expected_status")]
    public int ExpectedStatus { get; set; } = 200;
    [JsonPropertyName("expected_contains")]
    public string? ExpectedContains { get; set; }
}

public class EventWait
{
    public string Type { get; set; } = "";
    [JsonPropertyName("from_step")]
    public string? FromStep { get; set; }
}
