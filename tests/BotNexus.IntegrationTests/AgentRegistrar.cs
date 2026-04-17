using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BotNexus.IntegrationTests;

public static class AgentRegistrar
{
    public static async Task RegisterAsync(
        HttpClient client,
        AgentDefinition agent,
        Dictionary<string, string>? mcpUrls = null,
        CancellationToken ct = default)
    {
        // Build extension config, substituting MCP server URL placeholders like {{mcp:ping}}
        object? extensionConfig = null;
        if (agent.Extensions.HasValue)
        {
            var extJson = agent.Extensions.Value.GetRawText();
            if (mcpUrls is not null)
            {
                foreach (var (name, url) in mcpUrls)
                {
                    extJson = extJson.Replace($"{{{{mcp:{name}}}}}", url);
                }
            }
            extensionConfig = JsonSerializer.Deserialize<object>(extJson);
        }

        var descriptor = new
        {
            agentId = agent.Id,
            displayName = agent.DisplayName,
            modelId = agent.Model,
            apiProvider = agent.Provider,
            isolationStrategy = "in-process",
            systemPrompt = agent.SystemPrompt,
            extensionConfig = extensionConfig ?? new Dictionary<string, object>()
        };

        var response = await client.PostAsJsonAsync("/api/agents", descriptor, ct);
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"Failed to register agent '{agent.Id}': {response.StatusCode} — {body}");
        }
    }
}
