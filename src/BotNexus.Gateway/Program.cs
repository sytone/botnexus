using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BotNexus.Agent;
using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Diagnostics;
using BotNexus.Gateway;
using BotNexus.Gateway.HealthChecks;
using BotNexus.Providers.Base;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var botNexusHome = BotNexusHome.Initialize();
builder.Configuration.AddJsonFile(
    Path.Combine(botNexusHome, "config.json"),
    optional: true,
    reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
var logFilePath = Path.Combine(botNexusHome, "logs", "botnexus-.log");
builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            path: logFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true);
});

builder.Services.AddBotNexus(builder.Configuration);

// Bind Kestrel to the configured gateway address
var gatewayCfg = builder.Configuration
    .GetSection($"{BotNexusConfig.SectionName}:Gateway")
    .Get<GatewayConfig>() ?? new GatewayConfig();

builder.WebHost.UseUrls($"http://{gatewayCfg.Host}:{gatewayCfg.Port}");

var app = builder.Build();
var startedAt = DateTimeOffset.UtcNow;
app.Logger.LogInformation("BotNexus home: {path}", botNexusHome);

// JSON options for REST API responses
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};

// --- Static files for the Web UI ---
var webUiPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(webUiPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new PhysicalFileProvider(webUiPath)
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webUiPath)
    });
}

var webSocketPath = string.IsNullOrWhiteSpace(gatewayCfg.WebSocketPath) ? "/ws" : gatewayCfg.WebSocketPath;
app.UseWhen(
    ctx =>
        ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        ctx.Request.Path.StartsWithSegments(webSocketPath, StringComparison.OrdinalIgnoreCase),
    branch => branch.UseMiddleware<ApiKeyAuthenticationMiddleware>());

// --- Health ---
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponse,
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status200OK
    }
});

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponse
});

// --- REST API: Sessions ---
app.MapGet("/api/sessions", async (ISessionManager sessionManager, IOptions<BotNexusConfig> config, HttpContext context) =>
{
    var defaultAgent = config.Value.Agents.Named.Keys.FirstOrDefault() ?? "default";
    var includeHidden = context.Request.Query.TryGetValue("includeHidden", out var value) &&
                        bool.TryParse(value, out var hidden) && hidden;

    var keys = await sessionManager.ListKeysAsync();
    var sessions = new List<object>();
    foreach (var key in keys)
    {
        var isHidden = await sessionManager.IsHiddenAsync(key);
        if (isHidden && !includeHidden)
            continue;

        var session = await sessionManager.GetOrCreateAsync(key, defaultAgent);
        sessions.Add(new
        {
            key = session.Key,
            agentName = session.AgentName,
            model = session.Model,
            createdAt = session.CreatedAt,
            updatedAt = session.UpdatedAt,
            messageCount = session.History.Count,
            channel = session.Key.Contains(':') ? session.Key[..session.Key.IndexOf(':')] : "unknown",
            hidden = isHidden
        });
    }
    return Results.Json(sessions, jsonOptions);
});

app.MapGet("/api/sessions/{*key}", async (string key, ISessionManager sessionManager, IOptions<BotNexusConfig> config) =>
{
    var decoded = Uri.UnescapeDataString(key);
    var defaultAgent = config.Value.Agents.Named.Keys.FirstOrDefault() ?? "default";
    var session = await sessionManager.GetOrCreateAsync(decoded, defaultAgent);
    if (session.History.Count == 0 && session.CreatedAt == session.UpdatedAt)
        return Results.NotFound(new { error = "Session not found" });

    var isHidden = await sessionManager.IsHiddenAsync(decoded);
    return Results.Json(new
    {
        key = session.Key,
        agentName = session.AgentName,
        model = session.Model,
        channel = session.Key.Contains(':') ? session.Key[..session.Key.IndexOf(':')] : "unknown",
        createdAt = session.CreatedAt,
        updatedAt = session.UpdatedAt,
        hidden = isHidden,
        history = session.History.Select(e => new
        {
            role = e.Role.ToString().ToLowerInvariant(),
            content = e.Content,
            timestamp = e.Timestamp,
            toolName = e.ToolName,
            toolCallId = e.ToolCallId,
            toolCalls = e.ToolCalls?.Select(tc => new
            {
                id = tc.Id,
                name = tc.ToolName,
                arguments = tc.Arguments
            })
        })
    }, jsonOptions);
});

