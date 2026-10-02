using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Text;
using BotNexus.Gateway.A2A;
using BotNexus.Gateway.Abstractions.A2A;

namespace BotNexus.Extensions.A2A;

/// <summary>Delegates one objective to an allowed A2A service profile and returns one compact terminal result.</summary>
public sealed class DelegateA2ATaskTool : IAgentTool, IDisposable
{
    private const int DefaultDeadlineSeconds = 60;
    private const int MaximumDeadlineSeconds = 600;
    private const int MaximumObjectiveCharacters = 32 * 1024;
    private const int MaximumSupportingContextCharacters = 32 * 1024;
    private const int MaximumAcceptanceCriteriaCharacters = 8 * 1024;
    private const int MaximumOutputShapeCharacters = 4 * 1024;
    private const int MaximumApprovalBoundaryCharacters = 4 * 1024;
    private const int MaximumAllowedActions = 32;
    private const int MaximumAllowedActionCharacters = 256;
    private const int MaximumPolicyReasonCharacters = 1024;
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
                "supportingContext": {
                  "type": "string",
                  "maxLength": 32768,
                  "description": "Bounded context needed to complete the objective; omit unrelated or protected content."
                },
                "acceptanceCriteria": {
                  "type": "string",
                  "maxLength": 8192,
                  "description": "Observable conditions the remote result must satisfy."
                },
                "requestedOutputShape": {
                  "type": "string",
                  "maxLength": 4096,
                  "description": "Requested structure for the remote result."
                },
                "allowedActions": {
                  "type": "array",
                  "maxItems": 32,
                  "items": { "type": "string", "maxLength": 256 },
                  "description": "Explicit actions the remote agent may perform. Empty means no consequential actions are granted."
                },
                "approvalBoundary": {
                  "type": "string",
                  "maxLength": 4096,
                  "description": "Required approvals and authority the remote agent must not broaden."
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
        var objective = ReadBoundedString(arguments, "objective", MaximumObjectiveCharacters, required: true);
        var supportingContext = ReadBoundedString(arguments, "supportingContext", MaximumSupportingContextCharacters);
        var acceptanceCriteria = ReadBoundedString(arguments, "acceptanceCriteria", MaximumAcceptanceCriteriaCharacters);
        var requestedOutputShape = ReadBoundedString(arguments, "requestedOutputShape", MaximumOutputShapeCharacters);
        var approvalBoundary = ReadBoundedString(arguments, "approvalBoundary", MaximumApprovalBoundaryCharacters) ?? string.Empty;
        var allowedActions = ReadStringList(arguments, "allowedActions", MaximumAllowedActions, MaximumAllowedActionCharacters);
        var contextId = ReadString(arguments, "contextId");
        var deadlineSeconds = ReadInt(arguments, "deadlineSeconds") ?? DefaultDeadlineSeconds;
        if (deadlineSeconds is < 1 or > MaximumDeadlineSeconds)
            throw new ArgumentOutOfRangeException(nameof(arguments), "deadlineSeconds must be between 1 and 600.");

        IReadOnlyDictionary<string, object?> prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profileId"] = profileId.Trim(),
            ["objective"] = objective,
            ["supportingContext"] = supportingContext,
            ["acceptanceCriteria"] = acceptanceCriteria,
            ["requestedOutputShape"] = requestedOutputShape,
            ["allowedActions"] = allowedActions,
            ["approvalBoundary"] = approvalBoundary,
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

        var objective = (string)arguments["objective"]!;
        var allowedActions = (IReadOnlyList<string>)arguments["allowedActions"]!;
        var approvalBoundary = (string)arguments["approvalBoundary"]!;
        var authorization = await profile.AuthorizeAsync(
            new A2ADelegationAuthorizationRequest(objective, allowedActions, approvalBoundary),
            cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed)
        {
            var denied = new A2ATaskResult(
                A2ATerminalOutcome.PolicyBlocked,
                arguments["contextId"] as string,
                null,
                null,
                [],
                null,
                null,
                BoundPolicyReason(authorization.Reason));
            return SerializeResult(denied);
        }

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
            new A2AMessage(BuildAssignment(arguments), arguments["contextId"] as string),
            deadline,
            cancellationToken).ConfigureAwait(false);

        return SerializeResult(result);
    }

    /// <summary>Releases the per-handle A2A client transport exactly once.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        client.Dispose();
    }

    private static AgentToolResult SerializeResult(A2ATaskResult result)
    {
        var json = JsonSerializer.Serialize(result, ResultJson);
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, json)]);
    }

    private static string BuildAssignment(IReadOnlyDictionary<string, object?> arguments)
    {
        var sections = new List<string>
        {
            $"Objective:\n{arguments["objective"]}"
        };
        AddSection(sections, "Supporting context", arguments["supportingContext"] as string);
        AddSection(sections, "Acceptance criteria", arguments["acceptanceCriteria"] as string);
        AddSection(sections, "Requested output shape", arguments["requestedOutputShape"] as string);
        var allowedActions = (IReadOnlyList<string>)arguments["allowedActions"]!;
        if (allowedActions.Count > 0)
            sections.Add($"Allowed actions:\n- {string.Join("\n- ", allowedActions)}");
        AddSection(sections, "Approval boundary", arguments["approvalBoundary"] as string);
        return string.Join("\n\n", sections);
    }

    private static void AddSection(List<string> sections, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sections.Add($"{label}:\n{value}");
    }

    private static string ReadBoundedString(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        int maximumCharacters,
        bool required = false)
    {
        var value = ReadString(arguments, key)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                throw new ArgumentException($"{key} is required.");
            return string.Empty;
        }
        if (value.Length > maximumCharacters)
            throw new ArgumentException($"{key} must not exceed {maximumCharacters} characters.");
        return value;
    }

    private static IReadOnlyList<string> ReadStringList(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        int maximumItems,
        int maximumItemCharacters)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
            return [];
        IEnumerable<string> values = value switch
        {
            IEnumerable<string> strings => strings,
            JsonElement { ValueKind: JsonValueKind.Array } element => ReadJsonStringArray(element, key),
            _ => throw new ArgumentException($"{key} must be an array of strings.")
        };
        var result = values.Select(static item => item.Trim()).Where(static item => item.Length > 0).ToList();
        if (result.Count > maximumItems)
            throw new ArgumentException($"{key} must not contain more than {maximumItems} items.");
        if (result.Any(item => item.Length > maximumItemCharacters))
            throw new ArgumentException($"Each {key} item must not exceed {maximumItemCharacters} characters.");
        return result;
    }

    private static IEnumerable<string> ReadJsonStringArray(JsonElement element, string key)
    {
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{key} must contain only strings.");
            yield return item.GetString() ?? string.Empty;
        }
    }

    private static string BoundPolicyReason(string? reason)
    {
        const string defaultReason = "The A2A service profile denied this delegation.";
        if (string.IsNullOrWhiteSpace(reason))
            return defaultReason;
        var trimmed = reason.Trim();
        return trimmed.SafeTruncate(MaximumPolicyReasonCharacters) ?? defaultReason;
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
