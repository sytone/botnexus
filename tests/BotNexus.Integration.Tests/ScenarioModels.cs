using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Integration.Tests;

public class ScenarioDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 30;
    public List<AgentDefinition> Agents { get; set; } = [];

    /// <summary>
    /// Sequential steps (legacy single-track mode).
    /// </summary>
    public List<ScenarioStep>? Steps { get; set; }

    /// <summary>
    /// Parallel step tracks — each track runs concurrently like a separate browser tab.
    /// Use this instead of Steps for true multi-agent concurrency testing.
    /// </summary>
    public List<StepTrack>? Tracks { get; set; }

    /// <summary>
    /// MCP test servers to start before the gateway.
    /// The gateway config is updated with their URLs before agent registration.
    /// </summary>
    [JsonPropertyName("mcp_servers")]
    public List<McpTestServerDef>? McpServers { get; set; }
}

/// <summary>
/// Defines a test MCP server to start alongside the gateway.
/// </summary>
public class McpTestServerDef
{
    public string Name { get; set; } = "";
}

/// <summary>
/// A named sequence of steps that runs on its own thread.
/// Each track is like one browser tab interacting with one agent.
/// </summary>
public class StepTrack
{
    public string Name { get; set; } = "";
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
    /// <summary>Extension config for the agent (e.g., MCP server references).</summary>
    public JsonElement? Extensions { get; set; }
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
    [JsonPropertyName("fast_step")]
    public string? FastStep { get; set; }
    [JsonPropertyName("slow_step")]
    public string? SlowStep { get; set; }
    public string? Description { get; set; }
    [JsonPropertyName("parallel_steps")]
    public List<ScenarioStep>? ParallelSteps { get; set; }
}

public class EventWait
{
    public string Type { get; set; } = "";
    [JsonPropertyName("from_step")]
    public string? FromStep { get; set; }
}