app.MapPatch("/api/sessions/{*key}", async (string key, HttpRequest request, ISessionManager sessionManager) =>
{
    var decoded = Uri.UnescapeDataString(key);
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(body);
    
    if (json != null && json.TryGetValue("hidden", out var hiddenObj))
    {
        var hidden = hiddenObj is JsonElement el ? el.GetBoolean() : Convert.ToBoolean(hiddenObj);
        await sessionManager.SetHiddenAsync(decoded, hidden);
        return Results.Json(new { key = decoded, hidden }, jsonOptions);
    }
    
    return Results.BadRequest(new { error = "Missing 'hidden' property in request body" });
});

// --- REST API: Channels ---
app.MapGet("/api/channels", (ChannelManager channelManager) =>
{
    var channels = channelManager.Channels.Select(c => new
    {
        name = c.Name,
        displayName = c.DisplayName,
        isRunning = c.IsRunning,
        supportsStreaming = c.SupportsStreaming
    });
    return Results.Json(channels, jsonOptions);
});

// --- REST API: Agents ---
app.MapGet("/api/agents", (IOptions<BotNexusConfig> config) =>
{
    var agentDefaults = config.Value.Agents;
    var agents = new List<object>();

    if (agentDefaults.Named.Count == 0)
    {
        agents.Add(new
        {
            name = "default",
            model = agentDefaults.Model,
            maxTokens = agentDefaults.MaxTokens,
            temperature = agentDefaults.Temperature,
            maxToolIterations = agentDefaults.MaxToolIterations,
            timezone = agentDefaults.Timezone
        });
    }

    foreach (var (agentName, agentCfg) in agentDefaults.Named)
    {
        agents.Add(new
        {
            name = agentName,
            model = agentCfg.Model ?? agentDefaults.Model,
            maxTokens = agentCfg.MaxTokens ?? agentDefaults.MaxTokens,
            temperature = agentCfg.Temperature ?? agentDefaults.Temperature,
            maxToolIterations = agentCfg.MaxToolIterations ?? agentDefaults.MaxToolIterations,
            timezone = agentCfg.Timezone ?? agentDefaults.Timezone
        });
    }

    return Results.Json(agents, jsonOptions);
});

app.MapGet("/api/agents/{name}", (string name, IOptions<BotNexusConfig> config) =>
{
    var agentDefaults = config.Value.Agents;
    
    if (!agentDefaults.Named.TryGetValue(name, out var agentCfg))
        return Results.Json(new { error = $"Agent '{name}' not found." }, jsonOptions, statusCode: 404);

    return Results.Json(new
    {
        name,
        systemPrompt = agentCfg.SystemPrompt,
        systemPromptFile = agentCfg.SystemPromptFile,
        workspace = agentCfg.Workspace,
        model = agentCfg.Model ?? agentDefaults.Model,
        provider = agentCfg.Provider,
        maxTokens = agentCfg.MaxTokens ?? agentDefaults.MaxTokens,
        temperature = agentCfg.Temperature ?? agentDefaults.Temperature,
        maxToolIterations = agentCfg.MaxToolIterations ?? agentDefaults.MaxToolIterations,
        timezone = agentCfg.Timezone ?? agentDefaults.Timezone,
        enableMemory = agentCfg.EnableMemory,
        maxContextFileChars = agentCfg.MaxContextFileChars,
        consolidationModel = agentCfg.ConsolidationModel,
        memoryConsolidationIntervalHours = agentCfg.MemoryConsolidationIntervalHours,
        autoLoadMemory = agentCfg.AutoLoadMemory,
        mcpServers = agentCfg.McpServers,
        skills = agentCfg.Skills,
        disallowedTools = agentCfg.DisallowedTools
    }, jsonOptions);
});

