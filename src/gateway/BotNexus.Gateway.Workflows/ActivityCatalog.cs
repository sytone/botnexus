using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Workflows;

namespace BotNexus.Gateway.Workflows;

/// <summary>Only explicitly registered application activities can execute. No reflection or authored CLR type loading.</summary>
/// <remarks>
/// Registered scoped: the execution scope owns the injected activities and authorizer, including disposal.
/// The registration snapshot is read-only after construction, but invocations are not serialized; dependencies must
/// support the concurrency they receive. The catalog never owns or disposes supplied dependencies or JSON documents.
/// </remarks>
public sealed class ActivityCatalog : IActivityCatalog
{
    private readonly Dictionary<(string Name, int Version), Registration> _activities = [];
    private readonly IActivityAuthorizer _authorizer;

    /// <summary>Captures an immutable allowlist from activities resolved in the owning execution scope.</summary>
    /// <param name="activities">Explicit code registrations with stable descriptors, not authored CLR type names.</param>
    /// <param name="authorizer">Policy to evaluate on every invocation, including resumed engine steps.</param>
    /// <exception cref="ArgumentNullException">The activity collection, an entry, or the authorizer is null.</exception>
    /// <exception cref="ArgumentException">A descriptor is missing or a name/version pair is duplicated.</exception>
    public ActivityCatalog(IEnumerable<IWorkflowActivity> activities, IActivityAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(authorizer);
        _authorizer = authorizer;
        foreach (var activity in activities)
        {
            ArgumentNullException.ThrowIfNull(activity);
            var descriptor = activity.Descriptor ?? throw new ArgumentException("An activity must declare its contract.", nameof(activities));
            if (!_activities.TryAdd((descriptor.Name, descriptor.Version), new(activity, descriptor)))
                throw new ArgumentException($"Duplicate activity {descriptor.Name} v{descriptor.Version}.", nameof(activities));
        }
        Activities = new ReadOnlyCollection<ActivityDescriptor>(_activities.Values.Select(value => value.Descriptor)
            .OrderBy(value => value.Name, StringComparer.Ordinal).ThenBy(value => value.Version).ToArray());
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<ActivityDescriptor> Activities { get; }

    /// <inheritdoc/>
    public async ValueTask<JsonElement> InvokeAsync(string name, int version, ActivityInvocationContext context,
        JsonElement input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        // Validate identity even when a caller invokes directly, outside the engine bridge.
        _ = new ActivityDescriptor(name, version);
        if (!_activities.TryGetValue((name, version), out var registration))
            throw new KeyNotFoundException($"Unknown activity {name} v{version}.");
        var descriptor = registration.Descriptor;
        if (!await _authorizer.AuthorizeAsync(descriptor, context, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException($"Activity {name} v{version} is not authorized.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateJson(input, descriptor.MaxInputBytes, descriptor.InputKind, "input");
        var output = await registration.Activity.InvokeAsync(context, input.Clone(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateJson(output, descriptor.MaxOutputBytes, descriptor.OutputKind, "output");
        return output.Clone();
    }

    private static void ValidateJson(JsonElement value, int maxBytes, JsonValueKind? kind, string parameter)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Activity JSON must not be undefined.", parameter);
        if (kind is { } expected && value.ValueKind != expected)
            throw new ArgumentException($"Activity {parameter} must have JSON kind {expected}.", parameter);
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > maxBytes)
            throw new ArgumentException($"Activity {parameter} exceeds its {maxBytes}-byte contract.", parameter);
    }

    private sealed record Registration(IWorkflowActivity Activity, ActivityDescriptor Descriptor);
}

/// <summary>Stateless, thread-safe singleton default policy that denies every activity invocation.</summary>
public sealed class DenyActivityAuthorizer : IActivityAuthorizer
{
    /// <inheritdoc/>
    public ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

/// <summary>Stateless, thread-safe singleton default resolver with no trusted identity bindings.</summary>
public sealed class DenyWorkflowInvocationContextResolver : IWorkflowInvocationContextResolver
{
    /// <inheritdoc/>
    public ValueTask<ActivityInvocationContext?> ResolveAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ActivityInvocationContext?>(null);
    }
}
