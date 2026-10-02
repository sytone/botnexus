using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.A2A;
using BotNexus.Gateway.Abstractions.A2A;

namespace BotNexus.Extensions.A2A;

/// <summary>Delegates one objective to an allowed A2A service profile and returns one compact terminal result.</summary>
public sealed class DelegateA2ATaskTool : IAgentTool, IDisposable
{
    private const int DefaultDeadlineSeconds = 60;
    private const int MaximumDeadlineSeconds = 600;
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly IReadOnlyDictionary<string, IA2AServiceProfile> profiles;
    private readonly IA2AClient client;
    private bool disposed;

    internal DelegateA2ATaskTool(IEnumerable<IA2AServiceProfile> profiles, IA2AClientFactory clientFactory)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(clientFactory);
        this.profiles = profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        client = clientFactory.Create();
    }

    /// <inheritdoc />
    public string Name => "delegate_a2a_task";

    /// <inheritdoc />
    public string Label => "Delegate A2A Task";

    /// <summary>Remote agent results are foreign, attacker-influenceable content.</summary>
    public string ContentSource => ToolContentSource.Untrusted;

    /// <summary>Keeps the agent loop budget wide enough for the default remote deadline.</summary>
    public TimeSpan? DefaultTimeout => TimeSpan.FromSeconds(DefaultDeadlineSeconds);

    /// <summary>Declares the caller-selected deadline and its unit to the central tool executor.</summary>
    public ToolTimeoutArgument? TimeoutArgument => new("deadlineSeconds", ToolTimeoutUnit.Seconds);

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Delegate one bounded objective to an allowed remote A2A service profile and return its terminal result.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "profileId": {
                  "type": "string",
                  "description": "Configured A2A service profile ID."
                },
                "objective": {
                  "type": "string",
                  "description": "The bounded task for the remote agent."
                },
                "contextId": {
                  "type": "string",
                  "description": "Optional opaque A2A context ID from an earlier terminal result."
                },
                "deadlineSeconds": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 600,
                  "description": "Absolute call budget in seconds. Default: 60; maximum: 600."
                }
              },
              "required": ["profileId", "objective"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileId = ReadString(arguments, "profileId");
        if (string.IsNullOrWhiteSpace(profileId) || !profiles.ContainsKey(profileId))
            throw new ArgumentException("profileId must name an allowed, ready A2A service profile.");
        var objective = ReadString(arguments, "objective");
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("objective is required.");
        var contextId = ReadString(arguments, "contextId");
        var deadlineSeconds = ReadInt(arguments, "deadlineSeconds") ?? DefaultDeadlineSeconds;
        if (deadlineSeconds is < 1 or > MaximumDeadlineSeconds)
            throw new ArgumentOutOfRangeException(nameof(arguments), "deadlineSeconds must be between 1 and 600.");

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profileId"] = profileId.Trim(),
            ["objective"] = objective,
            ["contextId"] = contextId,
            ["deadlineSeconds"] = deadlineSeconds
        };
        return Task.FromResult(prepared);
    }

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var profileId = (string)arguments["profileId"]!;
        if (!profiles.TryGetValue(profileId, out var profile) || !profile.IsReady)
            throw new InvalidOperationException("The selected A2A service profile is no longer ready.");

        var connection = await profile.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var options = new A2AClientOptions(connection.ApprovedOrigin)
        {
            AdditionalBlockedHosts = connection.AdditionalBlockedHosts,
            AuthenticateDiscoveryAsync = connection.AuthenticateDiscoveryAsync is null
                ? null
                : (request, token) => connection.AuthenticateDiscoveryAsync(request, token),
            AuthenticateSubmissionAsync = connection.AuthenticateSubmissionAsync is null
                ? null
                : (request, token) => connection.AuthenticateSubmissionAsync(request, token)
        };
        var deadline = DateTimeOffset.UtcNow.AddSeconds((int)arguments["deadlineSeconds"]!);
        var result = await client.SendMessageAsync(
            options,
            new A2AMessage((string)arguments["objective"]!, arguments["contextId"] as string),
            deadline,
            cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(result, ResultJson);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, json)]);
    }

    /// <summary>Releases the per-handle A2A client transport exactly once.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        client.Dispose();
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            _ => null
        };
    }
}