app.MapPost("/api/agents", async (HttpContext httpContext, IOptions<BotNexusConfig> config, IAgentWorkspaceFactory workspaceFactory) =>
{
    CreateAgentRequest? body;
    try
    {
        body = await httpContext.Request.ReadFromJsonAsync<CreateAgentRequest>(jsonOptions);
    }
    catch
    {
        return Results.Json(new { error = "Invalid JSON body." }, jsonOptions, statusCode: 400);
    }

    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.Json(new { error = "Agent name is required." }, jsonOptions, statusCode: 400);

    var agentId = NormalizeAgentId(body.Name);
    if (string.IsNullOrWhiteSpace(agentId))
        return Results.Json(new { error = "Invalid agent name." }, jsonOptions, statusCode: 400);

    var agentDefaults = config.Value.Agents;
    if (agentDefaults.Named.ContainsKey(agentId))
        return Results.Json(new { error = $"Agent '{agentId}' already exists." }, jsonOptions, statusCode: 409);

    var configPath = Path.Combine(botNexusHome, "config.json");
    var configJson = await File.ReadAllTextAsync(configPath);
    var configDoc = JsonDocument.Parse(configJson);
    var rootElement = configDoc.RootElement;

    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    
    writer.WriteStartObject();
    foreach (var property in rootElement.EnumerateObject())
    {
        if (property.Name == "BotNexus")
        {
            writer.WritePropertyName("BotNexus");
            writer.WriteStartObject();
            foreach (var bnProperty in property.Value.EnumerateObject())
            {
                if (bnProperty.Name == "Agents")
                {
                    writer.WritePropertyName("Agents");
                    writer.WriteStartObject();
                    foreach (var agentProperty in bnProperty.Value.EnumerateObject())
                    {
                        if (agentProperty.Name == "Named")
                        {
                            writer.WritePropertyName("Named");
                            writer.WriteStartObject();
                            
                            // Copy existing agents
                            foreach (var existingAgent in agentProperty.Value.EnumerateObject())
                            {
                                writer.WritePropertyName(existingAgent.Name);
                                existingAgent.Value.WriteTo(writer);
                            }
                            
                            // Add new agent
                            writer.WritePropertyName(agentId);
                            writer.WriteStartObject();
                            writer.WriteString("Name", agentId);
                            if (!string.IsNullOrWhiteSpace(body.SystemPrompt))
                                writer.WriteString("SystemPrompt", body.SystemPrompt);
                            if (!string.IsNullOrWhiteSpace(body.SystemPromptFile))
                                writer.WriteString("SystemPromptFile", body.SystemPromptFile);
                            if (!string.IsNullOrWhiteSpace(body.Model))
                                writer.WriteString("Model", body.Model);
                            if (!string.IsNullOrWhiteSpace(body.Provider))
                                writer.WriteString("Provider", body.Provider);
                            if (body.MaxTokens.HasValue)
                                writer.WriteNumber("MaxTokens", body.MaxTokens.Value);
                            if (body.Temperature.HasValue)
                                writer.WriteNumber("Temperature", body.Temperature.Value);
                            if (body.MaxToolIterations.HasValue)
                                writer.WriteNumber("MaxToolIterations", body.MaxToolIterations.Value);
                            if (!string.IsNullOrWhiteSpace(body.Timezone))
                                writer.WriteString("Timezone", body.Timezone);
                            writer.WriteEndObject();
                            
                            writer.WriteEndObject();
                        }
                        else
                        {
                            writer.WritePropertyName(agentProperty.Name);
                            agentProperty.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WritePropertyName(bnProperty.Name);
                    bnProperty.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }
    writer.WriteEndObject();
    writer.Flush();

    var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
    await File.WriteAllTextAsync(configPath, updatedJson);

    // Bootstrap workspace
    var workspace = workspaceFactory.Create(agentId);
    await workspace.InitializeAsync();

    return Results.Json(new
    {
        name = agentId,
        systemPrompt = body.SystemPrompt,
        systemPromptFile = body.SystemPromptFile,
        model = body.Model ?? agentDefaults.Model,
        provider = body.Provider,
        maxTokens = body.MaxTokens ?? agentDefaults.MaxTokens,
        temperature = body.Temperature ?? agentDefaults.Temperature,
        maxToolIterations = body.MaxToolIterations ?? agentDefaults.MaxToolIterations,
        timezone = body.Timezone ?? agentDefaults.Timezone
    }, jsonOptions, statusCode: 201);
});

app.MapPut("/api/agents/{name}", async (string name, HttpContext httpContext, IOptions<BotNexusConfig> config) =>
{
    var agentDefaults = config.Value.Agents;
    if (!agentDefaults.Named.ContainsKey(name))
        return Results.Json(new { error = $"Agent '{name}' not found." }, jsonOptions, statusCode: 404);

    UpdateAgentRequest? body;
    try
    {
        body = await httpContext.Request.ReadFromJsonAsync<UpdateAgentRequest>(jsonOptions);
    }
    catch
    {
        return Results.Json(new { error = "Invalid JSON body." }, jsonOptions, statusCode: 400);
    }

    if (body is null)
        return Results.Json(new { error = "Request body is required." }, jsonOptions, statusCode: 400);

    var configPath = Path.Combine(botNexusHome, "config.json");
    var configJson = await File.ReadAllTextAsync(configPath);
    var configDoc = JsonDocument.Parse(configJson);
    var rootElement = configDoc.RootElement;

    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    
    writer.WriteStartObject();
    foreach (var property in rootElement.EnumerateObject())
    {
        if (property.Name == "BotNexus")
        {
            writer.WritePropertyName("BotNexus");
            writer.WriteStartObject();
            foreach (var bnProperty in property.Value.EnumerateObject())
            {
                if (bnProperty.Name == "Agents")
                {
                    writer.WritePropertyName("Agents");
                    writer.WriteStartObject();
                    foreach (var agentProperty in bnProperty.Value.EnumerateObject())
                    {
                        if (agentProperty.Name == "Named")
                        {
                            writer.WritePropertyName("Named");
                            writer.WriteStartObject();
                            
                            foreach (var existingAgent in agentProperty.Value.EnumerateObject())
                            {
                                writer.WritePropertyName(existingAgent.Name);
                                if (existingAgent.Name == name)
                                {
                                    // Update this agent
                                    writer.WriteStartObject();
                                    writer.WriteString("Name", name);
                                    if (body.SystemPrompt is not null)
                                        writer.WriteString("SystemPrompt", body.SystemPrompt);
                                    if (body.SystemPromptFile is not null)
                                        writer.WriteString("SystemPromptFile", body.SystemPromptFile);
                                    if (body.Model is not null)
                                        writer.WriteString("Model", body.Model);
                                    if (body.Provider is not null)
                                        writer.WriteString("Provider", body.Provider);
                                    if (body.MaxTokens.HasValue)
                                        writer.WriteNumber("MaxTokens", body.MaxTokens.Value);
                                    if (body.Temperature.HasValue)
                                        writer.WriteNumber("Temperature", body.Temperature.Value);
                                    if (body.MaxToolIterations.HasValue)
                                        writer.WriteNumber("MaxToolIterations", body.MaxToolIterations.Value);
                                    if (body.Timezone is not null)
                                        writer.WriteString("Timezone", body.Timezone);
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    existingAgent.Value.WriteTo(writer);
                                }
                            }
                            
                            writer.WriteEndObject();
                        }
                        else
                        {
                            writer.WritePropertyName(agentProperty.Name);
                            agentProperty.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WritePropertyName(bnProperty.Name);
                    bnProperty.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }
    writer.WriteEndObject();
    writer.Flush();

    var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
    await File.WriteAllTextAsync(configPath, updatedJson);

    return Results.Json(new
    {
        name,
        systemPrompt = body.SystemPrompt,
        systemPromptFile = body.SystemPromptFile,
        model = body.Model,
        provider = body.Provider,
        maxTokens = body.MaxTokens,
        temperature = body.Temperature,
        maxToolIterations = body.MaxToolIterations,
        timezone = body.Timezone
    }, jsonOptions);
});

app.MapDelete("/api/agents/{name}", async (string name, IOptions<BotNexusConfig> config) =>
{
    var agentDefaults = config.Value.Agents;
    if (!agentDefaults.Named.ContainsKey(name))
        return Results.Json(new { error = $"Agent '{name}' not found." }, jsonOptions, statusCode: 404);

    var configPath = Path.Combine(botNexusHome, "config.json");
    var configJson = await File.ReadAllTextAsync(configPath);
    var configDoc = JsonDocument.Parse(configJson);
    var rootElement = configDoc.RootElement;

    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    
    writer.WriteStartObject();
    foreach (var property in rootElement.EnumerateObject())
    {
        if (property.Name == "BotNexus")
        {
            writer.WritePropertyName("BotNexus");
            writer.WriteStartObject();
            foreach (var bnProperty in property.Value.EnumerateObject())
            {
                if (bnProperty.Name == "Agents")
                {
                    writer.WritePropertyName("Agents");
                    writer.WriteStartObject();
                    foreach (var agentProperty in bnProperty.Value.EnumerateObject())
                    {
                        if (agentProperty.Name == "Named")
                        {
                            writer.WritePropertyName("Named");
                            writer.WriteStartObject();
                            
                            foreach (var existingAgent in agentProperty.Value.EnumerateObject())
                            {
                                if (existingAgent.Name != name)
                                {
                                    writer.WritePropertyName(existingAgent.Name);
                                    existingAgent.Value.WriteTo(writer);
                                }
                            }
                            
                            writer.WriteEndObject();
                        }
                        else
                        {
                            writer.WritePropertyName(agentProperty.Name);
                            agentProperty.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WritePropertyName(bnProperty.Name);
                    bnProperty.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }
    writer.WriteEndObject();
    writer.Flush();

    var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
    await File.WriteAllTextAsync(configPath, updatedJson);

    return Results.Json(new { name, deleted = true }, jsonOptions);
});

// Local helper function for agent ID normalization
static string NormalizeAgentId(string agentName)
{
    if (string.IsNullOrWhiteSpace(agentName))
        return string.Empty;

    var normalized = agentName.Trim().ToLowerInvariant();
    normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
    normalized = normalized.Trim('-');
    normalized = Regex.Replace(normalized, @"-+", "-");

    return normalized;
}

// --- REST API: Providers ---
app.MapGet("/api/providers", async (ProviderRegistry providerRegistry) =>
{
    var names = providerRegistry.GetProviderNames();
    var providers = new List<object>();
    
    foreach (var name in names)
    {
        var provider = providerRegistry.Get(name);
        if (provider is null)
            continue;
            
        IReadOnlyList<string> availableModels;
        try
        {
            availableModels = await provider.GetAvailableModelsAsync();
        }
        catch (Exception)
        {
            availableModels = new[] { provider.DefaultModel };
        }
        
        providers.Add(new
        {
            name,
            defaultModel = provider.DefaultModel,
            model = provider.Generation.Model,
            maxTokens = provider.Generation.MaxTokens,
            temperature = provider.Generation.Temperature,
            availableModels = availableModels
        });
    }
    
    return Results.Json(providers, jsonOptions);
});

// --- REST API: Models ---
app.MapGet("/api/models", async (ProviderRegistry providerRegistry) =>
{
    var names = providerRegistry.GetProviderNames();
    var result = new List<object>();
    
    foreach (var name in names)
    {
        var provider = providerRegistry.Get(name);
        if (provider is null)
            continue;
            
        IReadOnlyList<string> models;
        try
        {
            models = await provider.GetAvailableModelsAsync();
        }
        catch (Exception)
        {
            models = new[] { provider.DefaultModel };
        }
        
        result.Add(new
        {
            provider = name,
            models = models
        });
    }
    
    return Results.Json(result, jsonOptions);
});

// --- REST API: Tools ---
app.MapGet("/api/tools", (IEnumerable<ITool> tools) =>
{
    var toolList = tools.Select(t => new
    {
        name = t.Definition.Name,
        description = t.Definition.Description,
        parameterCount = t.Definition.Parameters.Count
    });
    return Results.Json(toolList, jsonOptions);
});

// --- REST API: Skills ---
app.MapGet("/api/skills", async (IOptions<BotNexusConfig> config, ILoggerFactory loggerFactory) =>
{
    var skillsLoader = new SkillsLoader(botNexusHome, config, loggerFactory.CreateLogger<SkillsLoader>());
    var globalSkills = await skillsLoader.LoadSkillsAsync("_global", CancellationToken.None);
    
    return Results.Json(globalSkills.Select(s => new
    {
        name = s.Name,
        description = s.Description,
        version = s.Version,
        scope = s.Scope.ToString(),
        alwaysLoad = s.AlwaysLoad,
        sourcePath = s.SourcePath
    }), jsonOptions);
});

app.MapGet("/api/agents/{name}/skills", async (string name, IOptions<BotNexusConfig> config, ILoggerFactory loggerFactory) =>
{
    var agentDefaults = config.Value.Agents;
    if (!agentDefaults.Named.ContainsKey(name))
        return Results.Json(new { error = $"Agent '{name}' not found." }, jsonOptions, statusCode: 404);
    
    var skillsLoader = new SkillsLoader(botNexusHome, config, loggerFactory.CreateLogger<SkillsLoader>());
    var skills = await skillsLoader.LoadSkillsAsync(name, CancellationToken.None);
    
    return Results.Json(skills.Select(s => new
    {
        name = s.Name,
        description = s.Description,
        version = s.Version,
        scope = s.Scope.ToString(),
        alwaysLoad = s.AlwaysLoad,
        sourcePath = s.SourcePath,
        contentPreview = s.Content.Length > 200 ? s.Content[..200] + "..." : s.Content
    }), jsonOptions);
});

// --- REST API: Cron ---
app.MapGet("/api/cron", (ICronService cronService) =>
{
    var jobs = cronService.GetJobs().Select(j => new
    {
        name = j.Name,
        type = j.Type,
        schedule = j.Schedule,
        enabled = j.Enabled,
        lastRun = j.LastRunStartedAt,
        nextRun = j.NextOccurrence,
        lastResult = j.LastRunSuccess switch
        {
            true => "success",
            false => "failure",
            null => (string?)null
        }
    });
    return Results.Json(jobs, jsonOptions);
});

app.MapGet("/api/cron/history", (ICronService cronService, int? limit) =>
{
    var cap = Math.Clamp(limit ?? 50, 1, 500);
    var allHistory = cronService.GetJobs()
        .SelectMany(j => cronService.GetHistory(j.Name, cap))
        .OrderByDescending(e => e.StartedAt)
        .Take(cap)
        .Select(e => new
        {
            jobName = e.JobName,
            correlationId = e.CorrelationId,
            startedAt = e.StartedAt,
            completedAt = e.CompletedAt,
            success = e.Success,
            output = e.Output,
            error = e.Error
        });
    return Results.Json(allHistory, jsonOptions);
});

app.MapGet("/api/cron/{name}", (string name, ICronService cronService) =>
{
    var job = cronService.GetJobs().FirstOrDefault(j =>
        string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
    if (job is null)
        return Results.Json(new { error = $"Cron job '{name}' not found." }, jsonOptions, statusCode: 404);

    var history = cronService.GetHistory(job.Name).Select(e => new
    {
        correlationId = e.CorrelationId,
        startedAt = e.StartedAt,
        completedAt = e.CompletedAt,
        success = e.Success,
        output = e.Output,
        error = e.Error
    });

    return Results.Json(new
    {
        name = job.Name,
        type = job.Type,
        schedule = job.Schedule,
        enabled = job.Enabled,
        lastRun = job.LastRunStartedAt,
        nextRun = job.NextOccurrence,
        lastResult = job.LastRunSuccess switch
        {
            true => "success",
            false => "failure",
            null => (string?)null
        },
        history
    }, jsonOptions);
});

app.MapPost("/api/cron/{name}/trigger", async (string name, ICronService cronService) =>
{
    var job = cronService.GetJobs().FirstOrDefault(j =>
        string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
    if (job is null)
        return Results.Json(new { error = $"Cron job '{name}' not found." }, jsonOptions, statusCode: 404);

    await cronService.TriggerAsync(job.Name);
    return Results.Json(new { triggered = true, jobName = job.Name }, jsonOptions);
});

app.MapPut("/api/cron/{name}/enable", async (string name, HttpContext httpContext, ICronService cronService) =>
{
    var job = cronService.GetJobs().FirstOrDefault(j =>
        string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
    if (job is null)
        return Results.Json(new { error = $"Cron job '{name}' not found." }, jsonOptions, statusCode: 404);

    EnableRequest? body;
    try
    {
        body = await httpContext.Request.ReadFromJsonAsync<EnableRequest>(jsonOptions);
    }
    catch
    {
        return Results.Json(new { error = "Invalid JSON body. Expected { \"enabled\": true/false }." },
            jsonOptions, statusCode: 400);
    }

    if (body is null)
        return Results.Json(new { error = "Request body is required. Expected { \"enabled\": true/false }." },
            jsonOptions, statusCode: 400);

    cronService.SetEnabled(job.Name, body.Enabled);
    return Results.Json(new { jobName = job.Name, enabled = body.Enabled }, jsonOptions);
});

// --- REST API: Extensions summary ---
app.MapGet("/api/extensions", (
    ExtensionLoadReport report,
    ChannelManager channelManager,
    ProviderRegistry providerRegistry,
    IEnumerable<ITool> tools) =>
{
    return Results.Json(new
    {
        loaded = report.LoadedCount,
        failed = report.FailedCount,
        warnings = report.WarningCount,
        completed = report.Completed,
        healthy = report.CompletedSuccessfully,
        channels = channelManager.Channels.Count,
        providers = providerRegistry.GetProviderNames().Count,
        tools = tools.Count(),
        results = report.Results.Select(r => new
        {
            type = r.Type,
            key = r.Key,
            success = r.Success,
            message = r.Message,
            version = r.Version
        })
    }, jsonOptions);
});

// --- REST API: Status ---
app.MapGet("/api/status", async (
    HealthCheckService healthCheckService,
    ExtensionLoadReport extensionReport,
    ChannelManager channelManager,
    ProviderRegistry providerRegistry,
    IEnumerable<ITool> tools,
    IOptions<BotNexusConfig> config,
    ICronService cronService,
    ISessionManager sessionManager) =>
{
    var healthReport = await healthCheckService.CheckHealthAsync();
    var sessionKeys = await sessionManager.ListKeysAsync();
    var cronJobs = cronService.GetJobs();
    var namedAgents = config.Value.Agents.Named;
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    // Detect memory consolidation from maintenance cron jobs
    var consolidationJob = cronJobs.FirstOrDefault(j =>
        j.Type == CronJobType.Maintenance &&
        j.Name.Contains("consolidat", StringComparison.OrdinalIgnoreCase));

    return Results.Json(new
    {
        gateway = new
        {
            version,
            startedAt,
            uptime = DateTimeOffset.UtcNow - startedAt
        },
        health = new
        {
            status = healthReport.Status.ToString(),
            checks = healthReport.Entries.Count,
            healthy = healthReport.Entries.Count(e => e.Value.Status == HealthStatus.Healthy),
            degraded = healthReport.Entries.Count(e => e.Value.Status == HealthStatus.Degraded),
            unhealthy = healthReport.Entries.Count(e => e.Value.Status == HealthStatus.Unhealthy)
        },
        extensions = new
        {
            loaded = extensionReport.LoadedCount,
            providers = providerRegistry.GetProviderNames().Count,
            channels = channelManager.Channels.Count,
            tools = tools.Count()
        },
        agents = new
        {
            configured = 1 + namedAgents.Count  // default + named
        },
        cron = new
        {
            registered = cronJobs.Count,
            enabled = cronJobs.Count(j => j.Enabled),
            running = cronService.IsRunning
        },
        sessions = new
        {
            active = sessionKeys.Count
        },
        memory = new
        {
            consolidationConfigured = consolidationJob is not null,
            consolidationEnabled = consolidationJob?.Enabled,
            lastConsolidation = consolidationJob?.LastRunStartedAt,
            lastConsolidationSuccess = consolidationJob?.LastRunSuccess
        }
    }, jsonOptions);
});

// --- REST API: Doctor ---
app.MapGet("/api/doctor", async (CheckupRunner checkupRunner, string? category) =>
{
    var results = await checkupRunner.RunAndFixAsync(
        category: category,
        force: false,
        promptUser: null);

    var passed = results.Count(r => r.Result.Status == CheckupStatus.Pass);
    var warnings = results.Count(r => r.Result.Status == CheckupStatus.Warn);
    var failed = results.Count(r => r.Result.Status == CheckupStatus.Fail);

    return Results.Json(new
    {
        summary = new { passed, warnings, failed },
        results = results.Select(r => new
        {
            name = r.Checkup.Name,
            category = r.Checkup.Category,
            status = r.Result.Status,
            message = r.Result.Message,
            advice = r.Result.Advice,
            canAutoFix = r.Checkup.CanAutoFix
        })
    }, jsonOptions);
});

// --- REST API: Shutdown ---
app.MapPost("/api/shutdown", (
    HttpContext httpContext,
    IHostApplicationLifetime lifetime,
    ILogger<Program> logger) =>
{
    ShutdownRequest? body = null;
    try
    {
        body = httpContext.Request.ContentLength > 0
            ? httpContext.Request.ReadFromJsonAsync<ShutdownRequest>(jsonOptions).GetAwaiter().GetResult()
            : null;
    }
    catch
    {
        // Invalid JSON is acceptable — reason is optional
    }

    var reason = body?.Reason ?? "No reason provided";
    logger.LogWarning("Shutdown requested via API. Reason: {Reason}", reason);

    // Stop the application after the response is sent
    _ = Task.Run(async () =>
    {
        await Task.Delay(500);
        lifetime.StopApplication();
    });

    return Results.Json(new
    {
        accepted = true,
        message = "Shutdown initiated",
        reason
    }, jsonOptions, statusCode: 202);
});

// --- WebSocket endpoint ---
if (gatewayCfg.WebSocketEnabled)
{
    app.Map(webSocketPath, static wsApp =>
    {
        wsApp.UseWebSockets();
        wsApp.Run(static async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket upgrade required");
                return;
            }

            await ctx.RequestServices
                .GetRequiredService<GatewayWebSocketHandler>()
                .HandleAsync(ctx, ctx.RequestAborted);
        });
    });
}

foreach (var webhookHandler in app.Services.GetServices<IWebhookHandler>())
{
    var path = webhookHandler.Path.StartsWith("/", StringComparison.Ordinal)
        ? webhookHandler.Path
        : $"/{webhookHandler.Path}";

    app.MapPost(path, async (HttpContext context) =>
    {
        var result = await webhookHandler.HandleAsync(context).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    });
}

try
{
    await app.RunAsync();
}
finally
{
    Log.CloseAndFlush();
}

// Expose Program for WebApplicationFactory<Program> in integration tests
public partial class Program { }

internal sealed record EnableRequest(bool Enabled);
internal sealed record ShutdownRequest(string? Reason);
internal sealed record CreateAgentRequest(
    string Name,
    string? SystemPrompt = null,
    string? SystemPromptFile = null,
    string? Model = null,
    string? Provider = null,
    int? MaxTokens = null,
    double? Temperature = null,
    int? MaxToolIterations = null,
    string? Timezone = null);
internal sealed record UpdateAgentRequest(
    string? SystemPrompt = null,
    string? SystemPromptFile = null,
    string? Model = null,
    string? Provider = null,
    int? MaxTokens = null,
    double? Temperature = null,
    int? MaxToolIterations = null,
    string? Timezone = null);

